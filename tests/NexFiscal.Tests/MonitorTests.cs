using NexFiscal.Web;
using static NexFiscal.Tests.Sefaz;

namespace NexFiscal.Tests;

public class MonitorTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private static Task<MonitorView> EnableAsync(CaptureInstallation install, int minutes = 60, string? folder = null) =>
        install.Monitor.SaveAsync(install.CompanyId, new MonitorRequest(true, minutes, folder ?? install.Folder), Ct);

    // ---------- A01, A02 ----------

    [Fact]
    public async Task Sem_ativacao_explicita_nada_dispara_sozinho()
    {
        using var install = await CaptureInstallation.CreateAsync();

        var view = await install.Monitor.GetAsync(install.CompanyId, Ct);
        install.Clock.Now += TimeSpan.FromDays(3);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));

        Assert.Equal((false, MonitorService.DefaultIntervalMinutes, (DateTime?)null), (view.Enabled, view.IntervalMinutes, view.NextRunAtUtc));
        Assert.Empty(install.Sefaz.Calls);
        Assert.Empty(install.Store.Jobs);
        Assert.Empty(install.Store.Monitors);
    }

    [Fact]
    public async Task Ativar_grava_a_configuracao_por_empresa_e_a_primeira_execucao_usa_o_mesmo_motor()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(2, 2, Complete(1, 101), Summary(2, 201)));

        var saved = await EnableAsync(install, minutes: 90);
        Assert.Equal((true, 90, install.Folder), (saved.Enabled, saved.IntervalMinutes, saved.DestinationDirectory));
        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct));

        var job = Assert.Single(install.Store.Jobs);
        Assert.Equal(("automatic", CaptureJob.CompletedWithPending, 1, 1), (job.Kind, job.State, job.NewComplete, job.Pending));
        Assert.Equal([Key(101) + ".xml"], install.XmlFiles());
        Assert.Equal(2, install.Data.LastNsu);

        var view = await install.Monitor.GetAsync(install.CompanyId, Ct);
        Assert.Equal(install.Clock.Now.UtcDateTime, view.LastRunAtUtc);
        Assert.Equal(install.Clock.Now.UtcDateTime + TimeSpan.FromMinutes(90), view.NextRunAtUtc);
        Assert.Equal(job.JobId, view.LastJob!.JobId);
        Assert.Contains("manifestação", view.LastMessage);
        Assert.Equal(install.Folder, (await install.Service.GetAsync(install.CompanyId, Ct)).DestinationDirectory);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10081)]
    public async Task Intervalo_fora_da_faixa_e_recusado(int minutes)
    {
        using var install = await CaptureInstallation.CreateAsync();
        var error = await Assert.ThrowsAsync<NexFiscalException>(() => EnableAsync(install, minutes));
        Assert.Equal((400, "invalid_interval"), (error.StatusCode, error.Kind));
        Assert.Empty(install.Store.Monitors);
    }

    [Fact]
    public async Task Ativacao_exige_pasta_gravavel_e_certificado_valido_mas_desligar_e_sempre_possivel()
    {
        using var install = await CaptureInstallation.CreateAsync();

        var folder = await Assert.ThrowsAsync<NexFiscalException>(() => EnableAsync(install, folder: "relativa"));
        Assert.Equal("invalid_folder", folder.Kind);

        await EnableAsync(install);
        File.Delete(install.Base.VaultFile(install.CompanyId));
        var certificate = await Assert.ThrowsAsync<NexFiscalException>(() => EnableAsync(install));
        Assert.Equal((409, "certificate_unavailable"), (certificate.StatusCode, certificate.Kind));

        var off = await install.Monitor.SaveAsync(install.CompanyId, new MonitorRequest(false, 60, null), Ct);
        Assert.False(off.Enabled);
        Assert.Equal(install.Folder, off.DestinationDirectory); // a pasta configurada é mantida
        Assert.Null(off.NextRunAtUtc);
    }

    // ---------- A03: a regra da SEFAZ prevalece sobre intervalos curtos ----------

    [Fact]
    public async Task Intervalo_de_um_minuto_nao_antecipa_a_consulta_a_hora_exigida_pela_sefaz_e_respeitada()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(None(0));
        await EnableAsync(install, minutes: 1);
        await install.Worker.RunDueJobsAsync(Ct);
        Assert.Single(install.Sefaz.Calls);

        for (var minute = 1; minute <= 59; minute++)
        {
            install.Clock.Now += TimeSpan.FromMinutes(1);
            Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        }
        Assert.Single(install.Sefaz.Calls);
        Assert.Single(install.Store.Jobs);
        var view = await install.Monitor.GetAsync(install.CompanyId, Ct);
        Assert.Equal(install.Data.NextAllowedAtUtc, view.NextRunAtUtc); // a tela mostra o horário elegível, não o do intervalo

        install.Clock.Now += TimeSpan.FromMinutes(4);
        install.Sefaz.Reply(Found(1, 1, Complete(1, 101)));
        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Equal(2, install.Sefaz.Calls.Count);
        Assert.Equal([Key(101) + ".xml"], install.XmlFiles());
    }

    [Fact]
    public async Task Intervalo_maior_que_uma_hora_e_respeitado()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(None(0));
        await EnableAsync(install, minutes: 180);
        await install.Worker.RunDueJobsAsync(Ct);

        install.Clock.Now += TimeSpan.FromMinutes(120);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        install.Clock.Now += TimeSpan.FromMinutes(61);
        install.Sefaz.Reply(None(0));
        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Equal(2, install.Sefaz.Calls.Count);
    }

    // ---------- A04, A05: independe da tela; sobrevive a reinício ----------

    [Fact]
    public async Task Reinicio_do_backend_recompoe_a_agenda_a_partir_do_banco_sem_antecipar_nem_perder_execucoes()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(None(0));
        await EnableAsync(install);
        await install.Worker.RunDueJobsAsync(Ct);
        var planned = (await install.Monitor.GetAsync(install.CompanyId, Ct)).NextRunAtUtc;

        install.Restart(); // serviço encerrado e iniciado de novo
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct)); // nada dispara só por ter reiniciado
        Assert.Equal(planned, (await install.Monitor.GetAsync(install.CompanyId, Ct)).NextRunAtUtc);

        install.Clock.Now += OneHour + TimeSpan.FromMinutes(5);
        install.Restart(); // parado durante o horário previsto: executa ao voltar
        install.Sefaz.Reply(Found(1, 1, Complete(1, 101)));
        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Equal(2, install.Store.Jobs.Count);
        Assert.All(install.Store.Jobs, job => Assert.Equal("automatic", job.Kind));
    }

    // ---------- A06: manual e automático nunca competem ----------

    [Fact]
    public async Task Com_pedido_manual_em_andamento_o_automatico_espera_e_vice_versa()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(None(0));
        await install.CaptureAsync();                       // manual: consulta e entra no intervalo de 1 hora
        var waiting = await install.CaptureAsync();         // segundo manual: fica aguardando a SEFAZ
        Assert.Equal(CaptureJob.WaitingSefaz, waiting.State);

        await EnableAsync(install, minutes: 1);
        install.Clock.Now += TimeSpan.FromMinutes(30);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        Assert.DoesNotContain(install.Store.Jobs, job => job.Kind == "automatic");

        // Passada a hora: só o pedido manual pendente é executado; o automático não duplica a consulta.
        install.Clock.Now += TimeSpan.FromMinutes(40);
        install.Sefaz.Reply(None(0));
        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Equal(2, install.Sefaz.Calls.Count);
        Assert.DoesNotContain(install.Store.Jobs, job => job.Kind == "automatic");
    }

    [Fact]
    public async Task Clique_manual_enquanto_o_automatico_aguarda_devolve_o_mesmo_pedido()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Blocked());
        await EnableAsync(install);
        await install.Worker.RunDueJobsAsync(Ct);
        var automatic = Assert.Single(install.Store.Jobs);
        Assert.Equal(("automatic", CaptureJob.WaitingSefaz), (automatic.Kind, automatic.State));

        var manual = await install.Service.StartAsync(install.CompanyId, new CaptureRequest(install.Folder), Ct);

        Assert.Equal(automatic.JobId, manual.JobId);
        Assert.Single(install.Store.Jobs);
        Assert.Single(install.Sefaz.Calls);
    }

    // ---------- A07: trocar pasta ou A1 não perde checkpoint ----------

    [Fact]
    public async Task Trocar_pasta_ou_certificado_mantem_o_ponto_de_captura_e_o_historico()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(1, 1, Complete(1, 101)));
        await EnableAsync(install);
        await install.Worker.RunDueJobsAsync(Ct);

        var other = Path.Combine(install.Folder, "nova-pasta");
        await EnableAsync(install, folder: other);
        await install.Companies.UpdateCertificateAsync(install.CompanyId, TestCertificates.Upload(TestCertificates.Create()), Ct);
        install.Clock.Now += OneHour + TimeSpan.FromMinutes(5);
        install.Sefaz.Reply(Found(2, 2, Complete(2, 102)));
        await install.Worker.RunDueJobsAsync(Ct);

        Assert.Equal([1L, 1L], install.Sefaz.Calls.Select(c => c.Nsu).Skip(1).Prepend(1)); // continuou do NSU 1
        Assert.Equal(1L, install.Sefaz.Calls[1].Nsu);
        Assert.Equal(2, install.Data.LastNsu);
        Assert.Equal(2, install.Data.Envelopes.Count);
        // A pasta nova recebe o XML novo e também os já conhecidos, restaurados do HANA.
        Assert.Equal(2, Directory.GetFiles(other, "*.xml").Length);
        Assert.True(File.Exists(install.FileOf(101)));
    }

    // ---------- A08: falhas ----------

    [Fact]
    public async Task Falha_transitoria_e_registrada_e_espera_sem_repetir_em_laco()
    {
        using var install = await CaptureInstallation.CreateAsync();
        for (var i = 0; i < NfeCaptureEngine.MaxTransientAttempts; i++)
            install.Sefaz.Throw(new NexFiscalException("sefaz_unreachable", "Não foi possível conectar à SEFAZ.", 503));
        await EnableAsync(install, minutes: 1);

        await install.Worker.RunDueJobsAsync(Ct);
        for (var minute = 1; minute <= 9; minute++)
        {
            install.Clock.Now += TimeSpan.FromMinutes(1);
            Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        }
        Assert.Single(install.Sefaz.Calls);
        Assert.Single(install.Store.Jobs);
        Assert.Contains("conectar à SEFAZ", (await install.Monitor.GetAsync(install.CompanyId, Ct)).LastMessage);

        for (var i = 1; i < NfeCaptureEngine.MaxTransientAttempts; i++)
        {
            install.Clock.Now += NfeCaptureEngine.TransientRetry;
            await install.Worker.RunDueJobsAsync(Ct);
        }
        Assert.Equal(CaptureJob.Failed, install.Store.Jobs[0].State);
        Assert.Equal(NfeCaptureEngine.MaxTransientAttempts, install.Sefaz.Calls.Count);

        // Mesmo com intervalo de 1 minuto, não abre outro pedido logo após a falha.
        install.Clock.Now += CaptureWorker.FailureBackoff - TimeSpan.FromMinutes(1);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Single(install.Store.Jobs);

        install.Clock.Now += TimeSpan.FromMinutes(2);
        install.Sefaz.Reply(None(0));
        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct)); // volta a tentar depois da espera
        Assert.Equal(2, install.Store.Jobs.Count);
    }

    [Fact]
    public async Task Certificado_vencido_pausa_o_monitoramento_com_motivo_e_sem_criar_pedidos_em_serie()
    {
        using var install = await CaptureInstallation.CreateAsync();
        await EnableAsync(install, minutes: 1);
        install.Clock.Now = TestCertificates.Today.AddDays(400);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
            install.Clock.Now += TimeSpan.FromMinutes(2);
        }

        Assert.Empty(install.Store.Jobs);
        Assert.Empty(install.Sefaz.Calls);
        var view = await install.Monitor.GetAsync(install.CompanyId, Ct);
        Assert.False(view.CanCapture);
        Assert.Contains("vencido", view.LastMessage);
    }

    // ---------- desligar ----------

    [Fact]
    public async Task Desligar_interrompe_os_disparos_sem_apagar_historico_e_o_manual_continua_disponivel()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Found(1, 1, Complete(1, 101)));
        await EnableAsync(install);
        await install.Worker.RunDueJobsAsync(Ct);

        await install.Monitor.SaveAsync(install.CompanyId, new MonitorRequest(false, 60, install.Folder), Ct);
        install.Clock.Now += TimeSpan.FromHours(5);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Single(install.Sefaz.Calls);
        Assert.Single(install.Store.Jobs);
        Assert.Equal(1, install.Data.LastNsu);
        Assert.Single(install.Data.Envelopes);
        Assert.Equal([Key(101) + ".xml"], install.XmlFiles());

        install.Sefaz.Reply(Found(2, 2, Complete(2, 102)));
        var manual = await install.CaptureAsync();
        Assert.Equal(("manual", CaptureJob.Completed, 1), (manual.Kind, manual.State, manual.NewComplete));
    }

    [Fact]
    public async Task Desligar_enquanto_uma_execucao_automatica_aguardava_a_encerra_sem_consultar_a_sefaz()
    {
        using var install = await CaptureInstallation.CreateAsync();
        install.Sefaz.Reply(Blocked());
        await EnableAsync(install);
        await install.Worker.RunDueJobsAsync(Ct);

        await install.Monitor.SaveAsync(install.CompanyId, new MonitorRequest(false, 60, null), Ct);
        install.Clock.Now += OneHour + TimeSpan.FromMinutes(10);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));

        var job = Assert.Single(install.Store.Jobs);
        Assert.Equal(CaptureJob.Completed, job.State);
        Assert.Contains("desativado", job.Message);
        Assert.Single(install.Sefaz.Calls);
    }

    [Fact]
    public async Task Desligar_durante_uma_execucao_nao_e_desfeito_quando_a_execucao_termina()
    {
        using var install = await CaptureInstallation.CreateAsync();
        await EnableAsync(install);
        // A SEFAZ "responde" depois que o operador desligou o monitoramento no meio da execução.
        install.Sefaz.Responses.Enqueue(_ =>
        {
            install.Monitor.SaveAsync(install.CompanyId, new MonitorRequest(false, 45, install.Folder), Ct).GetAwaiter().GetResult();
            return None(0);
        });

        await install.Worker.RunDueJobsAsync(Ct);

        var view = await install.Monitor.GetAsync(install.CompanyId, Ct);
        Assert.Equal((false, 45), (view.Enabled, view.IntervalMinutes));
        install.Clock.Now += TimeSpan.FromHours(3);
        Assert.Equal(0, await install.Worker.RunDueJobsAsync(Ct));
        Assert.Single(install.Sefaz.Calls);
    }

    [Fact]
    public async Task Cada_empresa_tem_agenda_propria_e_so_as_ativadas_sao_monitoradas()
    {
        using var install = await CaptureInstallation.CreateAsync();
        var second = (await install.Companies.RegisterAsync(TestCertificates.Upload(TestCertificates.Create(
            sanCnpj: TestCertificates.CnpjB, commonName: "SEGUNDA EMPRESA SA:" + TestCertificates.CnpjB)), Ct)).Company!.CompanyId;
        install.Sefaz.Reply(None(0));
        await EnableAsync(install);

        Assert.Equal(1, await install.Worker.RunDueJobsAsync(Ct));

        Assert.Equal(TestCertificates.CnpjA, Assert.Single(install.Sefaz.Calls).Cnpj);
        Assert.False((await install.Monitor.GetAsync(second, Ct)).Enabled);
        Assert.DoesNotContain(install.Store.Jobs, job => job.CompanyId == second);
    }
}
