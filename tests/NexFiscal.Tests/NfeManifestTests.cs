using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using NexFiscal.Web;
using static NexFiscal.Tests.Sefaz;

namespace NexFiscal.Tests;

/// <summary>
/// Etapa 6: Ciência da Operação. Protocolo conferido contra o pacote oficial Evento_ManifestaDest_PL_v1.01
/// (NT 2020.001 v1.60). A SEFAZ é simulada: nada aqui valida o serviço real.
/// </summary>
public class NfeManifestTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Schemas = Path.Combine(AppContext.BaseDirectory, "Schemas", "Evento_ManifestaDest_PL_v1.01");
    private static readonly DateTimeOffset When = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static X509Certificate2 Certificate() => new(TestCertificates.Create(), TestCertificates.Password);

    private static void ValidateAgainst(string schemaFile, XElement element)
    {
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        // O arquivo oficial retEnvConfRecebto declara UTF-8 mas traz um comentário em Latin-1; é lido como publicado, sem alterar o arquivo.
        var path = Path.Combine(Schemas, schemaFile);
        var bytes = File.ReadAllBytes(path);
        string text;
        try { text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { text = System.Text.Encoding.Latin1.GetString(bytes); }
        using var reader = XmlReader.Create(new StringReader(text.TrimStart('\uFEFF')), new XmlReaderSettings { XmlResolver = new XmlUrlResolver() }, new Uri(path).AbsoluteUri);
        set.Add("http://www.portalfiscal.inf.br/nfe", reader);
        var errors = new List<string>();
        new XDocument(element).Validate(set, (_, e) => errors.Add(e.Message));
        Assert.True(errors.Count == 0, string.Join(" | ", errors));
    }

    private static bool SignatureIsValid(string eventXml, X509Certificate2 certificate)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(eventXml);
        var signed = new SignedXml(document);
        signed.LoadXml((XmlElement)document.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
        return signed.CheckSignature(certificate, verifySignatureOnly: true);
    }

    // ---------- protocolo ----------

    [Fact]
    public void Lote_de_ciencia_e_valido_no_schema_oficial_e_segue_a_nota_tecnica()
    {
        using var certificate = Certificate();
        var events = Enumerable.Range(1, 20)
            .Select(n => NfeManifestProtocol.BuildSignedAwareness(TestCertificates.CnpjA, Key(n), When, certificate)).ToList();
        var envelope = NfeManifestProtocol.BuildEnvelope(When.ToUnixTimeMilliseconds(), events);

        var data = NfeManifestProtocol.DataOf(envelope);
        ValidateAgainst("envConfRecebto_v1.00.xsd", data);
        XNamespace nfe = "http://www.portalfiscal.inf.br/nfe";
        var first = data.Elements(nfe + "evento").First().Element(nfe + "infEvento")!;
        ValidateAgainst("e210210_v1.00.xsd", first.Element(nfe + "detEvento")!);
        Assert.Equal("ID210210" + Key(1) + "01", (string?)first.Attribute("Id"));
        Assert.Equal(("91", "1", TestCertificates.CnpjA, "210210", "1", "2026-11-15T09:00:00-03:00"),
            ((string?)first.Element(nfe + "cOrgao"), (string?)first.Element(nfe + "tpAmb"), (string?)first.Element(nfe + "CNPJ"),
                (string?)first.Element(nfe + "tpEvento"), (string?)first.Element(nfe + "nSeqEvento"), (string?)first.Element(nfe + "dhEvento")));
        Assert.Contains("<soap12:Body><nfeDadosMsg xmlns=\"http://www.portalfiscal.inf.br/nfe/wsdl/NFeRecepcaoEvento4\">", envelope);
        Assert.Equal("https://www.nfe.fazenda.gov.br/NFeRecepcaoEvento4/NFeRecepcaoEvento4.asmx", NfeManifestProtocol.ProductionUrl.ToString());
        Assert.EndsWith("/NFeRecepcaoEvento4/nfeRecepcaoEventoNF", NfeManifestProtocol.SoapAction);
    }

    [Fact]
    public void Assinatura_confere_com_o_certificado_dentro_do_envelope_e_detecta_alteracao()
    {
        using var certificate = Certificate();
        var signed = NfeManifestProtocol.BuildSignedAwareness(TestCertificates.CnpjA, Key(1), When, certificate);
        var envelope = NfeManifestProtocol.BuildEnvelope(1, [signed]);

        Assert.Contains(signed, envelope); // o evento vai para a SEFAZ byte a byte como foi assinado
        Assert.True(SignatureIsValid(signed, certificate));
        Assert.False(SignatureIsValid(signed.Replace(Key(1), Key(2)), certificate));
        Assert.Contains("Algorithm=\"http://www.w3.org/2000/09/xmldsig#rsa-sha1\"", signed);
        Assert.Contains("Algorithm=\"http://www.w3.org/2000/09/xmldsig#enveloped-signature\"", signed);
        Assert.Contains("<Reference URI=\"#ID210210" + Key(1) + "01\">", signed);
        // Só a parte pública do certificado acompanha o evento.
        var embedded = Regex.Match(signed, "<X509Certificate>([^<]+)</X509Certificate>").Groups[1].Value;
        Assert.Equal(Convert.ToBase64String(certificate.RawData), embedded);
        Assert.DoesNotContain(TestCertificates.Password, envelope);
    }

    [Theory]
    [InlineData("12ABC34501DE35", null)] // CNPJ alfanumérico: fora do leiaute do evento
    [InlineData(TestCertificates.CnpjA, "3526ABCDEFGHIJ0001815500100000000110000000017")]
    [InlineData(TestCertificates.CnpjA, "123")]
    public void Evento_recusa_cnpj_e_chave_fora_do_leiaute_oficial(string cnpj, string? key)
    {
        using var certificate = Certificate();
        Assert.Throws<ArgumentException>(() => NfeManifestProtocol.BuildSignedAwareness(cnpj, key ?? Key(1), When, certificate));
    }

    [Fact]
    public void Lote_aceita_de_1_a_20_eventos()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NfeManifestProtocol.BuildEnvelope(1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NfeManifestProtocol.BuildEnvelope(1, Enumerable.Repeat("<evento/>", 21).ToList()));
        Assert.Throws<ArgumentOutOfRangeException>(() => NfeManifestProtocol.BuildEnvelope(0, ["<evento/>"]));
    }

    [Fact]
    public void Resposta_da_sefaz_e_lida_por_nota_e_respostas_invalidas_sao_recusadas()
    {
        var response = EventResponse(128, "Lote de evento processado",
            (Key(1), 135, "Evento registrado e vinculado a NF-e"), (Key(2), 596, "Rejeicao: Evento apresentado fora do prazo"));
        ValidateAgainst("retEnvConfRecebto_v1.00.xsd", XDocument.Parse(response).Descendants().Single(e => e.Name.LocalName == "retEnvEvento"));

        var lot = NfeManifestProtocol.ParseResponse(response);

        Assert.Equal((128, 2), (lot.Status, lot.Events.Count));
        Assert.Equal((Key(1), 135, "891260000000000"), (lot.Events[0].AccessKey, lot.Events[0].Status, lot.Events[0].Protocol));
        Assert.Equal(new DateTime(2026, 11, 15, 12, 0, 5, DateTimeKind.Utc), lot.Events[0].RegisteredAtUtc);
        Assert.Equal((Key(2), 596, (string?)null), (lot.Events[1].AccessKey, lot.Events[1].Status, lot.Events[1].Protocol));

        Assert.Equal("sefaz_invalid_response", Assert.Throws<NexFiscalException>(() => NfeManifestProtocol.ParseResponse("não é xml")).Kind);
        Assert.Equal("sefaz_invalid_response", Assert.Throws<NexFiscalException>(() => NfeManifestProtocol.ParseResponse("<a/>")).Kind);
        Assert.Equal("sefaz_fault", Assert.Throws<NexFiscalException>(() => NfeManifestProtocol.ParseResponse(
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><s:Fault/></s:Body></s:Envelope>")).Kind);
        Assert.ThrowsAny<Exception>(() => NfeManifestProtocol.ParseResponse(
            "<!DOCTYPE a [<!ENTITY x \"y\">]><a>&x;</a>")); // DTD proibido
    }

    // ---------- serviço ----------

    private static async Task<CaptureInstallation> WithSummariesAsync(params int[] numbers)
    {
        var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(numbers.Length, numbers.Length, numbers.Select((n, i) => Summary(i + 1, n)).ToArray()));
        await install.CaptureAsync();
        return install;
    }

    private static ManifestRequest Keys(params int[] numbers) => new(numbers.Select(Key).ToList());

    [Fact]
    public async Task Ciencia_e_enviada_so_para_as_notas_marcadas_assinada_com_o_a1_da_empresa_e_fica_registrada()
    {
        using var install = await WithSummariesAsync(201, 202, 203);
        Assert.All(await install.Manifest.PendingAsync(install.CompanyId, Ct), p => Assert.True(p.CanManifest && p.ManifestState is null));
        Assert.Empty(install.Sefaz.EventCalls); // listar nunca envia nada
        install.Sefaz.ReplyEvents();

        var result = await install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(201, 203), Ct);

        Assert.Equal((2, 0, 0), (result.Registered, result.Rejected, result.Unanswered));
        Assert.Contains("Ciência registrada para 2 nota(s)", result.Message);
        var call = Assert.Single(install.Sefaz.EventCalls);
        Assert.Equal((NfeManifestProtocol.ProductionUrl, NfeManifestProtocol.SoapAction), (call.Url, call.Action));
        Assert.Equal([Key(201), Key(203)], call.Keys.Distinct());
        Assert.NotNull(call.Thumbprint); // certificado com chave privada, para o TLS mútuo
        Assert.Contains("<CNPJ>" + TestCertificates.CnpjA + "</CNPJ>", call.Envelope);
        ValidateAgainst("envConfRecebto_v1.00.xsd", NfeManifestProtocol.DataOf(call.Envelope));

        var pending = (await install.Manifest.PendingAsync(install.CompanyId, Ct)).ToDictionary(p => p.AccessKey);
        Assert.Equal(("registered", false), (pending[Key(201)].ManifestState, pending[Key(201)].CanManifest));
        Assert.Contains("Aguardando o XML completo", pending[Key(201)].ManifestMessage);
        Assert.Equal(((string?)null, true), (pending[Key(202)].ManifestState, pending[Key(202)].CanManifest));
        var row = install.ManifestStore.Rows[(install.CompanyId, Key(201), 210210)];
        Assert.Equal((135, "891260000000000"), (row.Status, row.Protocol));
        // A manifestação não mexe em checkpoint, inventário nem arquivos.
        Assert.Equal(3, install.Data.LastNsu);
        Assert.Empty(install.XmlFiles());
    }

    [Fact]
    public async Task Depois_da_ciencia_o_xml_completo_chega_pela_captura_normal_e_a_nota_sai_das_pendencias()
    {
        using var install = await WithSummariesAsync(201);
        install.Sefaz.ReplyEvents();
        await install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(201), Ct);

        install.Clock.Now += TimeSpan.FromHours(2);
        install.Sefaz.Reply(Found(2, 2, Complete(2, 201)));
        var job = await install.CaptureAsync();

        Assert.Equal((CaptureJob.Completed, 1), (job.State, job.NewComplete));
        Assert.Empty(await install.Manifest.PendingAsync(install.CompanyId, Ct));
        Assert.Single(install.XmlFiles());
    }

    [Fact]
    public async Task Respostas_da_sefaz_por_nota_sao_traduzidas_e_recusas_podem_ser_reenviadas()
    {
        using var install = await WithSummariesAsync(201, 202, 203, 204, 205);
        install.Sefaz.ReplyEvents(
            (Key(202), 573, "Rejeicao: Duplicidade de evento"),
            (Key(203), 596, "Rejeicao: Evento apresentado fora do prazo: 10 dias"),
            (Key(204), 650, "Rejeicao: Evento de Ciencia da Operacao para NF-e Cancelada ou Denegada"),
            (Key(205), 655, "Rejeicao: Evento de Ciencia da Operacao informado apos a manifestacao final do destinatario"));

        var result = await install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(201, 202, 203, 204, 205), Ct);

        Assert.Equal((3, 2, 0), (result.Registered, result.Rejected, result.Unanswered));
        var notes = result.Notes.ToDictionary(n => n.AccessKey);
        Assert.Equal("registered", notes[Key(202)].Outcome);
        Assert.Contains("já estava registrada", notes[Key(202)].Message);
        Assert.Contains("prazo", notes[Key(203)].Message);
        Assert.Contains("cancelada ou denegada", notes[Key(204)].Message);
        Assert.Contains("manifestação conclusiva", notes[Key(205)].Message);

        var pending = (await install.Manifest.PendingAsync(install.CompanyId, Ct)).ToDictionary(p => p.AccessKey);
        Assert.Equal(("rejected", true), (pending[Key(203)].ManifestState, pending[Key(203)].CanManifest));

        // Reenvio: só as recusadas vão de novo; as já registradas não são reenviadas.
        install.Sefaz.ReplyEvents();
        var retry = await install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(201, 202, 203, 204, 205), Ct);
        Assert.Equal(2, retry.Registered);
        Assert.Equal([Key(203), Key(204)], install.Sefaz.EventCalls[1].Keys.Distinct());
    }

    [Fact]
    public async Task So_aceita_notas_que_o_nexfiscal_conhece_como_pendentes_e_nunca_chaves_arbitrarias()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(3, 3, Summary(1, 201), Complete(2, 202), Summary(3, 203, situation: 3)));
        await install.CaptureAsync();

        // 999 nunca veio da SEFAZ; 202 já tem XML completo; 203 está cancelada.
        var none = await Assert.ThrowsAsync<NexFiscalException>(() => install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(999, 202, 203), Ct));
        Assert.Equal(("nothing_to_manifest", 409), (none.Kind, none.StatusCode));
        Assert.Equal(400, (await Assert.ThrowsAsync<NexFiscalException>(() => install.Manifest.AcknowledgeAsync(install.CompanyId, null, Ct))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<NexFiscalException>(() =>
            install.Manifest.AcknowledgeAsync(install.CompanyId, new ManifestRequest(Enumerable.Range(1, 101).Select(Key).ToList()), Ct))).StatusCode);
        Assert.Empty(install.Sefaz.EventCalls);

        install.Sefaz.ReplyEvents();
        var mixed = await install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(999, 201, 201, 202), Ct);
        Assert.Equal(1, mixed.Registered);
        Assert.Equal([Key(201)], Assert.Single(install.Sefaz.EventCalls).Keys.Distinct());
    }

    [Fact]
    public async Task Mais_de_20_notas_vao_em_lotes_e_uma_falha_no_meio_preserva_o_que_ja_foi_registrado()
    {
        var numbers = Enumerable.Range(301, 45).ToArray();
        using var install = await WithSummariesAsync(numbers);
        install.Sefaz.ReplyEvents();
        install.Sefaz.EventResponses.Enqueue(_ => throw new NexFiscalException("sefaz_timeout", "A SEFAZ não respondeu a tempo.", 504));

        var result = await install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(numbers), Ct);

        Assert.Equal((20, 0, 25), (result.Registered, result.Rejected, result.Unanswered));
        Assert.Contains("O envio foi interrompido: A SEFAZ não respondeu a tempo.", result.Message);
        Assert.Equal([20, 20], install.Sefaz.EventCalls.Select(c => c.Keys.Distinct().Count())); // o terceiro lote não foi tentado
        Assert.Equal(20, install.ManifestStore.Rows.Count);

        // Nova tentativa: só as 25 restantes; o que a SEFAZ já tinha registrado no lote sem resposta volta como duplicidade.
        install.Sefaz.ReplyEvents((Key(321), 573, "Rejeicao: Duplicidade de evento")).ReplyEvents();
        var retry = await install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(numbers), Ct);
        Assert.Equal((25, 0, 0), (retry.Registered, retry.Rejected, retry.Unanswered));
        Assert.Equal(45, install.ManifestStore.Rows.Values.Count(r => r.State == ManifestRow.Registered));
    }

    [Fact]
    public async Task Lote_nao_processado_ou_falha_no_primeiro_envio_nao_grava_nada_e_explica_o_motivo()
    {
        using var install = await WithSummariesAsync(201);
        install.Sefaz.EventResponses.Enqueue(_ => EventResponse(215, "Rejeicao: Falha no schema XML"));
        var rejected = await Assert.ThrowsAsync<NexFiscalException>(() => install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(201), Ct));
        Assert.Equal("sefaz_rejected", rejected.Kind);
        Assert.Contains("215 - Rejeicao: Falha no schema XML", rejected.Message);

        install.Sefaz.EventResponses.Enqueue(_ => throw new NexFiscalException("sefaz_unreachable", "Não foi possível conectar à SEFAZ.", 503));
        Assert.Equal("sefaz_unreachable",
            (await Assert.ThrowsAsync<NexFiscalException>(() => install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(201), Ct))).Kind);

        // Resposta sem a nota pedida: fica "sem resposta", não é dada como registrada.
        install.Sefaz.EventResponses.Enqueue(_ => EventResponse(128, "Lote de evento processado"));
        var silent = await install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(201), Ct);
        Assert.Equal((0, 0, 1), (silent.Registered, silent.Rejected, silent.Unanswered));
        Assert.Empty(install.ManifestStore.Rows);
        Assert.True(Assert.Single(await install.Manifest.PendingAsync(install.CompanyId, Ct)).CanManifest);
    }

    [Fact]
    public async Task Empresa_arquivada_ou_sem_a1_nao_manifesta_e_registro_aceito_nunca_e_rebaixado()
    {
        using var install = await WithSummariesAsync(201);
        var registered = new ManifestRow(Key(201), 210210, ManifestRow.Registered, 135, "ok", "1", install.Clock.Now.UtcDateTime, install.Clock.Now.UtcDateTime);
        await install.ManifestStore.SaveAsync(install.CompanyId, registered, Ct);
        await install.ManifestStore.SaveAsync(install.CompanyId, registered with { State = ManifestRow.Rejected, Status = 596 }, Ct);
        Assert.Equal(ManifestRow.Registered, install.ManifestStore.Rows[(install.CompanyId, Key(201), 210210)].State);

        await install.Base.Store.ArchiveAsync(install.CompanyId, install.Clock.Now.UtcDateTime, Ct);
        var archived = await Assert.ThrowsAsync<NexFiscalException>(() => install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(201), Ct));
        Assert.Equal("company_archived", archived.Kind);
        Assert.Equal(404, (await Assert.ThrowsAsync<NexFiscalException>(() => install.Manifest.AcknowledgeAsync("nao-existe", Keys(201), Ct))).StatusCode);
        Assert.Empty(install.Sefaz.EventCalls);
    }

    [Fact]
    public async Task Logs_da_manifestacao_nao_contem_senha_certificado_nem_xml()
    {
        using var install = await WithSummariesAsync(201);
        install.Sefaz.ReplyEvents();
        await install.Manifest.AcknowledgeAsync(install.CompanyId, Keys(201), Ct);

        var logs = string.Join("\n", install.Base.Logs.Lines);
        Assert.Contains("Ciência da Operação", logs);
        Assert.DoesNotContain(TestCertificates.Password, logs);
        Assert.DoesNotContain("<evento", logs);
        Assert.DoesNotContain("X509Certificate", logs);
    }

    // ---------- SQL do HANA (sem HANA real: conferência estática) ----------

    [Fact]
    public void Sql_da_manifestacao_so_usa_a_tabela_propria_nunca_apaga_e_nunca_rebaixa_um_registro()
    {
        var columns = HanaSchemaVerifier.Required["BRAI_NEX_NfeManifest"].Select(c => c.Column)
            .Append("BRAI_NEX").Append("BRAI_NEX_NfeManifest").ToHashSet(StringComparer.Ordinal);
        var statements = typeof(HanaManifestStore).Assembly.GetType("NexFiscal.Web.ManifestSql")!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        Assert.Equal(4, statements.Count);

        foreach (var (name, sql) in statements)
        {
            foreach (Match identifier in Regex.Matches(sql, "\"(\\w+)\""))
                Assert.True(columns.Contains(identifier.Groups[1].Value), $"{name}: identificador desconhecido {identifier.Value}");
            Assert.DoesNotMatch(new Regex(@"\b(DELETE|DROP|TRUNCATE|ALTER|CREATE)\b", RegexOptions.IgnoreCase), sql);
            Assert.Contains("\"CompanyId\"", sql);
        }
        Assert.EndsWith("AND \"State\" <> 'registered'", statements["Update"]);
        var insert = Regex.Match(statements["Insert"], "\\(([^)]*)\\) VALUES \\(([^)]*)\\)$");
        Assert.Equal(Regex.Matches(insert.Groups[1].Value, "\"\\w+\"").Count, insert.Groups[2].Value.Split(',').Length);
        Assert.Equal(HanaSchemaVerifier.Required["BRAI_NEX_NfeManifest"].Length, Regex.Matches(insert.Groups[1].Value, "\"\\w+\"").Count);

        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sql", "migrations", "006_BRAI_NEX_nfe_manifest.sql"));
        var body = Regex.Replace(migration, "--.*", "");
        Assert.DoesNotMatch(new Regex(@"\b(DELETE|DROP|TRUNCATE|ALTER|UPDATE)\b", RegexOptions.IgnoreCase), body);
        Assert.Contains("\"BRAI_NEX_NfeManifest\"", HanaSchemaInstaller.CreateStatements.Select(s => "\"" + s.Table + "\""));
    }
}
