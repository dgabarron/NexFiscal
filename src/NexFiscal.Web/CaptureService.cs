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

/// <summary>Acorda o serviço de segundo plano quando um pedido é criado.</summary>
public sealed class CaptureSignal
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    public void Notify() => _signal.Release();
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Executa os pedidos de captura enquanto o backend estiver ativo. Ao iniciar, retoma pedidos que
/// ficaram pela metade; pedidos em espera pela SEFAZ são retomados sozinhos no horário permitido.
/// </summary>
public sealed class CaptureWorker(
    ICaptureStore store, NfeCaptureEngine engine, CaptureSignal signal, TimeProvider clock, ILogger<CaptureWorker> logger)
    : BackgroundService
{
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

    /// <summary>Uma passada pela fila: executa cada pedido vencido, um por vez (nunca dois para o mesmo CNPJ).</summary>
    public async Task<int> RunDueJobsAsync(CancellationToken ct)
    {
        // Uma passada por vez: garante que o mesmo pedido nunca seja executado duas vezes em paralelo.
        await _pass.WaitAsync(ct);
        try
        {
            var jobs = await store.ListActiveJobsAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var executed = 0;
            foreach (var job in jobs)
            {
                // "running" só existe aqui se o backend caiu no meio da execução: retomar com segurança.
                var interrupted = job.State == CaptureJob.Running && !_resumed;
                var due = job.State == CaptureJob.Queued || interrupted ||
                          (job.State == CaptureJob.WaitingSefaz && (job.NextRunAtUtc is null || job.NextRunAtUtc <= now));
                if (!due) continue;
                await engine.RunAsync(job, ct);
                executed++;
            }
            _resumed = true;
            return executed;
        }
        finally { _pass.Release(); }
    }
}
