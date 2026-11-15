using System.Reflection;
using System.Text.RegularExpressions;
using NexFiscal.Web;
using static NexFiscal.Tests.Sefaz;

namespace NexFiscal.Tests;

public class CaptureEngineTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    // ---------- N01, N09: distribuição ----------

    [Fact]
    public async Task Documentos_novos_sao_gravados_um_arquivo_por_chave_e_o_checkpoint_avanca()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(3, 3, Complete(1, 101), Complete(2, 102), Complete(3, 103)));

        var job = await install.CaptureAsync();

        Assert.Equal(CaptureJob.Completed, job.State);
        Assert.Equal((3, 0, 0, 0, 0, 0), (job.NewComplete, job.AlreadyPresent, job.Recovered, job.Ignored, job.Pending, job.Failures));
        Assert.Contains("nesta janela foram processados", job.Message);
        Assert.Equal([Key(101) + ".xml", Key(102) + ".xml", Key(103) + ".xml"], install.XmlFiles());
        Assert.Equal(ProcNFe(Key(102)), File.ReadAllText(install.FileOf(102))); // bytes originais, sem alteração
        Assert.Equal(3, install.Data.LastNsu);
        Assert.Equal(3, install.Data.Envelopes.Count);
        Assert.True(install.Data.NextAllowedAtUtc >= install.Clock.Now.UtcDateTime + OneHour);

        var call = Assert.Single(install.Sefaz.Calls);
        Assert.Equal(("distNSU", 0L, TestCertificates.CnpjA), (call.Operation, call.Nsu, call.Cnpj));
        Assert.Equal(NfeProtocol.ProductionUrl, call.Url);
        Assert.NotNull(call.Thumbprint); // autenticou com o A1 da empresa
        Assert.Equal("distNSU", Assert.Single(install.Data.Log).Result);
    }

    [Fact]
    public async Task Varios_lotes_seguem_sempre_o_ultNSU_devolvido_ate_alcancar_o_maxNSU()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz
            .Reply(Found(2, 5, Complete(1, 101), Complete(2, 102)))
            .Reply(Found(4, 5, Complete(3, 103), Summary(4, 104)))
            .Reply(Found(5, 5, Complete(5, 105)));

        var job = await install.CaptureAsync();

        Assert.Equal([0L, 2L, 4L], install.Sefaz.Calls.Select(c => c.Nsu));
        Assert.All(install.Sefaz.Calls, c => Assert.Equal("distNSU", c.Operation));
        Assert.Equal((4, 1), (job.NewComplete, job.Ignored));
        Assert.Equal(5, install.Data.LastNsu);
        Assert.Equal(4, install.XmlFiles().Length);
    }

    // ---------- N02, N03 ----------

    [Fact]
    public async Task Xml_completo_ja_presente_na_pasta_nao_e_duplicado_nem_sobrescrito()
    {
        using var install = await CaptureInstallation.CreateAsync();
        File.WriteAllText(install.FileOf(101), ProcNFe(Key(101)));
        var written = File.GetLastWriteTimeUtc(install.FileOf(101));
        install.Sefaz.Reply(Found(1, 1, Complete(1, 101)));

        var job = await install.CaptureAsync();

        Assert.Equal((0, 1), (job.NewComplete, job.AlreadyPresent));
        Assert.Equal(written, File.GetLastWriteTimeUtc(install.FileOf(101)));
        Assert.Single(Directory.GetFiles(install.Folder));
    }

    [Fact]
    public async Task Resumo_nao_conta_como_xml_e_fica_registrado_como_pendencia_de_manifestacao()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(2, 2, Summary(1, 201), Summary(2, 202, situation: 3)));

        var job = await install.CaptureAsync();

        Assert.Equal(CaptureJob.CompletedWithPending, job.State);
        Assert.Equal((0, 2, 1), (job.NewComplete, job.Ignored, job.Pending)); // a cancelada não é pendência
        Assert.Contains("manifestação", job.Message);
        Assert.Empty(install.XmlFiles());
        var pending = Assert.Single(await install.Service.PendingAsync(install.CompanyId, Ct));
        Assert.Equal((Key(201), "FORNECEDOR EXEMPLO LTDA", 1234.56m), (pending.AccessKey, pending.IssuerName, pending.TotalValue));
        Assert.Equal(1, (await install.Service.GetAsync(install.CompanyId, Ct)).PendingSummaries);
    }

    [Fact]
    public async Task Resumo_seguido_do_xml_completo_deixa_de_ser_pendencia_e_cancelamento_tambem()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(4, 4, Summary(1, 201), Summary(2, 202), Complete(3, 201), Cancel(4, 202)));

        var job = await install.CaptureAsync();

        Assert.Equal(CaptureJob.Completed, job.State);
        Assert.Equal((1, 3, 0), (job.NewComplete, job.Ignored, job.Pending));
        Assert.Equal([Key(201) + ".xml"], install.XmlFiles());
        Assert.Equal(3, install.Data.Documents[Key(202)].Situation);
    }

    // ---------- N04, N05: lacunas conhecidas ----------

    [Fact]
    public async Task Lacuna_dentro_da_sequencia_recebida_e_consultada_pontualmente_e_so_ela()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz
            .Reply(Found(5, 5, Complete(1, 101), Complete(2, 102), Complete(5, 105)))
            .Reply(Found(5, 5, Complete(3, 103)))  // consNSU 3
            .Reply(None(5));                       // consNSU 4: não existe no Ambiente Nacional

        var job = await install.CaptureAsync();

        Assert.Equal([("distNSU", 0L), ("consNSU", 3L), ("consNSU", 4L)], install.Sefaz.Calls.Select(c => (c.Operation, c.Nsu)));
        Assert.Equal((3, 1, 0), (job.NewComplete, job.Recovered, job.Pending));
        Assert.Equal(4, install.XmlFiles().Length);
        Assert.Equal("recovered", install.Data.Recovery[3].State);
        Assert.Equal("absent", install.Data.Recovery[4].State);
        Assert.Equal(5, install.Data.LastNsu); // consNSU não mexe no checkpoint

        // Lacuna já resolvida nunca é consultada de novo.
        install.Clock.Now += OneHour + TimeSpan.FromMinutes(5);
        install.Sefaz.Reply(None(5));
        await install.CaptureAsync();
        Assert.Equal(("distNSU", 5L), (install.Sefaz.Calls[^1].Operation, install.Sefaz.Calls[^1].Nsu));
        Assert.Equal(4, install.Sefaz.Calls.Count);
    }

    [Fact]
    public async Task Nunca_ha_varredura_de_nsu_sem_lacuna_nenhuma_consulta_pontual_e_feita()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Data.LastNsu = 500; // checkpoint antigo: o salto até o primeiro documento não é lacuna recuperável
        install.Sefaz.Reply(Found(902, 902, Complete(900, 101), Complete(901, 102), Complete(902, 103)));

        await install.CaptureAsync();

        Assert.Equal([("distNSU", 500L)], install.Sefaz.Calls.Select(c => (c.Operation, c.Nsu)));
        Assert.Empty(install.Data.Recovery);
    }

    [Fact]
    public async Task Consultas_pontuais_respeitam_o_limite_por_hora_e_o_numero_de_tentativas()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(30, 30, Complete(1, 101), Complete(30, 130))); // 28 NSUs faltantes
        for (var i = 0; i < 28; i++) install.Sefaz.Reply(Response(999, "Rejeicao: Erro nao catalogado", null, null));

        var job = await install.CaptureAsync();

        Assert.Equal(NfeCaptureEngine.RecoveryQueriesPerHour, install.Sefaz.Calls.Count(c => c.Operation == "consNSU"));
        Assert.Equal(CaptureJob.CompletedWithPending, job.State);
        Assert.Equal(28, job.Pending);

        // Dentro da mesma hora: nenhuma consulta pontual a mais.
        var before = install.Sefaz.Calls.Count;
        install.Clock.Now += TimeSpan.FromMinutes(30);
        await install.CaptureAsync();
        Assert.Equal(before, install.Sefaz.Calls.Count);
    }

    // ---------- N06, N13: arquivos locais ----------

    [Fact]
    public async Task Arquivo_com_nome_certo_mas_conteudo_corrompido_e_preservado_como_evidencia_e_regravado()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(1, 1, Complete(1, 101)));
        await install.CaptureAsync();
        File.WriteAllText(install.FileOf(101), "<nfeProc>truncado");

        var job = await install.CaptureAsync(); // dentro da hora: só o inventário local trabalha

        Assert.Equal((0, 0, 1), (job.NewComplete, job.AlreadyPresent, job.Recovered));
        Assert.Equal(ProcNFe(Key(101)), File.ReadAllText(install.FileOf(101)));
        var evidence = Assert.Single(Directory.GetFiles(install.Folder), f => f.Contains(".invalido-"));
        Assert.Equal("<nfeProc>truncado", File.ReadAllText(evidence));
        Assert.Single(install.Sefaz.Calls);
    }

    [Fact]
    public async Task Xml_capturado_antes_e_apagado_da_pasta_e_restaurado_sem_consultar_a_sefaz()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(2, 2, Complete(1, 101), Complete(2, 102)));
        await install.CaptureAsync();
        File.Delete(install.FileOf(101));

        var job = await install.CaptureAsync();

        Assert.Equal(CaptureJob.WaitingSefaz, job.State); // a SEFAZ só pode ser consultada daqui a 1 hora
        Assert.Equal((1, 1), (job.Recovered, job.AlreadyPresent));
        Assert.Equal(2, install.XmlFiles().Length);
        Assert.Single(install.Sefaz.Calls);
    }

    [Fact]
    public async Task Xmls_completos_ja_guardados_no_hana_por_versao_antiga_viram_arquivos_individuais()
    {
        using var install = await CaptureInstallation.CreateAsync();
        foreach (var (nsu, number) in new[] { (10L, 301), (11L, 302) })
        {
            var xml = System.Text.Encoding.UTF8.GetBytes(ProcNFe(Key(number)));
            install.Data.Envelopes[nsu] = new EnvelopeRow(nsu, "procNFe_v4.00.xsd",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(xml)), xml);
        }
        install.Data.Envelopes[12] = new EnvelopeRow(12, "resNFe_v1.01.xsd", "00", System.Text.Encoding.UTF8.GetBytes(ResNFe(Key(303))));
        install.Data.LastNsu = 12; // o checkpoint antigo é mantido
        install.Sefaz.Reply(None(12));

        var job = await install.CaptureAsync();

        Assert.Equal((0, 2), (job.NewComplete, job.Recovered));
        Assert.Equal([Key(301) + ".xml", Key(302) + ".xml"], install.XmlFiles());
        Assert.Equal(("distNSU", 12L), (install.Sefaz.Calls[0].Operation, install.Sefaz.Calls[0].Nsu));
        Assert.Equal(12, install.Data.LastNsu);
    }

    // ---------- N07, N12: pedido durável ----------

    [Fact]
    public async Task Pedido_duplicado_enquanto_ha_um_ativo_devolve_o_mesmo_pedido()
    {
        using var install = await CaptureInstallation.CreateAsync();
        var first = await install.Service.StartAsync(install.CompanyId, new CaptureRequest(install.Folder), Ct);
        var second = await install.Service.StartAsync(install.CompanyId, new CaptureRequest(install.Folder), Ct);

        Assert.Equal(first.JobId, second.JobId);
        Assert.Single(install.Store.Jobs);
        Assert.Empty(install.Sefaz.Calls);
    }

    [Fact]
    public async Task Pedido_interrompido_por_queda_do_backend_e_retomado_ao_reiniciar_sem_perda_nem_duplicacao()
    {
        using var install = await CaptureInstallation.CreateAsync();
        var view = await install.Service.StartAsync(install.CompanyId, new CaptureRequest(install.Folder), Ct);
        // O backend caiu no meio: o pedido ficou gravado como "em execução" e um XML já estava na pasta.
        install.Store.Jobs[0] = install.Store.Jobs[0] with { State = CaptureJob.Running, Attempts = 1 };
        File.WriteAllText(install.FileOf(101), ProcNFe(Key(101)));

        install.Restart();
        install.Sefaz.Reply(Found(2, 2, Complete(1, 101), Complete(2, 102)));
        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct));

        var job = install.Store.Jobs.Single(j => j.JobId == view.JobId);
        Assert.Equal(CaptureJob.Completed, job.State);
        Assert.Equal((1, 1), (job.NewComplete, job.AlreadyPresent));
        Assert.Equal(2, install.XmlFiles().Length);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
    }

    // ---------- N08, N10, N14: intervalos oficiais ----------

    [Fact]
    public async Task Sem_documentos_137_para_registra_o_intervalo_e_nao_consulta_de_novo_antes_de_uma_hora()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Data.LastNsu = 40;
        install.Sefaz.Reply(None(40));

        var first = await install.CaptureAsync();
        Assert.Equal(CaptureJob.Completed, first.State);
        Assert.Single(install.Sefaz.Calls);

        // Novo clique 20 minutos depois: nada é enviado à SEFAZ; o pedido fica aguardando sozinho.
        install.Clock.Now += TimeSpan.FromMinutes(20);
        var second = await install.CaptureAsync();
        Assert.Equal(CaptureJob.WaitingSefaz, second.State);
        Assert.Equal(install.Data.NextAllowedAtUtc, second.NextRunAtUtc);
        Assert.Contains("sem novo clique", second.Message);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Single(install.Sefaz.Calls);

        // Passada a hora, a fila retoma o mesmo pedido sem intervenção do operador.
        install.Clock.Now += OneHour;
        install.Sefaz.Reply(Found(41, 41, Complete(41, 141)));
        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct));
        var resumed = install.Store.Jobs.Single(j => j.JobId == second.JobId);
        Assert.Equal((CaptureJob.Completed, 1), (resumed.State, resumed.NewComplete));
        Assert.Equal([40L, 40L], install.Sefaz.Calls.Select(c => c.Nsu));
    }

    [Fact]
    public async Task Consumo_indevido_656_pausa_por_uma_hora_sem_repetir_a_consulta_e_sem_mexer_no_nsu()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Data.LastNsu = 40;
        install.Sefaz.Reply(Blocked(77));

        var job = await install.CaptureAsync();

        Assert.Equal((CaptureJob.WaitingSefaz, 656), (job.State, job.LastStatus));
        Assert.Contains("pausou as consultas", job.Message);
        Assert.Equal(40, install.Data.LastNsu); // o NSU devolvido no 656 não é adotado automaticamente
        Assert.True(job.NextRunAtUtc >= install.Clock.Now.UtcDateTime + OneHour);
        Assert.Single(install.Sefaz.Calls);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        install.Clock.Now += TimeSpan.FromMinutes(59);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Single(install.Sefaz.Calls);
    }

    [Fact]
    public async Task Bloqueio_656_repetido_encerra_o_pedido_em_vez_de_insistir_indefinidamente()
    {
        using var install = await CaptureInstallation.CreateAsync();
        for (var i = 0; i < NfeCaptureEngine.MaxTransientAttempts; i++) install.Sefaz.Reply(Blocked());

        var job = await install.CaptureAsync();
        for (var i = 1; i < NfeCaptureEngine.MaxTransientAttempts; i++)
        {
            install.Clock.Now += OneHour + TimeSpan.FromMinutes(5);
            await install.Worker.RunDueJobsAsync(Ct);
        }

        job = install.Store.Jobs.Single(j => j.JobId == job.JobId);
        Assert.False(job.Active);
        Assert.Equal(NfeCaptureEngine.MaxTransientAttempts, install.Sefaz.Calls.Count);
        Assert.Contains("outro sistema", job.Message);
    }

    [Fact]
    public async Task Primeiro_acesso_sem_documentos_e_informado_com_honestidade()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(None(0));

        var job = await install.CaptureAsync();

        Assert.Equal(CaptureJob.Completed, job.State);
        Assert.Equal(0, job.NewComplete);
        Assert.Contains("não é retroativa", job.Message);
        Assert.DoesNotContain("recuperad", job.Message);
        Assert.Empty(install.XmlFiles());
    }

    // ---------- N11: falhas entre arquivo e checkpoint ----------

    [Fact]
    public async Task Falha_do_hana_depois_de_gravar_os_arquivos_nao_avanca_o_nsu_e_a_nova_tentativa_nao_duplica()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Store.FailNextSave = true;
        install.Sefaz.Reply(Found(2, 2, Complete(1, 101), Complete(2, 102)));

        var failed = await install.CaptureAsync();

        Assert.Equal(CaptureJob.Failed, failed.State);
        Assert.Equal(0, install.Data.LastNsu);
        Assert.Empty(install.Data.Envelopes);
        Assert.Equal(2, install.XmlFiles().Length); // os XMLs não se perderam

        install.Sefaz.Reply(Found(2, 2, Complete(1, 101), Complete(2, 102)));
        var retry = await install.CaptureAsync();

        Assert.Equal(CaptureJob.Completed, retry.State);
        Assert.Equal((0, 2), (retry.NewComplete, retry.AlreadyPresent));
        Assert.Equal([0L, 0L], install.Sefaz.Calls.Select(c => c.Nsu));
        Assert.Equal(2, install.Data.LastNsu);
        Assert.Equal(2, Directory.GetFiles(install.Folder).Length);
    }

    [Fact]
    public async Task Falha_de_disco_ao_gravar_o_xml_nao_avanca_o_nsu()
    {
        using var install = await CaptureInstallation.CreateAsync();
        Directory.CreateDirectory(install.FileOf(102)); // impede a gravação de <chave>.xml
        install.Sefaz.Reply(Found(2, 2, Complete(1, 101), Complete(2, 102)));

        var job = await install.CaptureAsync();

        Assert.Equal(CaptureJob.Failed, job.State);
        Assert.Contains("O ponto de captura não avançou", job.Message);
        Assert.Equal(0, install.Data.LastNsu);
        Assert.Empty(install.Data.Envelopes);
    }

    // ---------- rejeições e falhas transitórias ----------

    [Fact]
    public async Task Rejeicao_da_sefaz_encerra_com_o_motivo_e_mantem_o_ponto_de_captura()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Data.LastNsu = 40;
        install.Sefaz.Reply(Response(593, "Rejeicao: CNPJ-Base consultado difere do CNPJ-Base do Certificado Digital", null, null));

        var job = await install.CaptureAsync();

        Assert.Equal(CaptureJob.Failed, job.State);
        Assert.Contains("código 593", job.Message);
        Assert.Equal(40, install.Data.LastNsu);
        Assert.Single(install.Sefaz.Calls);
        Assert.Equal(593, install.Data.Log[0].Status);
    }

    [Fact]
    public async Task Falha_transitoria_e_tentada_de_novo_mais_tarde_e_desiste_apos_o_limite()
    {
        using var install = await CaptureInstallation.CreateAsync();
        for (var i = 0; i < NfeCaptureEngine.MaxTransientAttempts; i++)
            install.Sefaz.Throw(new NexFiscalException("sefaz_timeout", "A SEFAZ não respondeu a tempo.", 504));

        var job = await install.CaptureAsync();
        Assert.Equal(CaptureJob.WaitingSefaz, job.State);
        Assert.Equal(install.Clock.Now.UtcDateTime + NfeCaptureEngine.TransientRetry, job.NextRunAtUtc);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct)); // não martela a SEFAZ

        for (var i = 1; i < NfeCaptureEngine.MaxTransientAttempts; i++)
        {
            install.Clock.Now += NfeCaptureEngine.TransientRetry;
            await install.Worker.RunDueJobsAsync(Ct);
        }
        job = install.Store.Jobs.Single();
        Assert.Equal(CaptureJob.Failed, job.State);
        Assert.Equal(NfeCaptureEngine.MaxTransientAttempts, install.Sefaz.Calls.Count);
        Assert.Equal(0, install.Data.LastNsu);
    }

    [Fact]
    public async Task ProcNFe_com_estrutura_invalida_nao_conta_como_xml_completo()
    {
        using var install = await CaptureInstallation.CreateAsync();
        var mismatched = ProcNFe(Key(101)).Replace("<chNFe>" + Key(101), "<chNFe>" + Key(999));
        install.Sefaz.Reply(Found(2, 2, new Doc(1, "procNFe_v4.00.xsd", mismatched), new Doc(2, "procNFe_v4.00.xsd", "<nfeProc/>")));

        var job = await install.CaptureAsync();

        Assert.Equal((0, 2), (job.NewComplete, job.Failures));
        Assert.Equal(CaptureJob.CompletedWithPending, job.State);
        Assert.Empty(install.XmlFiles());
        Assert.Equal(2, install.Data.Envelopes.Count); // guardados como evidência
        Assert.Empty(install.Data.Documents);
    }

    // ---------- pré-condições: nada disso chega à SEFAZ ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pasta-relativa")]
    public async Task Pasta_invalida_e_recusada_antes_de_criar_o_pedido(string? folder)
    {
        using var install = await CaptureInstallation.CreateAsync();
        var error = await Assert.ThrowsAsync<NexFiscalException>(() => install.Service.StartAsync(install.CompanyId, new CaptureRequest(folder), Ct));
        Assert.Equal((400, "invalid_folder"), (error.StatusCode, error.Kind));
        Assert.Empty(install.Store.Jobs);
        Assert.Empty(install.Sefaz.Calls);
    }

    [Fact]
    public async Task Pasta_de_destino_e_criada_lembrada_e_validada_para_escrita()
    {
        using var install = await CaptureInstallation.CreateAsync();
        var nested = Path.Combine(install.Folder, "2026", "entrada");
        install.Sefaz.Reply(None(0));

        await install.CaptureAsync(nested);

        Assert.True(Directory.Exists(nested));
        Assert.Empty(Directory.GetFiles(nested)); // o arquivo de teste de escrita não fica para trás
        Assert.Equal(nested, (await install.Service.GetAsync(install.CompanyId, Ct)).DestinationDirectory);
    }

    [Fact]
    public async Task Certificado_vencido_ausente_ou_empresa_arquivada_bloqueiam_a_captura()
    {
        using var install = await CaptureInstallation.CreateAsync();

        install.Clock.Now = TestCertificates.Today.AddDays(400);
        var expired = await Assert.ThrowsAsync<NexFiscalException>(() => install.Service.StartAsync(install.CompanyId, new CaptureRequest(install.Folder), Ct));
        Assert.Contains("vencido", expired.Message);
        var view = await install.Service.GetAsync(install.CompanyId, Ct);
        Assert.False(view.CanCapture);
        install.Clock.Now = TestCertificates.Today;

        File.Delete(install.Base.VaultFile(install.CompanyId));
        var missing = await Assert.ThrowsAsync<NexFiscalException>(() => install.Service.StartAsync(install.CompanyId, new CaptureRequest(install.Folder), Ct));
        Assert.Equal("certificate_unavailable", missing.Kind);

        var unknown = await Assert.ThrowsAsync<NexFiscalException>(() => install.Service.StartAsync("nao-existe", new CaptureRequest(install.Folder), Ct));
        Assert.Equal(404, unknown.StatusCode);
        Assert.Empty(install.Store.Jobs);
        Assert.Empty(install.Sefaz.Calls);
    }

    [Fact]
    public async Task Senha_e_conteudo_do_certificado_nunca_aparecem_em_log_ou_no_pedido()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(1, 1, Complete(1, 101)));
        var job = await install.CaptureAsync();

        Assert.DoesNotContain(install.Base.Logs.Lines, line => line.Contains(TestCertificates.Password));
        var json = System.Text.Json.JsonSerializer.Serialize(CaptureJobView.From(job));
        Assert.DoesNotContain("Nsu", json);
        Assert.DoesNotContain("assword", json);
    }

    // ---------- SQL do HANA (sem HANA real: conferência estática) ----------

    [Fact]
    public void Sql_da_captura_so_usa_tabelas_e_colunas_das_migracoes_e_nunca_apaga_nem_retrocede()
    {
        var known = HanaSchemaVerifier.Required.Keys
            .Concat(HanaSchemaVerifier.Required.Values.SelectMany(columns => columns.Select(c => c.Column)))
            .Append("BRAI_NEX").ToHashSet(StringComparer.Ordinal);
        var statements = typeof(HanaCaptureStore).Assembly.GetType("NexFiscal.Web.CaptureSql")!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        Assert.True(statements.Count >= 25);

        foreach (var (name, sql) in statements)
        {
            foreach (Match identifier in Regex.Matches(sql, "\"(\\w+)\""))
                Assert.True(known.Contains(identifier.Groups[1].Value), $"{name}: identificador desconhecido {identifier.Value}");
            Assert.DoesNotMatch(new Regex(@"\b(DELETE|DROP|TRUNCATE|ALTER|CREATE)\b", RegexOptions.IgnoreCase), sql);
            if (sql.StartsWith("UPDATE")) Assert.Contains("WHERE", sql);
            // Cada tabela citada precisa conter as colunas usadas junto dela em INSERTs.
            var insert = Regex.Match(sql, "^INSERT INTO \"BRAI_NEX\"\\.\"(\\w+)\" \\(([^)]*)\\) VALUES \\(([^)]*)\\)$");
            if (!sql.StartsWith("INSERT")) continue;
            Assert.True(insert.Success, name);
            var columns = Regex.Matches(insert.Groups[2].Value, "\"(\\w+)\"").Select(m => m.Groups[1].Value).ToArray();
            Assert.All(columns, column => Assert.Contains(HanaSchemaVerifier.Required[insert.Groups[1].Value], c => c.Column == column));
            Assert.Equal(columns.Length, insert.Groups[3].Value.Split(',').Length);
        }
        // O checkpoint só avança: a condição "LastNsu" < ? impede retrocesso, e nenhum comando zera o NSU.
        Assert.EndsWith("AND \"LastNsu\" < ?", statements["AdvanceCursor"]);
        Assert.DoesNotContain(statements.Where(s => s.Key != "AdvanceCursor" && s.Key != "InsertCursor").Select(s => s.Value),
            sql => Regex.IsMatch(sql, "SET.*\"LastNsu\" ="));
        Assert.All(statements.Values.Where(s => s.Contains("\"DocumentType\"")), sql => Assert.Contains("'NFE'", sql));
    }
}
