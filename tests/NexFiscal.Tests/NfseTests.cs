using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NexFiscal.Web;
using static NexFiscal.Tests.Adn;

namespace NexFiscal.Tests;

public class NfseTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private static async Task<CaptureJob> CaptureAsync(CaptureInstallation install)
    {
        var view = await install.Nfse.StartAsync(install.CompanyId, new CaptureRequest(install.NfseFolder), Ct);
        await install.Worker.RunDueJobsAsync(Ct);
        return install.Store.Jobs.Single(j => j.JobId == view.JobId);
    }

    private static string[] Files(CaptureInstallation install) =>
        Directory.GetFiles(install.NfseFolder, "*.xml").Select(Path.GetFileName).Order().ToArray()!;

    // ---------- protocolo ----------

    [Fact]
    public void Consulta_segue_o_metodo_oficial_com_cnpj_e_endereco_de_producao()
    {
        var url = NfseProtocol.BuildRequest(TestCertificates.CnpjA, 123);
        Assert.Equal("https://adn.nfse.gov.br/contribuintes/DFe/123?cnpjConsulta=11222333000181&lote=true", url.AbsoluteUri);
        Assert.Throws<ArgumentException>(() => NfseProtocol.BuildRequest("11222333000182", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NfseProtocol.BuildRequest(TestCertificates.CnpjA, -1));
    }

    [Fact]
    public void Resposta_com_lote_e_lida_descompactada_e_tolerante_a_maiusculas_e_tipos()
    {
        var (status, body) = Found(Note(7, 1), Cancel(8, 1));
        var lower = body.Replace("\"LoteDFe\"", "\"loteDFe\"").Replace("\"ArquivoXml\"", "\"arquivoXml\"").Replace("\"NSU\":7", "\"nsu\":\"7\"");

        foreach (var text in new[] { body, lower })
        {
            var response = NfseProtocol.ParseResponse(status, text);
            Assert.Equal(NfseResponseStatus.Found, response.Status);
            Assert.Equal([7L, 8L], response.Documents.Select(d => d.Nsu));
            Assert.Equal(Nfse(Key(1)), Encoding.UTF8.GetString(response.Documents[0].Xml));
            var data = NfseProtocol.ReadNfse(response.Documents[0].Xml)!;
            Assert.Equal((Key(1), TestCertificates.CnpjB, "PRESTADOR EXEMPLO LTDA", 500.00m), (data.AccessKey, data.IssuerId, data.IssuerName, data.TotalValue));
            Assert.Equal(new DateTime(2026, 11, 10, 12, 30, 0, DateTimeKind.Utc), data.IssuedAtUtc);
            Assert.Null(NfseProtocol.ReadNfse(response.Documents[1].Xml));
            Assert.True(NfseProtocol.IsEvent(response.Documents[1].Xml));
            Assert.Equal(Key(1), NfseProtocol.CancelledKeyOf(response.Documents[1].Xml));
        }
    }

    [Fact]
    public void Fila_vazia_rejeicao_e_limite_de_consultas_sao_distinguidos()
    {
        var (s404, b404) = None();
        Assert.Equal(NfseResponseStatus.None, NfseProtocol.ParseResponse(s404, b404).Status);
        Assert.Equal(NfseResponseStatus.None, NfseProtocol.ParseResponse(404, "").Status);
        Assert.Equal(NfseResponseStatus.None, NfseProtocol.ParseResponse(200, "{\"StatusProcessamento\":\"NENHUM_DOCUMENTO_LOCALIZADO\",\"LoteDFe\":[]}").Status);

        var (s400, b400) = Rejected("E2245", "Não há procuração da inscrição X para a inscrição Y.");
        var rejected = NfseProtocol.ParseResponse(s400, b400);
        Assert.Equal((NfseResponseStatus.Rejected, "E2245"), (rejected.Status, rejected.ErrorCode));
        Assert.Contains("procuração", rejected.ErrorMessage);

        var limited = NfseProtocol.ParseResponse(429, "", TimeSpan.FromMinutes(20));
        Assert.Equal((NfseResponseStatus.RateLimited, TimeSpan.FromMinutes(20)), (limited.Status, limited.RetryAfter!.Value));
    }

    [Theory]
    [InlineData(200, "isto não é json")]
    [InlineData(200, "{\"StatusProcessamento\":\"DOCUMENTOS_LOCALIZADOS\"}")]
    [InlineData(200, "{\"LoteDFe\":[{\"NSU\":1}]}")]
    [InlineData(200, "{\"LoteDFe\":[{\"NSU\":1,\"ArquivoXml\":\"@@nao-base64@@\"}]}")]
    [InlineData(302, "")]
    public void Resposta_em_formato_inesperado_vira_erro_claro_e_nao_lote(int status, string body)
    {
        var error = Assert.Throws<NexFiscalException>(() => NfseProtocol.ParseResponse(status, body));
        Assert.Equal("adn_invalid_response", error.Kind);
        Assert.Contains("formato inesperado", error.Message);
    }

    [Theory]
    [InlineData("<NFSe xmlns=\"http://www.sped.fazenda.gov.br/nfse\"/>")]
    [InlineData("<NFSe xmlns=\"http://www.sped.fazenda.gov.br/nfse\"><infNFSe Id=\"NFS123\"/></NFSe>")]
    [InlineData("<NFSe><infNFSe Id=\"NFS11444777000161000000000000000000000000000000000001\"/></NFSe>")]
    [InlineData("xml quebrado <")]
    public void Xml_que_nao_e_nfse_integra_nao_e_aceito(string xml) => Assert.Null(NfseProtocol.ReadNfse(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void Chave_de_50_digitos_e_a_unica_aceita_como_nome_de_arquivo_de_nfse()
    {
        Assert.True(NfseProtocol.IsAccessKey(Key(1)));
        Assert.False(NfseProtocol.IsAccessKey(Key(1)[..49]));
        Assert.False(NfseProtocol.IsAccessKey(Key(1)[..49] + "A"));
        Assert.EndsWith(Key(1) + ".xml", NfeXmlFolder.PathFor(Path.GetTempPath(), Key(1)));
        Assert.Throws<ArgumentException>(() => NfeXmlFolder.PathFor(Path.GetTempPath(), Key(1) + "0"));
    }

    // ---------- captura ----------

    [Fact]
    public async Task Nfse_novas_sao_gravadas_uma_por_chave_e_o_checkpoint_avanca_ate_a_fila_esvaziar()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Adn.Reply(Found(Note(1, 1), Note(2, 2))).Reply(Found(Note(3, 3), Cancel(4, 1))).Reply(None());

        var job = await CaptureAsync(install);

        Assert.Equal((CaptureJob.ManualNfse, CaptureJob.Completed), (job.Kind, job.State));
        Assert.Equal((3, 0, 0, 1, 0, 0), (job.NewComplete, job.AlreadyPresent, job.Recovered, job.Ignored, job.Pending, job.Failures));
        Assert.Equal([Key(1) + ".xml", Key(2) + ".xml", Key(3) + ".xml"], Files(install));
        Assert.Equal(Nfse(Key(2)), File.ReadAllText(Path.Combine(install.NfseFolder, Key(2) + ".xml")));
        Assert.Equal([0L, 2L, 4L], install.Adn.Calls.Select(c => c.Nsu));
        Assert.All(install.Adn.Calls, c => Assert.True(c.Cnpj == TestCertificates.CnpjA && c.HasPrivateKey));

        var data = install.NfseStore.Of(install.CompanyId);
        Assert.Equal(4, data.LastNsu);
        Assert.Equal(4, data.Envelopes.Count);
        Assert.True(data.Documents[Key(1)].Cancelled);
        Assert.True(data.NextAllowedAtUtc >= install.Clock.Now.UtcDateTime + OneHour);
        Assert.Equal(3, data.Log.Count);
    }

    [Fact]
    public async Task Nfe_e_nfse_tem_checkpoints_pedidos_e_pastas_independentes()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Sefaz.Found(5, 5, Sefaz.Complete(5, 101)));
        install.Adn.Reply(Found(Note(9, 1))).Reply(None());

        var nfe = await install.CaptureAsync();
        var nfse = await CaptureAsync(install);

        Assert.Equal(("manual", 1), (nfe.Kind, nfe.NewComplete));
        Assert.Equal((CaptureJob.ManualNfse, 1), (nfse.Kind, nfse.NewComplete));
        Assert.Equal(5, install.Data.LastNsu);
        Assert.Equal(9, install.NfseStore.Of(install.CompanyId).LastNsu);
        Assert.Equal([Sefaz.Key(101) + ".xml"], install.XmlFiles());
        Assert.Equal([Key(1) + ".xml"], Files(install));
        // Cada tela enxerga só o seu pedido.
        Assert.Equal(nfe.JobId, (await install.Service.GetAsync(install.CompanyId, Ct)).Job!.JobId);
        Assert.Equal(nfse.JobId, (await install.Nfse.GetCaptureAsync(install.CompanyId, Ct)).Job!.JobId);
    }

    [Fact]
    public async Task Arquivo_ja_presente_nao_duplica_e_arquivo_apagado_ou_corrompido_volta_do_hana_sem_consultar_o_adn()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Adn.Reply(Found(Note(1, 1), Note(2, 2))).Reply(None());
        await CaptureAsync(install);
        File.Delete(Path.Combine(install.NfseFolder, Key(1) + ".xml"));
        File.WriteAllText(Path.Combine(install.NfseFolder, Key(2) + ".xml"), "<NFSe>truncada");

        var job = await CaptureAsync(install); // dentro da hora

        Assert.Equal(CaptureJob.WaitingSefaz, job.State);
        Assert.Equal((0, 2), (job.NewComplete, job.Recovered));
        Assert.Equal(2, Files(install).Length);
        Assert.Equal(Nfse(Key(2)), File.ReadAllText(Path.Combine(install.NfseFolder, Key(2) + ".xml")));
        Assert.Single(Directory.GetFiles(install.NfseFolder), f => f.Contains(".invalido-"));
        Assert.Equal(2, install.Adn.Calls.Count);
    }

    [Fact]
    public async Task Fila_vazia_respeita_a_espera_e_o_pedido_e_retomado_sozinho()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Adn.Reply(None());
        Assert.Equal(CaptureJob.Completed, (await CaptureAsync(install)).State);

        install.Clock.Now += TimeSpan.FromMinutes(10);
        var waiting = await CaptureAsync(install);
        Assert.Equal(CaptureJob.WaitingSefaz, waiting.State);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Single(install.Adn.Calls);

        install.Clock.Now += OneHour;
        install.Adn.Reply(Found(Note(1, 1))).Reply(None());
        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Equal((CaptureJob.Completed, 1), (install.Store.Jobs.Single(j => j.JobId == waiting.JobId).State, install.Store.Jobs.Single(j => j.JobId == waiting.JobId).NewComplete));
    }

    [Fact]
    public async Task Limite_de_consultas_429_pausa_pelo_tempo_indicado_sem_repetir()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Adn.Reply((429, ""), retryAfter: TimeSpan.FromMinutes(20));

        var job = await CaptureAsync(install);

        Assert.Equal((CaptureJob.WaitingSefaz, 429), (job.State, job.LastStatus));
        Assert.Equal(install.Clock.Now.UtcDateTime + TimeSpan.FromMinutes(20), job.NextRunAtUtc);
        install.Clock.Now += TimeSpan.FromMinutes(19);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Single(install.Adn.Calls);
        Assert.Equal(0, install.NfseStore.Of(install.CompanyId).LastNsu);
    }

    [Fact]
    public async Task Rejeicao_do_adn_encerra_com_o_codigo_e_mantem_o_ponto_de_captura()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.NfseStore.Of(install.CompanyId).LastNsu = 30;
        install.Adn.Reply(Rejected("E2243", "O CNPJ informado na consulta deve ter o mesmo CNPJ base do contribuinte solicitante."));

        var job = await CaptureAsync(install);

        Assert.Equal(CaptureJob.Failed, job.State);
        Assert.Contains("E2243", job.Message);
        Assert.Equal(30, install.NfseStore.Of(install.CompanyId).LastNsu);
        Assert.Single(install.Adn.Calls);
    }

    [Fact]
    public async Task Falha_do_hana_depois_de_gravar_os_arquivos_nao_avanca_o_nsu_e_a_nova_tentativa_nao_duplica()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.NfseStore.FailNextSave = true;
        install.Adn.Reply(Found(Note(1, 1)));

        var failed = await CaptureAsync(install);
        Assert.Equal(CaptureJob.Failed, failed.State);
        Assert.Equal(0, install.NfseStore.Of(install.CompanyId).LastNsu);
        Assert.Single(Files(install));

        install.Adn.Reply(Found(Note(1, 1))).Reply(None());
        var retry = await CaptureAsync(install);
        Assert.Equal((CaptureJob.Completed, 0, 1), (retry.State, retry.NewComplete, retry.AlreadyPresent));
        Assert.Equal(1, install.NfseStore.Of(install.CompanyId).LastNsu);
        Assert.Single(Directory.GetFiles(install.NfseFolder));
    }

    [Fact]
    public async Task Documento_irreconhecivel_ou_com_chave_divergente_nao_vira_arquivo_e_lote_sem_nsu_nao_entra_em_laco()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Adn.Reply(Found(new Doc(1, Key(9), "NFSE", Nfse(Key(1))), new Doc(2, null, "OUTRO", "<x/>")));
        install.Adn.Reply(Found(new Doc(null, Key(3), "NFSE", Nfse(Key(3)))));

        var job = await CaptureAsync(install);

        Assert.Equal((1, 2), (job.NewComplete, job.Failures));
        Assert.Equal(CaptureJob.CompletedWithPending, job.State);
        Assert.Equal([Key(3) + ".xml"], Files(install));
        Assert.Equal(2, install.Adn.Calls.Count); // sem NSU para continuar: grava e para, em vez de repetir a consulta
        Assert.Equal(2, install.NfseStore.Of(install.CompanyId).LastNsu);
    }

    [Fact]
    public async Task Pedido_duplicado_pasta_invalida_e_certificado_indisponivel_sao_barrados_antes_do_adn()
    {
        using var install = await CaptureInstallation.CreateAsync();
        var first = await install.Nfse.StartAsync(install.CompanyId, new CaptureRequest(install.NfseFolder), Ct);
        var second = await install.Nfse.StartAsync(install.CompanyId, new CaptureRequest(install.NfseFolder), Ct);
        Assert.Equal(first.JobId, second.JobId);

        var folder = await Assert.ThrowsAsync<NexFiscalException>(() => install.Nfse.StartAsync(install.CompanyId, new CaptureRequest("relativa"), Ct));
        Assert.Equal("invalid_folder", folder.Kind);

        File.Delete(install.Base.VaultFile(install.CompanyId));
        install.Store.Jobs.Clear();
        var certificate = await Assert.ThrowsAsync<NexFiscalException>(() => install.Nfse.StartAsync(install.CompanyId, new CaptureRequest(install.NfseFolder), Ct));
        Assert.Equal("certificate_unavailable", certificate.Kind);
        Assert.Empty(install.Adn.Calls);
    }

    // ---------- monitoramento ----------

    [Fact]
    public async Task Monitoramento_de_nfse_e_independente_do_de_nfe_vem_desligado_e_respeita_a_espera()
    {
        using var install = await CaptureInstallation.CreateAsync();
        Assert.False((await install.Nfse.GetMonitorAsync(install.CompanyId, Ct)).Enabled);
        install.Clock.Now += TimeSpan.FromDays(2);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));

        install.Adn.Reply(Found(Note(1, 1))).Reply(None());
        var saved = await install.Nfse.SaveMonitorAsync(install.CompanyId, new MonitorRequest(true, 1, install.NfseFolder), Ct);
        Assert.True(saved.Enabled);
        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct));

        var job = Assert.Single(install.Store.Jobs);
        Assert.Equal((CaptureJob.AutomaticNfse, CaptureJob.Completed, 1), (job.Kind, job.State, job.NewComplete));
        Assert.False((await install.Monitor.GetAsync(install.CompanyId, Ct)).Enabled); // o da NF-e continua desligado
        Assert.Empty(install.Sefaz.Calls);

        for (var minute = 1; minute <= 59; minute++)
        {
            install.Clock.Now += TimeSpan.FromMinutes(1);
            Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        }
        Assert.Equal(2, install.Adn.Calls.Count);
        var view = await install.Nfse.GetMonitorAsync(install.CompanyId, Ct);
        Assert.Equal(install.NfseStore.Of(install.CompanyId).NextAllowedAtUtc, view.NextRunAtUtc);
        Assert.Equal(job.JobId, view.LastJob!.JobId);

        await install.Nfse.SaveMonitorAsync(install.CompanyId, new MonitorRequest(false, 1, null), Ct);
        install.Clock.Now += TimeSpan.FromHours(3);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Equal(install.NfseFolder, (await install.Nfse.GetMonitorAsync(install.CompanyId, Ct)).DestinationDirectory);
    }

    [Fact]
    public async Task Captura_manual_lembra_a_pasta_sem_ligar_o_monitoramento()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Adn.Reply(None());
        await CaptureAsync(install);

        var monitor = await install.Nfse.GetMonitorAsync(install.CompanyId, Ct);
        Assert.Equal((false, install.NfseFolder), (monitor.Enabled, monitor.DestinationDirectory));
        Assert.Equal(install.NfseFolder, (await install.Nfse.GetCaptureAsync(install.CompanyId, Ct)).DestinationDirectory);
        install.Clock.Now += TimeSpan.FromDays(1);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
    }

    // ---------- SQL do HANA (sem HANA real: conferência estática) ----------

    [Fact]
    public void Sql_da_nfse_so_usa_tabelas_e_colunas_das_migracoes_separa_o_tipo_de_documento_e_nunca_apaga()
    {
        var known = HanaSchemaVerifier.Required.Keys
            .Concat(HanaSchemaVerifier.Required.Values.SelectMany(columns => columns.Select(c => c.Column)))
            .Append("BRAI_NEX").ToHashSet(StringComparer.Ordinal);
        var statements = typeof(HanaNfseStore).Assembly.GetType("NexFiscal.Web.NfseSql")!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        Assert.True(statements.Count >= 18);

        foreach (var (name, sql) in statements)
        {
            foreach (Match identifier in Regex.Matches(sql, "\"(\\w+)\""))
                Assert.True(known.Contains(identifier.Groups[1].Value), $"{name}: identificador desconhecido {identifier.Value}");
            Assert.DoesNotMatch(new Regex(@"\b(DELETE|DROP|TRUNCATE|ALTER|CREATE)\b", RegexOptions.IgnoreCase), sql);
            Assert.DoesNotContain("'NFE'", sql); // nunca toca no cursor ou nos envelopes da NF-e
            if (sql.Contains("\"DocumentType\"")) Assert.Contains("'NFSE'", sql);
            if (sql.StartsWith("UPDATE")) Assert.Contains("WHERE", sql);
            if (!sql.StartsWith("INSERT")) continue;
            var insert = Regex.Match(sql, "^INSERT INTO \"BRAI_NEX\"\\.\"(\\w+)\" \\(([^)]*)\\) VALUES \\(([^)]*)\\)$");
            Assert.True(insert.Success, name);
            var columns = Regex.Matches(insert.Groups[2].Value, "\"(\\w+)\"").Select(m => m.Groups[1].Value).ToArray();
            Assert.All(columns, column => Assert.Contains(HanaSchemaVerifier.Required[insert.Groups[1].Value], c => c.Column == column));
            Assert.Equal(columns.Length, insert.Groups[3].Value.Split(',').Length);
        }
        Assert.EndsWith("AND \"LastNsu\" < ?", statements["AdvanceCursor"]);
    }
}
