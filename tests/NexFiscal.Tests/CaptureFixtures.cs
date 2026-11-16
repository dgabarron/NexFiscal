using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexFiscal.Web;

namespace NexFiscal.Tests;

/// <summary>Documentos e respostas de teste no formato da NT 2014.002. Chaves e CNPJs fictícios.</summary>
public static class Sefaz
{
    public const string IssuerCnpj = TestCertificates.CnpjB;

    public static string Key(int number) => $"352611{IssuerCnpj}55001{number:D9}1{number:D8}7";

    public static string ProcNFe(string key, string value = "1234.56") =>
        "<nfeProc xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"4.00\"><NFe><infNFe Id=\"NFe" + key + "\" versao=\"4.00\">" +
        "<ide><dhEmi>2026-11-01T10:00:00-03:00</dhEmi></ide><emit><CNPJ>" + IssuerCnpj + "</CNPJ><xNome>FORNECEDOR EXEMPLO LTDA</xNome></emit>" +
        "<total><ICMSTot><vNF>" + value + "</vNF></ICMSTot></total></infNFe></NFe>" +
        "<protNFe versao=\"4.00\"><infProt><chNFe>" + key + "</chNFe><cStat>100</cStat></infProt></protNFe></nfeProc>";

    public static string ResNFe(string key, int situation = 1) =>
        "<resNFe xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"1.01\"><chNFe>" + key + "</chNFe><CNPJ>" + IssuerCnpj + "</CNPJ>" +
        "<xNome>FORNECEDOR EXEMPLO LTDA</xNome><IE>123456789012</IE><dhEmi>2026-11-01T10:00:00-03:00</dhEmi><tpNF>1</tpNF><vNF>1234.56</vNF>" +
        "<digVal>AAAAAAAAAAAAAAAAAAAAAAAAAAA=</digVal><dhRecbto>2026-11-01T10:01:00-03:00</dhRecbto><nProt>135260000000001</nProt>" +
        "<cSitNFe>" + situation + "</cSitNFe></resNFe>";

    public static string CancelEvent(string key) =>
        "<resEvento xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"1.01\"><cOrgao>35</cOrgao><CNPJ>" + IssuerCnpj + "</CNPJ><chNFe>" + key +
        "</chNFe><dhEvento>2026-11-02T10:00:00-03:00</dhEvento><tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento><xEvento>Cancelamento</xEvento>" +
        "<dhRecbto>2026-11-02T10:00:10-03:00</dhRecbto><nProt>135260000000002</nProt></resEvento>";

    public sealed record Doc(long? Nsu, string Schema, string Xml);
    public static Doc Complete(long nsu, int number) => new(nsu, "procNFe_v4.00.xsd", ProcNFe(Key(number)));
    public static Doc Summary(long nsu, int number, int situation = 1) => new(nsu, "resNFe_v1.01.xsd", ResNFe(Key(number), situation));
    public static Doc Cancel(long nsu, int number) => new(nsu, "resEvento_v1.01.xsd", CancelEvent(Key(number)));

    public static string Response(int status, string reason, long? last, long? max, params Doc[] documents)
    {
        var lot = new StringBuilder();
        if (documents.Length > 0)
        {
            lot.Append("<loteDistDFeInt>");
            foreach (var document in documents)
                lot.Append("<docZip").Append(document.Nsu is { } nsu ? $" NSU=\"{nsu:D15}\"" : "")
                    .Append(" schema=\"").Append(document.Schema).Append("\">").Append(Zip(document.Xml)).Append("</docZip>");
            lot.Append("</loteDistDFeInt>");
        }
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\"><soap:Body>" +
               "<nfeDistDFeInteresseResponse xmlns=\"http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe\"><nfeDistDFeInteresseResult>" +
               "<retDistDFeInt xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"1.01\"><tpAmb>1</tpAmb><verAplic>1.4.0</verAplic>" +
               $"<cStat>{status}</cStat><xMotivo>{reason}</xMotivo><dhResp>2026-11-15T09:00:00-03:00</dhResp>" +
               (last is { } l ? $"<ultNSU>{l:D15}</ultNSU>" : "") + (max is { } m ? $"<maxNSU>{m:D15}</maxNSU>" : "") + lot +
               "</retDistDFeInt></nfeDistDFeInteresseResult></nfeDistDFeInteresseResponse></soap:Body></soap:Envelope>";
    }

    public static string Found(long last, long max, params Doc[] documents) => Response(138, "Documento(s) localizado(s)", last, max, documents);
    public static string None(long last) => Response(137, "Nenhum documento localizado", last, last);
    public static string Blocked(long? last = null) =>
        Response(656, "Rejeicao: Consumo Indevido. Deve ser aguardado 1 hora para efetuar nova solicitacao caso nao existam mais documentos a serem pesquisados. Tente apos 1 hora", last, last);

    public static string EventResponse(int lotStatus, string lotReason, params (string Key, int Status, string Reason)[] events) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\"><soap:Body>" +
        "<nfeRecepcaoEventoNFResult xmlns=\"http://www.portalfiscal.inf.br/nfe/wsdl/NFeRecepcaoEvento4\">" +
        "<retEnvEvento xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"1.00\"><idLote>1</idLote><tpAmb>1</tpAmb><verAplic>AN_1.6.0</verAplic>" +
        $"<cOrgao>91</cOrgao><cStat>{lotStatus}</cStat><xMotivo>{lotReason}</xMotivo>" +
        string.Concat(events.Select((e, i) =>
            "<retEvento versao=\"1.00\"><infEvento><tpAmb>1</tpAmb><verAplic>AN_1.6.0</verAplic><cOrgao>91</cOrgao>" +
            $"<cStat>{e.Status}</cStat><xMotivo>{e.Reason}</xMotivo><chNFe>{e.Key}</chNFe><tpEvento>210210</tpEvento>" +
            "<xEvento>Ciencia da Operacao</xEvento><nSeqEvento>1</nSeqEvento><CNPJDest>" + TestCertificates.CnpjA + "</CNPJDest>" +
            "<dhRegEvento>2026-11-15T09:00:05-03:00</dhRegEvento>" +
            (e.Status is 135 or 136 ? $"<nProt>89126000000{i:D4}</nProt>" : "") + "</infEvento></retEvento>")) +
        "</retEnvEvento></nfeRecepcaoEventoNFResult></soap:Body></soap:Envelope>";

    public static string Zip(string xml)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest)) gzip.Write(Encoding.UTF8.GetBytes(xml));
        return Convert.ToBase64String(output.ToArray());
    }
}

/// <summary>SEFAZ simulada: devolve as respostas programadas, em ordem, e registra cada pedido recebido.</summary>
public sealed class FakeSefaz : ISefazTransport
{
    public sealed record Call(string Operation, long Nsu, string Cnpj, string? Thumbprint, Uri Url);
    public readonly List<Call> Calls = [];
    public readonly Queue<Func<Call, string>> Responses = new();

    public FakeSefaz Reply(string response) { Responses.Enqueue(_ => response); return this; }
    public FakeSefaz Throw(NexFiscalException error) { Responses.Enqueue(_ => throw error); return this; }

    public Task<string> SendAsync(Uri url, string soapEnvelope, X509Certificate2 clientCertificate, CancellationToken ct)
    {
        var match = Regex.Match(soapEnvelope, "<CNPJ>(\\w+)</CNPJ><(distNSU|consNSU)><(?:ultNSU|NSU)>(\\d{15})<");
        if (!match.Success) throw new InvalidOperationException("Pedido fora do protocolo: " + soapEnvelope);
        var call = new Call(match.Groups[2].Value, long.Parse(match.Groups[3].Value), match.Groups[1].Value,
            clientCertificate.HasPrivateKey ? clientCertificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) : null, url);
        Calls.Add(call);
        if (Responses.Count == 0) throw new InvalidOperationException($"Consulta não esperada à SEFAZ: {call.Operation} {call.Nsu}");
        return Task.FromResult(Responses.Dequeue()(call));
    }

    // ---------- recepção de evento (manifestação do destinatário) ----------

    public sealed record EventCall(Uri Url, string Action, string Envelope, string? Thumbprint)
    {
        public IReadOnlyList<string> Keys => Regex.Matches(Envelope, "<chNFe>(\\d{44})</chNFe>").Select(m => m.Groups[1].Value).ToList();
    }
    public readonly List<EventCall> EventCalls = [];
    public readonly Queue<Func<EventCall, string>> EventResponses = new();

    /// <summary>Próximo lote: a SEFAZ registra (135) todas as notas, exceto as indicadas em <paramref name="exceptions"/>.</summary>
    public FakeSefaz ReplyEvents(params (string Key, int Status, string Reason)[] exceptions)
    {
        EventResponses.Enqueue(call => Sefaz.EventResponse(128, "Lote de evento processado", call.Keys.Select(key =>
            exceptions.FirstOrDefault(e => e.Key == key) is { Key: not null } e ? e : (key, 135, "Evento registrado e vinculado a NF-e")).ToArray()));
        return this;
    }

    public Task<string> SendAsync(Uri url, string soapAction, string soapEnvelope, X509Certificate2 clientCertificate, CancellationToken ct)
    {
        var call = new EventCall(url, soapAction, soapEnvelope,
            clientCertificate.HasPrivateKey ? clientCertificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) : null);
        EventCalls.Add(call);
        if (EventResponses.Count == 0) throw new InvalidOperationException("Evento não esperado enviado à SEFAZ.");
        return Task.FromResult(EventResponses.Dequeue()(call));
    }
}

/// <summary>Substitui a tabela BRAI_NEX_NfeManifest nos testes, com a mesma regra do repositório real.</summary>
public sealed class InMemoryManifestStore : IManifestStore
{
    public readonly Dictionary<(string Company, string Key, int Type), ManifestRow> Rows = [];

    public Task<IReadOnlyList<ManifestRow>> ListAsync(string companyId, int eventType, CancellationToken ct)
    {
        lock (Rows)
            return Task.FromResult<IReadOnlyList<ManifestRow>>(
                Rows.Where(r => r.Key.Company == companyId && r.Key.Type == eventType).Select(r => r.Value).ToList());
    }

    public Task SaveAsync(string companyId, ManifestRow row, CancellationToken ct)
    {
        lock (Rows)
        {
            var id = (companyId, row.AccessKey, row.EventType);
            if (!Rows.TryGetValue(id, out var current) || current.State != ManifestRow.Registered) Rows[id] = row;
        }
        return Task.CompletedTask;
    }
}

/// <summary>Substitui o HANA nos testes da captura, com as mesmas regras do repositório real.</summary>
public class UnsafeInMemoryCaptureStore
{
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public sealed class Company
    {
        public long LastNsu;
        public DateTime NextAllowedAtUtc = Epoch;
        public readonly SortedDictionary<long, EnvelopeRow> Envelopes = [];
        public readonly Dictionary<string, DocumentRow> Documents = [];
        public readonly Dictionary<long, (string State, int Attempts, int? Status, DateTime? LastAttempt)> Recovery = [];
        public readonly List<CaptureAttempt> Log = [];
        public string? Destination;
    }

    public readonly Dictionary<string, Company> Companies = [];
    public readonly List<CaptureJob> Jobs = [];
    public readonly Dictionary<string, MonitorRow> Monitors = [];
    public bool FailNextSave;
    public Company Of(string id) => Companies.TryGetValue(id, out var c) ? c : Companies[id] = new Company();

    public Task<CaptureCursor> GetCursorAsync(string companyId, CancellationToken ct) =>
        Task.FromResult(new CaptureCursor(Of(companyId).LastNsu, Of(companyId).NextAllowedAtUtc));

    public Task SaveLotAsync(string companyId, LotPersistence lot, DateTime nowUtc, CancellationToken ct)
    {
        if (FailNextSave) { FailNextSave = false; throw new NexFiscalException("hana_error", "O SAP HANA recusou a operação (código 2048).", 502); }
        var c = Of(companyId);
        foreach (var e in lot.Envelopes) c.Envelopes.TryAdd(e.Nsu, e);
        foreach (var d in lot.Documents)
        {
            if (!c.Documents.TryGetValue(d.AccessKey, out var current)) c.Documents[d.AccessKey] = d;
            else if (d.HasCompleteXml)
                c.Documents[d.AccessKey] = current with
                {
                    HasCompleteXml = true, CompleteNsu = d.CompleteNsu, CompleteSha256 = d.CompleteSha256, IssuerId = d.IssuerId,
                    IssuerName = d.IssuerName, IssuedAtUtc = d.IssuedAtUtc, TotalValue = d.TotalValue
                };
            else if (!current.HasCompleteXml) c.Documents[d.AccessKey] = current with { SummaryNsu = d.SummaryNsu, Situation = d.Situation };
        }
        foreach (var key in lot.CancelledKeys)
            if (c.Documents.TryGetValue(key, out var doc)) c.Documents[key] = doc with { Situation = 3 };
        foreach (var nsu in lot.MissingNsus) c.Recovery.TryAdd(nsu, ("pending", 0, null, null));
        if (lot.LastNsu is { } last && last > c.LastNsu) c.LastNsu = last; // nunca retrocede
        if (lot.NextAllowedAtUtc is { } next) c.NextAllowedAtUtc = next;
        return Task.CompletedTask;
    }

    public Task LogAttemptAsync(string companyId, CaptureAttempt attempt, CancellationToken ct) { Of(companyId).Log.Add(attempt); return Task.CompletedTask; }

    public Task<IReadOnlyList<DocumentRow>> ListDocumentsAsync(string companyId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DocumentRow>>(Of(companyId).Documents.Values.ToList());

    public Task<byte[]?> ReadEnvelopeAsync(string companyId, long nsu, CancellationToken ct) =>
        Task.FromResult(Of(companyId).Envelopes.TryGetValue(nsu, out var e) ? e.Xml : null);

    public Task<IReadOnlyList<EnvelopeRow>> ListUnindexedCompleteEnvelopesAsync(string companyId, long afterNsu, CancellationToken ct)
    {
        var c = Of(companyId);
        var indexed = c.Documents.Values.Where(d => d.CompleteNsu is not null).Select(d => d.CompleteNsu!.Value).ToHashSet();
        return Task.FromResult<IReadOnlyList<EnvelopeRow>>(c.Envelopes.Values
            .Where(e => e.Nsu > afterNsu && e.Schema.StartsWith("procNFe") && !indexed.Contains(e.Nsu)).Take(50).ToList());
    }

    public Task<IReadOnlyList<long>> ListPendingRecoveryAsync(string companyId, int maxAttempts, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<long>>(Of(companyId).Recovery.Where(r => r.Value.State == "pending" && r.Value.Attempts < maxAttempts)
            .Select(r => r.Key).Order().Take(take).ToList());

    public Task<int> CountRecoveryAttemptsSinceAsync(string companyId, DateTime sinceUtc, CancellationToken ct) =>
        Task.FromResult(Of(companyId).Recovery.Values.Count(r => r.LastAttempt >= sinceUtc));

    public Task<int> CountOpenRecoveryAsync(string companyId, int maxAttempts, CancellationToken ct) =>
        Task.FromResult(Of(companyId).Recovery.Values.Count(r => r.State == "pending" && r.Attempts < maxAttempts));

    public Task SaveRecoveryResultAsync(string companyId, long nsu, string state, int? status, DateTime nowUtc, CancellationToken ct)
    {
        var current = Of(companyId).Recovery[nsu];
        Of(companyId).Recovery[nsu] = (state, current.Attempts + 1, status, nowUtc);
        return Task.CompletedTask;
    }

    public Task InsertJobAsync(CaptureJob job, CancellationToken ct) { Jobs.Add(job); return Task.CompletedTask; }
    public Task UpdateJobAsync(CaptureJob job, CancellationToken ct) { Jobs[Jobs.FindIndex(j => j.JobId == job.JobId)] = job; return Task.CompletedTask; }
    public Task<IReadOnlyList<CaptureJob>> ListActiveJobsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CaptureJob>>(Jobs.Where(j => j.Active).OrderBy(j => j.RequestedAtUtc).ToList());
    public Task<CaptureJob?> LatestJobAsync(string companyId, CancellationToken ct) =>
        Task.FromResult(Jobs.Where(j => j.CompanyId == companyId && !j.IsNfse).OrderByDescending(j => j.RequestedAtUtc).FirstOrDefault());
    public Task<CaptureJob?> LatestNfseJobAsync(string companyId, CancellationToken ct) =>
        Task.FromResult(Jobs.Where(j => j.CompanyId == companyId && j.IsNfse).OrderByDescending(j => j.RequestedAtUtc).FirstOrDefault());
    public Task<CaptureJob?> LatestJobOfKindAsync(string companyId, string kind, CancellationToken ct) =>
        Task.FromResult(Jobs.Where(j => j.CompanyId == companyId && j.Kind == kind).OrderByDescending(j => j.RequestedAtUtc).FirstOrDefault());
    public Task<MonitorRow?> GetMonitorAsync(string companyId, CancellationToken ct) => Task.FromResult(Monitors.GetValueOrDefault(companyId));
    public Task SaveMonitorAsync(MonitorRow monitor, DateTime nowUtc, CancellationToken ct) { Monitors[monitor.CompanyId] = monitor; return Task.CompletedTask; }
    public Task SetMonitorScheduleAsync(string companyId, DateTime? nextRunAtUtc, DateTime? lastRunAtUtc, string? message, DateTime nowUtc, CancellationToken ct)
    {
        if (Monitors.TryGetValue(companyId, out var current))
            Monitors[companyId] = current with { NextRunAtUtc = nextRunAtUtc, LastRunAtUtc = lastRunAtUtc, LastMessage = message };
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<MonitorRow>> ListEnabledMonitorsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MonitorRow>>(Monitors.Values.Where(m => m.Enabled).OrderBy(m => m.CompanyId).ToList());
    public Task<string?> GetDestinationAsync(string companyId, CancellationToken ct) => Task.FromResult(Of(companyId).Destination);
    public Task SetDestinationAsync(string companyId, string directory, DateTime nowUtc, CancellationToken ct) { Of(companyId).Destination = directory; return Task.CompletedTask; }
}

/// <summary>Versão usada nos testes: mesmas regras, com trava para uso simultâneo pela fila e pela API.</summary>
public sealed class InMemoryCaptureStore : UnsafeInMemoryCaptureStore, ICaptureStore
{
    private readonly object _lock = new();
    private T Locked<T>(Func<Task<T>> work) { lock (_lock) return work().GetAwaiter().GetResult(); }
    private void Locked(Func<Task> work) { lock (_lock) work().GetAwaiter().GetResult(); }

    Task<CaptureCursor> ICaptureStore.GetCursorAsync(string companyId, CancellationToken ct) => Task.FromResult(Locked(() => GetCursorAsync(companyId, ct)));
    Task ICaptureStore.SaveLotAsync(string companyId, LotPersistence lot, DateTime nowUtc, CancellationToken ct) { Locked(() => SaveLotAsync(companyId, lot, nowUtc, ct)); return Task.CompletedTask; }
    Task ICaptureStore.LogAttemptAsync(string companyId, CaptureAttempt attempt, CancellationToken ct) { Locked(() => LogAttemptAsync(companyId, attempt, ct)); return Task.CompletedTask; }
    Task<IReadOnlyList<DocumentRow>> ICaptureStore.ListDocumentsAsync(string companyId, CancellationToken ct) => Task.FromResult(Locked(() => ListDocumentsAsync(companyId, ct)));
    Task<byte[]?> ICaptureStore.ReadEnvelopeAsync(string companyId, long nsu, CancellationToken ct) => Task.FromResult(Locked(() => ReadEnvelopeAsync(companyId, nsu, ct)));
    Task<IReadOnlyList<EnvelopeRow>> ICaptureStore.ListUnindexedCompleteEnvelopesAsync(string companyId, long afterNsu, CancellationToken ct) => Task.FromResult(Locked(() => ListUnindexedCompleteEnvelopesAsync(companyId, afterNsu, ct)));
    Task<IReadOnlyList<long>> ICaptureStore.ListPendingRecoveryAsync(string companyId, int maxAttempts, int take, CancellationToken ct) => Task.FromResult(Locked(() => ListPendingRecoveryAsync(companyId, maxAttempts, take, ct)));
    Task<int> ICaptureStore.CountRecoveryAttemptsSinceAsync(string companyId, DateTime sinceUtc, CancellationToken ct) => Task.FromResult(Locked(() => CountRecoveryAttemptsSinceAsync(companyId, sinceUtc, ct)));
    Task ICaptureStore.SaveRecoveryResultAsync(string companyId, long nsu, string state, int? status, DateTime nowUtc, CancellationToken ct) { Locked(() => SaveRecoveryResultAsync(companyId, nsu, state, status, nowUtc, ct)); return Task.CompletedTask; }
    Task<int> ICaptureStore.CountOpenRecoveryAsync(string companyId, int maxAttempts, CancellationToken ct) => Task.FromResult(Locked(() => CountOpenRecoveryAsync(companyId, maxAttempts, ct)));
    Task ICaptureStore.InsertJobAsync(CaptureJob job, CancellationToken ct) { Locked(() => InsertJobAsync(job, ct)); return Task.CompletedTask; }
    Task ICaptureStore.UpdateJobAsync(CaptureJob job, CancellationToken ct) { Locked(() => UpdateJobAsync(job, ct)); return Task.CompletedTask; }
    Task<IReadOnlyList<CaptureJob>> ICaptureStore.ListActiveJobsAsync(CancellationToken ct) => Task.FromResult(Locked(() => ListActiveJobsAsync(ct)));
    Task<CaptureJob?> ICaptureStore.LatestJobAsync(string companyId, CancellationToken ct) => Task.FromResult(Locked(() => LatestJobAsync(companyId, ct)));
    Task<CaptureJob?> ICaptureStore.LatestNfseJobAsync(string companyId, CancellationToken ct) => Task.FromResult(Locked(() => LatestNfseJobAsync(companyId, ct)));
    Task<CaptureJob?> ICaptureStore.LatestJobOfKindAsync(string companyId, string kind, CancellationToken ct) => Task.FromResult(Locked(() => LatestJobOfKindAsync(companyId, kind, ct)));
    Task<MonitorRow?> ICaptureStore.GetMonitorAsync(string companyId, CancellationToken ct) => Task.FromResult(Locked(() => GetMonitorAsync(companyId, ct)));
    Task ICaptureStore.SaveMonitorAsync(MonitorRow monitor, DateTime nowUtc, CancellationToken ct) { Locked(() => SaveMonitorAsync(monitor, nowUtc, ct)); return Task.CompletedTask; }
    Task ICaptureStore.SetMonitorScheduleAsync(string companyId, DateTime? nextRunAtUtc, DateTime? lastRunAtUtc, string? message, DateTime nowUtc, CancellationToken ct) { Locked(() => SetMonitorScheduleAsync(companyId, nextRunAtUtc, lastRunAtUtc, message, nowUtc, ct)); return Task.CompletedTask; }
    Task<IReadOnlyList<MonitorRow>> ICaptureStore.ListEnabledMonitorsAsync(CancellationToken ct) => Task.FromResult(Locked(() => ListEnabledMonitorsAsync(ct)));
    Task<string?> ICaptureStore.GetDestinationAsync(string companyId, CancellationToken ct) => Task.FromResult(Locked(() => GetDestinationAsync(companyId, ct)));
    Task ICaptureStore.SetDestinationAsync(string companyId, string directory, DateTime nowUtc, CancellationToken ct) { Locked(() => SetDestinationAsync(companyId, directory, nowUtc, ct)); return Task.CompletedTask; }
}

/// <summary>Uma instalação completa de teste: empresa cadastrada com A1, pasta de destino, HANA em memória e SEFAZ simulada.</summary>
public sealed class CaptureInstallation : IDisposable
{
    public readonly CompanyInstallation Base = new();
    public readonly InMemoryCaptureStore Store = new();
    public readonly FakeSefaz Sefaz = new();
    public readonly string Folder = Directory.CreateTempSubdirectory("nexfiscal-xml-").FullName;
    public string CompanyId = "";
    public TestClock Clock => Base.Clock;
    public InMemoryCaptureStore.Company Data => Store.Of(CompanyId);

    public CompanyService Companies = null!;
    public CaptureService Service = null!;
    public MonitorService Monitor = null!;
    public readonly InMemoryNfseStore NfseStore = new();
    public readonly FakeAdn Adn = new();
    public NfseService Nfse = null!;
    public readonly string NfseFolder = Directory.CreateTempSubdirectory("nexfiscal-nfse-").FullName;
    public CaptureWorker Worker = null!;
    public readonly InMemoryManifestStore ManifestStore = new();
    public ManifestService Manifest = null!;

    public static async Task<CaptureInstallation> CreateAsync()
    {
        var install = new CaptureInstallation();
        install.Restart();
        install.CompanyId = (await install.Companies.RegisterAsync(TestCertificates.Upload(), CancellationToken.None)).Company!.CompanyId;
        return install;
    }

    /// <summary>Recria serviços, motor e fila sobre os mesmos dados: equivale a reiniciar o backend.</summary>
    public void Restart()
    {
        var logs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Base.Logs));
        Companies = Base.Start();
        var signal = new CaptureSignal();
        var engine = new NfeCaptureEngine(Store, Companies, Sefaz, Clock, logs.CreateLogger<NfeCaptureEngine>());
        Service = new CaptureService(Store, Companies, signal, Clock);
        Monitor = new MonitorService(Store, Companies, signal, Clock);
        var nfseEngine = new NfseCaptureEngine(NfseStore, Store, Companies, Adn, Clock, logs.CreateLogger<NfseCaptureEngine>());
        Nfse = new NfseService(NfseStore, Store, Companies, signal, Clock);
        Manifest = new ManifestService(Store, ManifestStore, new SefazManifestGateway(Sefaz), Companies, Clock, logs.CreateLogger<ManifestService>());
        Worker = new CaptureWorker(Store, engine, NfseStore, nfseEngine, Companies, signal, Clock, logs.CreateLogger<CaptureWorker>());
    }

    /// <summary>Clique em "Capturar todos os XMLs disponíveis" + uma passada da fila em segundo plano.</summary>
    public async Task<CaptureJob> CaptureAsync(string? folder = null)
    {
        var view = await Service.StartAsync(CompanyId, new CaptureRequest(folder ?? Folder), CancellationToken.None);
        await Worker.RunDueJobsAsync(CancellationToken.None);
        return Store.Jobs.Single(j => j.JobId == view.JobId);
    }

    public string[] XmlFiles() => Directory.GetFiles(Folder, "*.xml").Select(Path.GetFileName).Order().ToArray()!;
    public string FileOf(int number) => Path.Combine(Folder, NexFiscal.Tests.Sefaz.Key(number) + ".xml");

    public void Dispose()
    {
        Base.Dispose();
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        if (Directory.Exists(NfseFolder)) Directory.Delete(NfseFolder, recursive: true);
    }
}

/// <summary>NFS-e e respostas do ADN de teste. Chaves e CNPJs fictícios.</summary>
public static class Adn
{
    public static string Key(int number) => TestCertificates.CnpjB + number.ToString("D36"); // 50 dígitos

    public static string Nfse(string key, string value = "500.00") =>
        "<NFSe xmlns=\"http://www.sped.fazenda.gov.br/nfse\" versao=\"1.01\"><infNFSe Id=\"NFS" + key + "\"><xLocEmi>SAO PAULO</xLocEmi>" +
        "<nNFSe>1</nNFSe><cStat>100</cStat><dhProc>2026-11-10T09:30:00-03:00</dhProc><emit><CNPJ>" + TestCertificates.CnpjB +
        "</CNPJ><xNome>PRESTADOR EXEMPLO LTDA</xNome></emit><valores><vLiq>" + value + "</vLiq></valores>" +
        "<DPS versao=\"1.01\"><infDPS Id=\"DPS1\"><dCompet>2026-11-10</dCompet></infDPS></DPS></infNFSe></NFSe>";

    public static string CancelEvent(string key) =>
        "<evento xmlns=\"http://www.sped.fazenda.gov.br/nfse\" versao=\"1.01\"><infEvento Id=\"EVT1\"><dhProc>2026-11-11T10:00:00-03:00</dhProc>" +
        "<pedRegEvento><infPedReg><chNFSe>" + key + "</chNFSe><e101101><xDesc>Cancelamento de NFS-e</xDesc></e101101></infPedReg></pedRegEvento></infEvento></evento>";

    public sealed record Doc(long? Nsu, string? Key, string Type, string Xml);
    public static Doc Note(long nsu, int number) => new(nsu, Key(number), "NFSE", Nfse(Key(number)));
    public static Doc Cancel(long nsu, int number) => new(nsu, Key(number), "EVENTO", CancelEvent(Key(number)));

    public static (int, string) Found(params Doc[] documents) => (200, System.Text.Json.JsonSerializer.Serialize(new
    {
        StatusProcessamento = "DOCUMENTOS_LOCALIZADOS",
        LoteDFe = documents.Select(d => new { NSU = d.Nsu, ChaveAcesso = d.Key, TipoDocumento = d.Type, ArquivoXml = Sefaz.Zip(d.Xml), DataHoraGeracao = "2026-11-10T09:30:00" }),
        Alertas = (object?)null, Erros = (object?)null, TipoAmbiente = "PRODUCAO"
    }));

    public static (int, string) None() => (404, "{\"StatusProcessamento\":\"NENHUM_DOCUMENTO_LOCALIZADO\",\"LoteDFe\":null,\"Erros\":[{\"Codigo\":\"E2220\"," +
        "\"Descricao\":\"Nenhum documento localizado - não existem documentos fiscais para o contribuinte a partir do NSU informado.\"}]}");

    public static (int, string) Rejected(string code, string text) =>
        (400, "{\"StatusProcessamento\":\"REJEICAO\",\"Erros\":[{\"Codigo\":\"" + code + "\",\"Descricao\":\"" + text + "\"}],\"TipoConsulta\":\"InscricaoCertificado\"}");
}

/// <summary>ADN simulado: devolve as respostas programadas, em ordem, e registra cada consulta.</summary>
public sealed class FakeAdn : IAdnTransport
{
    public sealed record Call(long Nsu, string Cnpj, string Url, bool HasPrivateKey);
    public readonly List<Call> Calls = [];
    public readonly Queue<Func<(int, string, TimeSpan?)>> Responses = new();

    public FakeAdn Reply((int Status, string Body) response, TimeSpan? retryAfter = null) { Responses.Enqueue(() => (response.Status, response.Body, retryAfter)); return this; }
    public FakeAdn Throw(NexFiscalException error) { Responses.Enqueue(() => throw error); return this; }

    public Task<(int Status, string Body, TimeSpan? RetryAfter)> GetAsync(Uri url, System.Security.Cryptography.X509Certificates.X509Certificate2 clientCertificate, CancellationToken ct)
    {
        var match = System.Text.RegularExpressions.Regex.Match(url.AbsoluteUri, "^https://adn\\.nfse\\.gov\\.br/contribuintes/DFe/(\\d+)\\?cnpjConsulta=(\\w{14})&lote=true$");
        if (!match.Success) throw new InvalidOperationException("Consulta fora do contrato: " + url);
        Calls.Add(new Call(long.Parse(match.Groups[1].Value), match.Groups[2].Value, url.AbsoluteUri, clientCertificate.HasPrivateKey));
        if (Responses.Count == 0) throw new InvalidOperationException("Consulta não esperada ao ADN: " + url);
        return Task.FromResult(Responses.Dequeue()());
    }
}

/// <summary>Substitui o HANA nos testes da NFS-e, com as mesmas regras do repositório real e trava para uso simultâneo.</summary>
public sealed class InMemoryNfseStore : INfseStore
{
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public sealed class Company
    {
        public long LastNsu;
        public DateTime NextAllowedAtUtc = Epoch;
        public readonly SortedDictionary<long, EnvelopeRow> Envelopes = [];
        public readonly Dictionary<string, NfseDocumentRow> Documents = [];
        public readonly List<CaptureAttempt> Log = [];
    }
    private readonly object _lock = new();
    public readonly Dictionary<string, Company> Companies = [];
    public readonly Dictionary<string, MonitorRow> Monitors = [];
    public bool FailNextSave;
    public Company Of(string id) { lock (_lock) return Companies.TryGetValue(id, out var c) ? c : Companies[id] = new Company(); }

    public Task<CaptureCursor> GetCursorAsync(string companyId, CancellationToken ct) { lock (_lock) return Task.FromResult(new CaptureCursor(Of(companyId).LastNsu, Of(companyId).NextAllowedAtUtc)); }

    public Task SaveLotAsync(string companyId, NfseLot lot, DateTime nowUtc, CancellationToken ct)
    {
        lock (_lock)
        {
            if (FailNextSave) { FailNextSave = false; throw new NexFiscalException("hana_error", "O SAP HANA recusou a operação (código 2048).", 502); }
            var c = Of(companyId);
            foreach (var e in lot.Envelopes) c.Envelopes.TryAdd(e.Nsu, e);
            foreach (var d in lot.Documents)
                c.Documents[d.AccessKey] = c.Documents.TryGetValue(d.AccessKey, out var current) ? current with { Nsu = d.Nsu, Sha256 = d.Sha256 } : d;
            foreach (var key in lot.CancelledKeys)
                if (c.Documents.TryGetValue(key, out var doc)) c.Documents[key] = doc with { Cancelled = true };
            if (lot.LastNsu is { } last && last > c.LastNsu) c.LastNsu = last;
            if (lot.NextAllowedAtUtc is { } next) c.NextAllowedAtUtc = next;
            return Task.CompletedTask;
        }
    }

    public Task LogAttemptAsync(string companyId, CaptureAttempt attempt, CancellationToken ct) { lock (_lock) Of(companyId).Log.Add(attempt); return Task.CompletedTask; }
    public Task<IReadOnlyList<NfseDocumentRow>> ListDocumentsAsync(string companyId, CancellationToken ct) { lock (_lock) return Task.FromResult<IReadOnlyList<NfseDocumentRow>>(Of(companyId).Documents.Values.ToList()); }
    public Task<byte[]?> ReadEnvelopeAsync(string companyId, long nsu, CancellationToken ct) { lock (_lock) return Task.FromResult(Of(companyId).Envelopes.TryGetValue(nsu, out var e) ? e.Xml : null); }
    public Task<MonitorRow?> GetMonitorAsync(string companyId, CancellationToken ct) { lock (_lock) return Task.FromResult(Monitors.GetValueOrDefault(companyId)); }
    public Task SaveMonitorAsync(MonitorRow monitor, DateTime nowUtc, CancellationToken ct) { lock (_lock) Monitors[monitor.CompanyId] = monitor; return Task.CompletedTask; }
    public Task SetMonitorScheduleAsync(string companyId, DateTime? nextRunAtUtc, DateTime? lastRunAtUtc, string? message, DateTime nowUtc, CancellationToken ct)
    {
        lock (_lock)
            if (Monitors.TryGetValue(companyId, out var current))
                Monitors[companyId] = current with { NextRunAtUtc = nextRunAtUtc, LastRunAtUtc = lastRunAtUtc, LastMessage = message };
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<MonitorRow>> ListEnabledMonitorsAsync(CancellationToken ct) { lock (_lock) return Task.FromResult<IReadOnlyList<MonitorRow>>(Monitors.Values.Where(m => m.Enabled).OrderBy(m => m.CompanyId).ToList()); }
}
