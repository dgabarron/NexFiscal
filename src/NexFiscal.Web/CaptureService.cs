namespace NexFiscal.Web;

public sealed record CaptureRequest(string? DestinationDirectory);

/// <summary>Pedido de captura como a tela vê. Não expõe NSU.</summary>
public sealed record CaptureJobView(
    string JobId, string State, string DestinationDirectory, DateTime RequestedAtUtc, DateTime? StartedAtUtc, DateTime? FinishedAtUtc,
    DateTime? NextRunAtUtc, int NewComplete, int AlreadyPresent, int Recovered, int Ignored, int Pending, int Failures, string? Message)
{
    public static CaptureJobView From(CaptureJob job) => new(job.JobId, job.State, job.DestinationDirectory, job.RequestedAtUtc,
        job.StartedAtUtc, job.FinishedAtUtc, job.NextRunAtUtc, job.NewComplete, job.AlreadyPresent, job.Recovered, job.Ignored,
        job.Pending, job.Failures, job.Message);
}

public sealed record CaptureView(
    string CompanyId, string? DestinationDirectory, bool CanCapture, string? BlockedReason,
    DateTime? NextAllowedAtUtc, int PendingSummaries, CaptureJobView? Job);

/// <summary>NF-e conhecida apenas pelo resumo, aguardando a manifestação do destinatário.</summary>
public sealed record PendingNfeView(string AccessKey, string? IssuerName, string? IssuerId, DateTime? IssuedAtUtc, decimal? TotalValue);

/// <summary>
/// Função 3 da V1: captura manual de todos os XMLs completos de NF-e disponíveis e ainda ausentes.
/// O pedido é durável: é gravado no HANA e executado pelo serviço em segundo plano.
/// </summary>
public sealed class CaptureService(
    ICaptureStore store, CompanyService companies, CaptureSignal signal, TimeProvider clock)
{
    private static readonly SemaphoreSlim StartLock = new(1, 1);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<CaptureView> GetAsync(string companyId, CancellationToken ct)
    {
        var (company, view) = await companies.GetAsync(companyId, ct);
        var blocked = company.ArchivedAtUtc is not null ? "Empresa arquivada. Reative-a na seção Empresas."
            : view.CertificateStatus != "stored" ? view.CertificateMessage : null;
        var cursor = await store.GetCursorAsync(companyId, ct);
        var job = await store.LatestJobAsync(companyId, ct);
        var pending = (await store.ListDocumentsAsync(companyId, ct)).Count(NfeCaptureEngine.IsPendingSummary);
        return new CaptureView(companyId, await store.GetDestinationAsync(companyId, ct) ?? job?.DestinationDirectory,
            blocked is null, blocked, cursor.NextAllowedAtUtc > Now ? cursor.NextAllowedAtUtc : null, pending,
            job is null ? null : CaptureJobView.From(job));
    }

    /// <summary>Cria o pedido de captura. Se já houver um em andamento para a empresa, devolve esse mesmo pedido.</summary>
    public async Task<CaptureJobView> StartAsync(string companyId, CaptureRequest? request, CancellationToken ct)
    {
        var (company, view) = await companies.GetAsync(companyId, ct);
        if (company.ArchivedAtUtc is not null)
            throw new NexFiscalException("company_archived", "Esta empresa está arquivada. Reative-a na seção Empresas para capturar.", 409);
        if (view.CertificateStatus != "stored")
            throw new NexFiscalException("certificate_unavailable", "Captura bloqueada: " + view.CertificateMessage, 409);
        // Validar a pasta antes de qualquer contato com a SEFAZ.
        var directory = NfeXmlFolder.Prepare(request?.DestinationDirectory);

        await StartLock.WaitAsync(ct);
        try
        {
            var latest = await store.LatestJobAsync(companyId, ct);
            if (latest is { Active: true }) return CaptureJobView.From(latest);
            await store.SetDestinationAsync(companyId, directory, Now, ct);
            var job = new CaptureJob(Guid.NewGuid().ToString(), companyId, "manual", CaptureJob.Queued, directory, Now);
            await store.InsertJobAsync(job, ct);
            signal.Notify();
            return CaptureJobView.From(job);
        }
        finally { StartLock.Release(); }
    }

    public async Task<IReadOnlyList<PendingNfeView>> PendingAsync(string companyId, CancellationToken ct)
    {
        await companies.GetAsync(companyId, ct);
        return (await store.ListDocumentsAsync(companyId, ct)).Where(NfeCaptureEngine.IsPendingSummary)
            .OrderByDescending(d => d.IssuedAtUtc).Take(200)
            .Select(d => new PendingNfeView(d.AccessKey, d.IssuerName, d.IssuerId, d.IssuedAtUtc, d.TotalValue)).ToList();
    }
}

public sealed record MonitorRequest(bool Enabled, int IntervalMinutes, string? DestinationDirectory);

/// <summary>Monitoramento automático como a tela vê. Não expõe NSU.</summary>
public sealed record MonitorView(
    string CompanyId, bool Enabled, int IntervalMinutes, string? DestinationDirectory, bool CanCapture, string? BlockedReason,
    DateTime? NextRunAtUtc, DateTime? LastRunAtUtc, string? LastMessage, CaptureJobView? LastJob);

/// <summary>
/// Função 4 da V1: monitoramento automático de NF-e por empresa. Só liga por opção explícita do operador.
/// A configuração fica no HANA; a execução usa o mesmo motor e a mesma fila da captura manual.
/// </summary>
public sealed class MonitorService(ICaptureStore store, CompanyService companies, CaptureSignal signal, TimeProvider clock)
{
    public const int DefaultIntervalMinutes = 60;
    public const int MaxIntervalMinutes = 7 * 24 * 60;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<MonitorView> GetAsync(string companyId, CancellationToken ct)
    {
        var (company, view) = await companies.GetAsync(companyId, ct);
        var blocked = Blocked(company, view);
        var row = await store.GetMonitorAsync(companyId, ct);
        var job = await store.LatestJobOfKindAsync(companyId, "automatic", ct);
        DateTime? next = null;
        if (row is { Enabled: true })
        {
            // O intervalo é a periodicidade desejada; a regra da SEFAZ prevalece sobre intervalos curtos.
            var allowed = (await store.GetCursorAsync(companyId, ct)).NextAllowedAtUtc;
            next = row.NextRunAtUtc is { } planned && planned > allowed ? planned : allowed;
            if (next < Now) next = Now;
        }
        return new MonitorView(companyId, row?.Enabled ?? false, row?.IntervalMinutes ?? DefaultIntervalMinutes,
            row?.DestinationDirectory ?? await store.GetDestinationAsync(companyId, ct), blocked is null, blocked,
            next, row?.LastRunAtUtc, row?.LastMessage, job is null ? null : CaptureJobView.From(job));
    }

    public async Task<MonitorView> SaveAsync(string companyId, MonitorRequest? request, CancellationToken ct)
    {
        if (request is null) throw new NexFiscalException("invalid_input", "Informe a configuração do monitoramento.", 400);
        if (request.IntervalMinutes is < 1 or > MaxIntervalMinutes)
            throw new NexFiscalException("invalid_interval", "Informe o intervalo em minutos, de 1 a 10080 (uma semana).", 400);
        var (company, view) = await companies.GetAsync(companyId, ct);
        var current = await store.GetMonitorAsync(companyId, ct);

        string directory;
        if (request.Enabled)
        {
            if (Blocked(company, view) is { } blocked)
                throw new NexFiscalException("certificate_unavailable", "Não é possível ativar o monitoramento: " + blocked, 409);
            directory = NfeXmlFolder.Prepare(request.DestinationDirectory);
        }
        else
            // Desligar nunca é impedido por causa da pasta ou do certificado.
            directory = string.IsNullOrWhiteSpace(request.DestinationDirectory)
                ? current?.DestinationDirectory ?? "" : request.DestinationDirectory.Trim();

        var turningOn = request.Enabled && current is not { Enabled: true };
        await store.SaveMonitorAsync(new MonitorRow(companyId, request.Enabled, request.IntervalMinutes, directory,
            request.Enabled ? (turningOn ? Now : current!.NextRunAtUtc ?? Now) : null,
            current?.LastRunAtUtc, turningOn ? null : current?.LastMessage), Now, ct);
        if (request.Enabled) await store.SetDestinationAsync(companyId, directory, Now, ct);
        signal.Notify();
        return await GetAsync(companyId, ct);
    }

    internal static string? Blocked(CompanyRecord company, CompanyView view) =>
        company.ArchivedAtUtc is not null ? "Empresa arquivada. Reative-a na seção Empresas."
        : view.CertificateStatus != "stored" ? view.CertificateMessage : null;
}

/// <summary>Acorda o serviço de segundo plano quando um pedido é criado.</summary>
public sealed class CaptureSignal
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    public void Notify() => _signal.Release();
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Executa os pedidos de captura e o monitoramento automático enquanto o backend estiver ativo.
/// Ao iniciar, retoma pedidos que ficaram pela metade e recompõe a agenda a partir do banco;
/// pedidos em espera pela SEFAZ são retomados sozinhos no horário permitido.
/// </summary>
public sealed class CaptureWorker(
    ICaptureStore store, NfeCaptureEngine engine, INfseStore nfseStore, NfseCaptureEngine nfseEngine, CompanyService companies,
    CaptureSignal signal, TimeProvider clock, ILogger<CaptureWorker> logger)
    : BackgroundService
{
    /// <summary>Espera mínima depois de uma execução automática que falhou ou não pôde começar.</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _pass = new(1, 1);
    private bool _resumed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunDueJobsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (NexFiscalException ex) { logger.LogDebug("Fila de captura indisponível: {Kind}", ex.Kind); }
            catch (Exception ex) { logger.LogError("Falha inesperada na fila de captura: {Type}", ex.GetType().Name); }
            try { await signal.WaitAsync(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Uma passada: executa cada pedido vencido e depois dispara o monitoramento das empresas que estão no horário.
    /// Tudo em série — nunca há duas consultas simultâneas para o mesmo CNPJ, seja manual ou automática.
    /// </summary>
    public async Task<int> RunDueJobsAsync(CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            var executed = 0;
            foreach (var job in await store.ListActiveJobsAsync(ct))
            {
                // "running" só existe aqui se o backend caiu no meio da execução: retomar com segurança.
                var interrupted = job.State == CaptureJob.Running && !_resumed;
                var due = job.State == CaptureJob.Queued || interrupted ||
                          (job.State == CaptureJob.WaitingSefaz && (job.NextRunAtUtc is null || job.NextRunAtUtc <= Now));
                if (!due) continue;
                var monitor = !job.IsAutomatic ? null
                    : job.IsNfse ? await nfseStore.GetMonitorAsync(job.CompanyId, ct) : await store.GetMonitorAsync(job.CompanyId, ct);
                if (job.IsAutomatic && monitor is not { Enabled: true })
                {
                    // O operador desligou o monitoramento enquanto esta execução aguardava: encerrar sem consultar a SEFAZ.
                    await store.UpdateJobAsync(job with
                    {
                        State = CaptureJob.Completed, FinishedAtUtc = Now, NextRunAtUtc = null,
                        Message = "Monitoramento automático desativado; execução encerrada sem nova consulta à SEFAZ."
                    }, ct);
                    continue;
                }
                if (job.IsNfse) await nfseEngine.RunAsync(job, ct); else await engine.RunAsync(job, ct);
                executed++;
            }
            _resumed = true;
            return executed + await RunMonitorsAsync(ct) + await RunNfseMonitorsAsync(ct);
        }
        finally { _pass.Release(); }
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<int> RunMonitorsAsync(CancellationToken ct)
    {
        var executed = 0;
        foreach (var monitor in await store.ListEnabledMonitorsAsync(ct))
        {
            // Mesma exclusão da captura manual: com um pedido em andamento para a empresa, o automático espera.
            var latest = await store.LatestJobAsync(monitor.CompanyId, ct);
            if (latest is { Active: true }) continue;
            // Depois de uma execução automática que falhou, esperar antes de tentar outra: nada de laço de consultas.
            if (latest is { Kind: "automatic", State: CaptureJob.Failed, FinishedAtUtc: { } failedAt } && failedAt + FailureBackoff > Now) continue;
            var cursor = await store.GetCursorAsync(monitor.CompanyId, ct);
            // Nunca antes do horário liberado pela SEFAZ, por menor que seja o intervalo configurado.
            if ((monitor.NextRunAtUtc ?? Now) > Now || cursor.NextAllowedAtUtc > Now) continue;

            string? blocked;
            try
            {
                var (company, view) = await companies.GetAsync(monitor.CompanyId, ct);
                blocked = MonitorService.Blocked(company, view);
            }
            catch (NexFiscalException ex) when (ex.Kind == "company_not_found") { blocked = "Empresa não encontrada."; }
            if (blocked is not null)
            {
                await store.SetMonitorScheduleAsync(monitor.CompanyId, Now + Backoff(monitor), monitor.LastRunAtUtc, "Monitoramento em espera: " + blocked, Now, ct);
                continue;
            }

            var job = new CaptureJob(Guid.NewGuid().ToString(), monitor.CompanyId, "automatic", CaptureJob.Queued, monitor.DestinationDirectory, Now);
            await store.InsertJobAsync(job, ct);
            var result = await engine.RunAsync(job, ct);
            executed++;

            var next = Now + TimeSpan.FromMinutes(monitor.IntervalMinutes);
            if (result.State == CaptureJob.Failed && next < Now + FailureBackoff) next = Now + FailureBackoff;
            // Só a agenda é gravada aqui: se o operador mudou a configuração durante a execução, a escolha dele prevalece.
            await store.SetMonitorScheduleAsync(monitor.CompanyId, next, Now, result.Message, Now, ct);
        }
        return executed;
    }

    /// <summary>Mesma agenda da NF-e, aplicada à NFS-e. As duas consultam serviços diferentes e têm checkpoints próprios.</summary>
    private async Task<int> RunNfseMonitorsAsync(CancellationToken ct)
    {
        var executed = 0;
        foreach (var monitor in await nfseStore.ListEnabledMonitorsAsync(ct))
        {
            var latest = await store.LatestNfseJobAsync(monitor.CompanyId, ct);
            if (latest is { Active: true }) continue;
            if (latest is { Kind: CaptureJob.AutomaticNfse, State: CaptureJob.Failed, FinishedAtUtc: { } failedAt } && failedAt + FailureBackoff > Now) continue;
            var cursor = await nfseStore.GetCursorAsync(monitor.CompanyId, ct);
            if ((monitor.NextRunAtUtc ?? Now) > Now || cursor.NextAllowedAtUtc > Now) continue;

            string? blocked;
            try
            {
                var (company, view) = await companies.GetAsync(monitor.CompanyId, ct);
                blocked = MonitorService.Blocked(company, view);
            }
            catch (NexFiscalException ex) when (ex.Kind == "company_not_found") { blocked = "Empresa não encontrada."; }
            if (blocked is not null)
            {
                await nfseStore.SetMonitorScheduleAsync(monitor.CompanyId, Now + Backoff(monitor), monitor.LastRunAtUtc, "Monitoramento em espera: " + blocked, Now, ct);
                continue;
            }

            var job = new CaptureJob(Guid.NewGuid().ToString(), monitor.CompanyId, CaptureJob.AutomaticNfse, CaptureJob.Queued, monitor.DestinationDirectory, Now);
            await store.InsertJobAsync(job, ct);
            var result = await nfseEngine.RunAsync(job, ct);
            executed++;

            var next = Now + TimeSpan.FromMinutes(monitor.IntervalMinutes);
            if (result.State == CaptureJob.Failed && next < Now + FailureBackoff) next = Now + FailureBackoff;
            await nfseStore.SetMonitorScheduleAsync(monitor.CompanyId, next, Now, result.Message, Now, ct);
        }
        return executed;
    }

    private static TimeSpan Backoff(MonitorRow monitor) =>
        TimeSpan.FromMinutes(Math.Max(monitor.IntervalMinutes, FailureBackoff.TotalMinutes));
}
