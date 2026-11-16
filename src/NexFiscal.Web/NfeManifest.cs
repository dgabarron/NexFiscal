using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NexFiscal.Web;

/// <summary>Resposta da SEFAZ para um evento do lote.</summary>
public sealed record ManifestEventResult(string AccessKey, int Status, string Reason, string? Protocol, DateTime? RegisteredAtUtc);

/// <summary>Resposta retEnvEvento. <see cref="Status"/> 128 = lote processado; o resultado de cada nota vem em <see cref="Events"/>.</summary>
public sealed record ManifestLotResult(int Status, string Reason, IReadOnlyList<ManifestEventResult> Events);

/// <summary>
/// Evento "Ciência da Operação" (210210) da Manifestação do Destinatário, conforme a NT 2020.001 v1.60
/// e o pacote de schemas Evento_ManifestaDest_PL_v1.01. Somente montagem, assinatura e leitura de XML: não acessa rede.
/// A assinatura usa a implementação XML-DSig do próprio .NET (System.Security.Cryptography.Xml).
/// </summary>
public static class NfeManifestProtocol
{
    /// <summary>Portal Nacional da NF-e, "Relação de Serviços Web", Ambiente Nacional: RecepcaoEvento 4.00.</summary>
    public static readonly Uri ProductionUrl = new("https://www.nfe.fazenda.gov.br/NFeRecepcaoEvento4/NFeRecepcaoEvento4.asmx");
    public const string SoapAction = "http://www.portalfiscal.inf.br/nfe/wsdl/NFeRecepcaoEvento4/nfeRecepcaoEventoNF";

    public const int AwarenessEvent = 210210;
    public const int MaxEventsPerLot = 20;
    public const int StatusLotProcessed = 128;
    public const int StatusRegistered = 135, StatusRegisteredNotLinked = 136;
    public const int StatusDuplicate = 573, StatusAfterFinalManifestation = 655;
    public const int StatusOutOfDeadline = 596, StatusCancelledOrDenied = 650;

    private const string NfeNs = "http://www.portalfiscal.inf.br/nfe";
    private const string WsNs = "http://www.portalfiscal.inf.br/nfe/wsdl/NFeRecepcaoEvento4";
    private static readonly XNamespace Nfe = NfeNs;
    // O leiaute do evento (TChNFe / TCnpj do pacote v1.01) só aceita chave e CNPJ numéricos.
    private static readonly Regex NumericKey = new("^[0-9]{44}$", RegexOptions.Compiled);
    private static readonly Regex NumericCnpj = new("^[0-9]{14}$", RegexOptions.Compiled);
    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);

    public static bool CanManifest(string? accessKey) => accessKey is not null && NumericKey.IsMatch(accessKey);
    public static bool CanAuthor(string? cnpj) => cnpj is not null && NumericCnpj.IsMatch(cnpj);

    /// <summary>Elemento &lt;evento&gt; de Ciência da Operação, já assinado com o A1 da empresa.</summary>
    public static string BuildSignedAwareness(string cnpj, string accessKey, DateTimeOffset when, X509Certificate2 certificate)
    {
        if (!CanAuthor(cnpj)) throw new ArgumentException("CNPJ inválido para o evento.", nameof(cnpj));
        if (!CanManifest(accessKey)) throw new ArgumentException("Chave de acesso inválida para o evento.", nameof(accessKey));
        var id = "ID" + AwarenessEvent.ToString(CultureInfo.InvariantCulture) + accessKey + "01";
        var moment = when.ToOffset(Brasilia).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        // Sem espaços nem quebras: o que é assinado é exatamente o que vai para a SEFAZ.
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        document.LoadXml(
            $"<evento xmlns=\"{NfeNs}\" versao=\"1.00\"><infEvento Id=\"{id}\"><cOrgao>91</cOrgao>" +
            $"<tpAmb>{CompanyService.ProductionEnvironment}</tpAmb><CNPJ>{cnpj}</CNPJ><chNFe>{accessKey}</chNFe>" +
            $"<dhEvento>{moment}</dhEvento><tpEvento>{AwarenessEvent}</tpEvento><nSeqEvento>1</nSeqEvento><verEvento>1.00</verEvento>" +
            "<detEvento versao=\"1.00\"><descEvento>Ciencia da Operacao</descEvento></detEvento></infEvento></evento>");

        using var key = certificate.GetRSAPrivateKey()
            ?? throw new NexFiscalException("certificate_unavailable",
                "O certificado A1 desta empresa não tem chave privada RSA utilizável para assinar. Use \"Atualizar A1\" na seção Empresas.", 409);
        // Algoritmos fixados pelo schema oficial da assinatura (xmldsig-core-schema_v1.01): C14N, RSA-SHA1 e SHA-1.
        var signed = new SignedXml(document) { SigningKey = key };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA1Url;
        var reference = new Reference("#" + id) { DigestMethod = SignedXml.XmlDsigSHA1Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigC14NTransform());
        signed.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate)); // só o certificado público
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();
        document.DocumentElement!.AppendChild(document.ImportNode(signed.GetXml(), true));
        return document.OuterXml;
    }

    /// <summary>Envelope SOAP 1.2 com o lote (envEvento) de até 20 eventos já assinados.</summary>
    public static string BuildEnvelope(long lotId, IReadOnlyList<string> signedEvents)
    {
        if (lotId is < 1 or > 999_999_999_999_999L) throw new ArgumentOutOfRangeException(nameof(lotId));
        if (signedEvents.Count is < 1 or > MaxEventsPerLot) throw new ArgumentOutOfRangeException(nameof(signedEvents));
        // Concatenação de texto: reserializar os eventos poderia alterar o que foi assinado.
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
               "<soap12:Envelope xmlns:soap12=\"http://www.w3.org/2003/05/soap-envelope\"><soap12:Body>" +
               $"<nfeDadosMsg xmlns=\"{WsNs}\"><envEvento xmlns=\"{NfeNs}\" versao=\"1.00\">" +
               $"<idLote>{lotId.ToString(CultureInfo.InvariantCulture)}</idLote>{string.Concat(signedEvents)}" +
               "</envEvento></nfeDadosMsg></soap12:Body></soap12:Envelope>";
    }

    /// <summary>Área de dados (envEvento) de um envelope, para validação contra o schema oficial.</summary>
    public static XElement DataOf(string soapEnvelope) =>
        XDocument.Parse(soapEnvelope, LoadOptions.PreserveWhitespace).Descendants(Nfe + "envEvento").Single();

    public static ManifestLotResult ParseResponse(string soap)
    {
        XDocument xml;
        try { xml = NfeProtocol.Load(soap); }
        catch (XmlException) { throw Invalid("resposta não é XML"); }

        if (xml.Descendants().Any(e => e.Name.LocalName == "Fault"))
            throw new NexFiscalException("sefaz_fault",
                "A SEFAZ devolveu um erro de serviço (SOAP Fault) ao receber a manifestação. Confira o resultado na tela antes de tentar de novo.", 502);
        var ret = xml.Descendants(Nfe + "retEnvEvento").FirstOrDefault() ?? throw Invalid("sem retEnvEvento");
        var events = new List<ManifestEventResult>();
        foreach (var info in ret.Elements(Nfe + "retEvento").Select(e => e.Element(Nfe + "infEvento")))
        {
            var key = (string?)info?.Element(Nfe + "chNFe");
            // Sem chave não há como atribuir o resultado a uma nota: fica como "sem resposta".
            if (info is null || !CanManifest(key)) continue;
            events.Add(new ManifestEventResult(key!, Status(info), Reason(info), Limit((string?)info.Element(Nfe + "nProt"), 20),
                DateTimeOffset.TryParse((string?)info.Element(Nfe + "dhRegEvento"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var registered) ? registered.UtcDateTime : null));
        }
        return new ManifestLotResult(Status(ret), Reason(ret), events);
    }

    private static int Status(XElement element) =>
        int.TryParse((string?)element.Element(Nfe + "cStat"), NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            ? status : throw Invalid("sem cStat");

    private static string Reason(XElement element) => Limit(((string?)element.Element(Nfe + "xMotivo") ?? "").Trim(), 500)!;
    private static string? Limit(string? text, int max) => text is not null && text.Length > max ? text[..max] : text;

    private static NexFiscalException Invalid(string detail) =>
        new("sefaz_invalid_response",
            $"A SEFAZ devolveu uma resposta que não pôde ser interpretada ({detail}). Confira o resultado na tela antes de tentar de novo.", 502);
}

/// <summary>
/// Registra a Ciência da Operação na SEFAZ para um lote de notas. É o ponto de troca:
/// uma biblioteca fiscal de terceiros pode substituir a implementação padrão sem alterar o restante.
/// </summary>
public interface IManifestGateway
{
    Task<ManifestLotResult> SendAwarenessAsync(
        string cnpj, IReadOnlyList<string> accessKeys, X509Certificate2 certificate, DateTimeOffset when, CancellationToken ct);
}

/// <summary>Implementação padrão: monta e assina com o .NET e envia pelo mesmo transporte HTTPS usado na captura.</summary>
public sealed class SefazManifestGateway(ISefazTransport transport) : IManifestGateway
{
    public async Task<ManifestLotResult> SendAwarenessAsync(
        string cnpj, IReadOnlyList<string> accessKeys, X509Certificate2 certificate, DateTimeOffset when, CancellationToken ct)
    {
        var events = accessKeys.Select(key => NfeManifestProtocol.BuildSignedAwareness(cnpj, key, when, certificate)).ToList();
        var envelope = NfeManifestProtocol.BuildEnvelope(when.ToUnixTimeMilliseconds(), events);
        var response = await transport.SendAsync(NfeManifestProtocol.ProductionUrl, NfeManifestProtocol.SoapAction, envelope, certificate, ct);
        return NfeManifestProtocol.ParseResponse(response);
    }
}

/// <summary>Última resposta da SEFAZ para o evento de uma nota (BRAI_NEX_NfeManifest).</summary>
public sealed record ManifestRow(
    string AccessKey, int EventType, string State, int? Status, string? Reason, string? Protocol, DateTime? RegisteredAtUtc, DateTime UpdatedAtUtc)
{
    public const string Registered = "registered", Rejected = "rejected";
}

/// <summary>Persistência da manifestação. A implementação real é o HANA; os testes usam uma em memória.</summary>
public interface IManifestStore
{
    Task<IReadOnlyList<ManifestRow>> ListAsync(string companyId, int eventType, CancellationToken ct);
    /// <summary>Grava a resposta da SEFAZ. Um evento já registrado nunca volta a "rejeitado".</summary>
    Task SaveAsync(string companyId, ManifestRow row, CancellationToken ct);
}

/// <summary>Todo o SQL da manifestação. Um teste confere cada identificador contra as migrações.</summary>
internal static class ManifestSql
{
    private const string T = "\"BRAI_NEX\".\"BRAI_NEX_NfeManifest\"";

    public const string List =
        "SELECT \"AccessKey\", \"EventType\", \"State\", \"Status\", \"Reason\", \"Protocol\", \"RegisteredAtUtc\", \"UpdatedAtUtc\" " +
        "FROM " + T + " WHERE \"CompanyId\" = ? AND \"EventType\" = ?";
    public const string Count =
        "SELECT COUNT(*) FROM " + T + " WHERE \"CompanyId\" = ? AND \"AccessKey\" = ? AND \"EventType\" = ?";
    public const string Insert =
        "INSERT INTO " + T + " (\"CompanyId\", \"AccessKey\", \"EventType\", \"State\", \"Status\", \"Reason\", \"Protocol\", " +
        "\"RegisteredAtUtc\", \"Attempts\", \"UpdatedAtUtc\") VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1, ?)";
    // "State" <> 'registered' garante que um registro aceito pela SEFAZ nunca é rebaixado por uma tentativa posterior.
    public const string Update =
        "UPDATE " + T + " SET \"State\" = ?, \"Status\" = ?, \"Reason\" = ?, \"Protocol\" = ?, \"RegisteredAtUtc\" = ?, " +
        "\"Attempts\" = \"Attempts\" + 1, \"UpdatedAtUtc\" = ? " +
        "WHERE \"CompanyId\" = ? AND \"AccessKey\" = ? AND \"EventType\" = ? AND \"State\" <> 'registered'";
}

/// <summary>Manifestação no SAP HANA, usando a conexão salva na Função 1. Nunca apaga nada.</summary>
public sealed class HanaManifestStore(
    HanaCredentialStore credentialStore, IHanaConnectionFactory connections, ILogger<HanaManifestStore> logger)
    : IManifestStore
{
    public Task<IReadOnlyList<ManifestRow>> ListAsync(string companyId, int eventType, CancellationToken ct) =>
        WithConnectionAsync<IReadOnlyList<ManifestRow>>(async db =>
        {
            var list = new List<ManifestRow>();
            await using var cmd = HanaCompanyStore.Command(db, null, ManifestSql.List, companyId, eventType);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(new ManifestRow(reader.GetString(0), Convert.ToInt32(reader.GetValue(1)), reader.GetString(2),
                    reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3)), Text(reader, 4), Text(reader, 5), Utc(reader, 6), Utc(reader, 7)!.Value));
            return list;
        }, ct);

    public Task SaveAsync(string companyId, ManifestRow row, CancellationToken ct) => WithConnectionAsync(async db =>
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            int existing;
            await using (var count = HanaCompanyStore.Command(db, tx, ManifestSql.Count, companyId, row.AccessKey, row.EventType))
                existing = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
            await using (var write = existing == 0
                ? HanaCompanyStore.Command(db, tx, ManifestSql.Insert, companyId, row.AccessKey, row.EventType, row.State,
                    TypedNull.Int(row.Status), row.Reason, row.Protocol, TypedNull.Time(row.RegisteredAtUtc), row.UpdatedAtUtc)
                : HanaCompanyStore.Command(db, tx, ManifestSql.Update, row.State, TypedNull.Int(row.Status), row.Reason, row.Protocol,
                    TypedNull.Time(row.RegisteredAtUtc), row.UpdatedAtUtc, companyId, row.AccessKey, row.EventType))
                await write.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { /* conexão já perdida */ }
            throw;
        }
    }, ct);

    private async Task<T> WithConnectionAsync<T>(Func<DbConnection, Task<T>> work, CancellationToken ct)
    {
        var (state, credentials) = credentialStore.Read();
        if (state != StoredConfigState.Readable)
            throw new NexFiscalException("hana_not_configured", "Configure e valide a conexão SAP HANA antes de registrar a manifestação.", 409);
        try
        {
            await using var db = connections.Create(credentials!);
            await db.OpenAsync(ct);
            return await work(db);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or NexFiscalException))
        {
            var failure = HanaFailure.Translate(ex, credentials!, saving: false);
            logger.LogWarning("Operação de manifestação no HANA falhou: {Type} / {Kind} / código {Code}",
                ex.GetType().Name, failure.Kind, HanaFailure.NativeCode(ex));
            throw failure;
        }
    }

    private static string? Text(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTime? Utc(DbDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTime.SpecifyKind(reader.GetDateTime(index), DateTimeKind.Utc);
}

public sealed record ManifestRequest(IReadOnlyList<string>? AccessKeys);

/// <summary>Resultado por nota: registered | rejected | unanswered.</summary>
public sealed record ManifestNoteView(string AccessKey, string Outcome, string Message);

public sealed record ManifestView(int Registered, int Rejected, int Unanswered, string Message, IReadOnlyList<ManifestNoteView> Notes);

/// <summary>Nota pendente com a situação da Ciência da Operação (null = ainda não enviada).</summary>
public sealed record PendingManifestView(
    string AccessKey, string? IssuerName, string? IssuerId, DateTime? IssuedAtUtc, decimal? TotalValue,
    bool CanManifest, string? ManifestState, string? ManifestMessage, DateTime? ManifestAtUtc);

/// <summary>
/// Etapa 6: Ciência da Operação para as notas que a SEFAZ entregou só em resumo.
/// Só é enviada por ação do operador, para as notas que ele marcou na tela; nunca automaticamente.
/// </summary>
public sealed class ManifestService(
    ICaptureStore capture, IManifestStore store, IManifestGateway gateway, CompanyService companies, TimeProvider clock,
    ILogger<ManifestService> logger)
{
    public const int MaxNotesPerRequest = 100;
    private static readonly SemaphoreSlim SendLock = new(1, 1);

    public async Task<IReadOnlyList<PendingManifestView>> PendingAsync(string companyId, CancellationToken ct)
    {
        await companies.GetAsync(companyId, ct);
        var manifest = (await store.ListAsync(companyId, NfeManifestProtocol.AwarenessEvent, ct)).ToDictionary(m => m.AccessKey);
        return (await capture.ListDocumentsAsync(companyId, ct)).Where(NfeCaptureEngine.IsPendingSummary)
            .OrderByDescending(d => d.IssuedAtUtc).Take(200)
            .Select(d =>
            {
                manifest.TryGetValue(d.AccessKey, out var row);
                var registered = row?.State == ManifestRow.Registered;
                return new PendingManifestView(d.AccessKey, d.IssuerName, d.IssuerId, d.IssuedAtUtc, d.TotalValue,
                    NfeManifestProtocol.CanManifest(d.AccessKey) && !registered, row?.State,
                    row is null ? null : Describe(row.Status, row.Reason, registered), row?.RegisteredAtUtc ?? row?.UpdatedAtUtc);
            }).ToList();
    }

    public async Task<ManifestView> AcknowledgeAsync(string companyId, ManifestRequest? request, CancellationToken ct)
    {
        var (company, view) = await companies.GetAsync(companyId, ct);
        if (company.ArchivedAtUtc is not null)
            throw new NexFiscalException("company_archived", "Esta empresa está arquivada. Reative-a na seção Empresas para manifestar.", 409);
        if (view.CertificateStatus != "stored")
            throw new NexFiscalException("certificate_unavailable", "Manifestação bloqueada: " + view.CertificateMessage, 409);
        if (!NfeManifestProtocol.CanAuthor(company.Cnpj))
            throw new NexFiscalException("manifest_not_supported",
                "O leiaute oficial do evento de manifestação ainda não aceita CNPJ alfanumérico. Faça a manifestação no portal da SEFAZ.", 409);
        var requested = (request?.AccessKeys ?? []).Where(k => k is not null).Distinct(StringComparer.Ordinal).ToList();
        if (requested.Count == 0)
            throw new NexFiscalException("invalid_input", "Marque ao menos uma nota para registrar a ciência.", 400);
        if (requested.Count > MaxNotesPerRequest)
            throw new NexFiscalException("invalid_input", $"Selecione no máximo {MaxNotesPerRequest} notas por vez.", 400);

        await SendLock.WaitAsync(ct);
        try
        {
            // Só vale o que o próprio NexFiscal conhece: resumo recebido da SEFAZ, sem XML completo e sem ciência registrada.
            var pending = (await capture.ListDocumentsAsync(companyId, ct)).Where(NfeCaptureEngine.IsPendingSummary)
                .Select(d => d.AccessKey).ToHashSet(StringComparer.Ordinal);
            var done = (await store.ListAsync(companyId, NfeManifestProtocol.AwarenessEvent, ct))
                .Where(m => m.State == ManifestRow.Registered).Select(m => m.AccessKey).ToHashSet(StringComparer.Ordinal);
            var keys = requested.Where(k => pending.Contains(k) && !done.Contains(k) && NfeManifestProtocol.CanManifest(k)).ToList();
            if (keys.Count == 0)
                throw new NexFiscalException("nothing_to_manifest",
                    "Nenhuma das notas marcadas está aguardando ciência. Atualize a lista e tente de novo.", 409);

            using var certificate = companies.OpenCertificate(companyId);
            var notes = new List<ManifestNoteView>();
            string? interruption = null;
            foreach (var lot in keys.Chunk(NfeManifestProtocol.MaxEventsPerLot))
            {
                ManifestLotResult result;
                try
                {
                    result = await gateway.SendAwarenessAsync(company.Cnpj, lot, certificate, clock.GetUtcNow(), ct);
                    if (result.Status != NfeManifestProtocol.StatusLotProcessed)
                        throw new NexFiscalException("sefaz_rejected",
                            $"A SEFAZ não processou o pedido de ciência: {result.Status} - {result.Reason}", 502);
                }
                catch (NexFiscalException ex) when (notes.Count > 0)
                {
                    // Lotes anteriores já foram registrados e gravados: informar o que ficou sem resposta em vez de perder o resultado.
                    interruption = ex.Message;
                    break;
                }
                logger.LogInformation("Ciência da Operação: lote de {Count} nota(s) processado pela SEFAZ para a empresa {CompanyId}", lot.Length, companyId);

                var answers = result.Events.GroupBy(e => e.AccessKey).ToDictionary(g => g.Key, g => g.First());
                foreach (var key in lot)
                {
                    if (!answers.TryGetValue(key, out var answer))
                    {
                        notes.Add(new ManifestNoteView(key, "unanswered", "A SEFAZ não devolveu resultado para esta nota."));
                        continue;
                    }
                    var registered = IsRegistered(answer.Status);
                    var now = clock.GetUtcNow().UtcDateTime;
                    await store.SaveAsync(companyId, new ManifestRow(key, NfeManifestProtocol.AwarenessEvent,
                        registered ? ManifestRow.Registered : ManifestRow.Rejected, answer.Status, answer.Reason, answer.Protocol,
                        registered ? answer.RegisteredAtUtc ?? now : null, now), ct);
                    notes.Add(new ManifestNoteView(key, registered ? "registered" : "rejected", Describe(answer.Status, answer.Reason, registered)));
                }
            }

            var sent = notes.Select(n => n.AccessKey).ToHashSet(StringComparer.Ordinal);
            notes.AddRange(keys.Where(k => !sent.Contains(k))
                .Select(k => new ManifestNoteView(k, "unanswered", "Não enviada: o envio foi interrompido antes desta nota.")));
            int ok = notes.Count(n => n.Outcome == "registered"), rejected = notes.Count(n => n.Outcome == "rejected"),
                unanswered = notes.Count - ok - rejected;
            var message = $"Ciência registrada para {ok} nota(s)." +
                (rejected > 0 ? $" {rejected} recusada(s) pela SEFAZ — veja o motivo na lista." : "") +
                (unanswered > 0 ? $" {unanswered} sem resposta — podem ser enviadas de novo." : "") +
                (interruption is not null ? " O envio foi interrompido: " + interruption : "") +
                (ok > 0 ? " O XML completo costuma ser liberado pela SEFAZ em alguns minutos e chega na próxima captura." : "");
            return new ManifestView(ok, rejected, unanswered, message, notes);
        }
        finally { SendLock.Release(); }
    }

    /// <summary>135/136: registrado. 573: a ciência já existia. 655: já há manifestação conclusiva (o XML já está liberado).</summary>
    private static bool IsRegistered(int status) => status is NfeManifestProtocol.StatusRegistered
        or NfeManifestProtocol.StatusRegisteredNotLinked or NfeManifestProtocol.StatusDuplicate
        or NfeManifestProtocol.StatusAfterFinalManifestation;

    private static string Describe(int? status, string? reason, bool registered) => status switch
    {
        NfeManifestProtocol.StatusDuplicate => "A ciência já estava registrada na SEFAZ. Aguardando o XML completo.",
        NfeManifestProtocol.StatusAfterFinalManifestation => "Esta nota já tem manifestação conclusiva na SEFAZ. Aguardando o XML completo.",
        NfeManifestProtocol.StatusOutOfDeadline => "A SEFAZ recusou: prazo para manifestação encerrado.",
        NfeManifestProtocol.StatusCancelledOrDenied => "A SEFAZ recusou: a nota está cancelada ou denegada, não há XML a liberar.",
        _ when registered => "Ciência registrada. Aguardando o XML completo.",
        _ => $"A SEFAZ recusou: {status} - {reason}"
    };
}
