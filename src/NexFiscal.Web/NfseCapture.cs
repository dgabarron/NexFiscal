using System.Security.Cryptography.X509Certificates;

namespace NexFiscal.Web;

/// <summary>
/// Motor de captura de NFS-e nacional pelo ADN. Mesma disciplina da NF-e: arquivo antes do checkpoint,
/// NSU que só avança, espera gravada no HANA e pedido durável. Na NFS-e todo documento distribuído ao
/// contribuinte já é o XML completo — não existe "resumo aguardando manifestação".
/// </summary>
public sealed class NfseCaptureEngine(
    INfseStore store, ICaptureStore jobs, CompanyService companies, IAdnTransport adn, TimeProvider clock, ILogger<NfseCaptureEngine> logger)
{
    public const int MaxLotsPerRun = 400;
    private static readonly HashSet<string> TransientKinds = new(StringComparer.Ordinal)
    {
        "adn_timeout", "adn_unreachable", "adn_http_error", "unreachable", "connection_refused", "connection_timeout", "host_not_found"
    };

    private sealed class Counters { public int New, Already, Recovered, Ignored, Failures; }
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<CaptureJob> RunAsync(CaptureJob job, CancellationToken ct)
    {
        job = job with { State = CaptureJob.Running, StartedAtUtc = job.StartedAtUtc ?? Now, Attempts = job.Attempts + 1, NextRunAtUtc = null };
        await jobs.UpdateJobAsync(job, ct);
        var counters = new Counters { New = job.NewComplete, Recovered = job.Recovered, Ignored = job.Ignored, Failures = job.Failures };
        try { job = await ExecuteAsync(job, counters, ct); }
        catch (NexFiscalException ex)
        {
            var transient = TransientKinds.Contains(ex.Kind) && job.Attempts < NfeCaptureEngine.MaxTransientAttempts;
            logger.LogWarning("Captura de NFS-e {JobId} interrompida: {Kind} (nova tentativa: {Retry})", job.JobId, ex.Kind, transient);
            job = Apply(job, counters) with
            {
                State = transient ? CaptureJob.WaitingSefaz : CaptureJob.Failed,
                NextRunAtUtc = transient ? Now + NfeCaptureEngine.TransientRetry : null,
                FinishedAtUtc = transient ? null : Now,
                Message = transient ? ex.Message + " Nova tentativa automática em alguns minutos." : ex.Message
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Captura de NFS-e {JobId} falhou de forma inesperada: {Type}", job.JobId, ex.GetType().Name);
            job = Apply(job, counters) with
            {
                State = CaptureJob.Failed, FinishedAtUtc = Now,
                Message = "Falha inesperada durante a captura de NFS-e. O ponto de captura não avançou além do que foi gravado; tente novamente."
            };
        }
        await jobs.UpdateJobAsync(job, CancellationToken.None);
        return job;
    }

    private async Task<CaptureJob> ExecuteAsync(CaptureJob job, Counters counters, CancellationToken ct)
    {
        var (company, view) = await companies.GetAsync(job.CompanyId, ct);
        if (MonitorService.Blocked(company, view) is { } blocked)
            throw new NexFiscalException("certificate_unavailable", "Captura bloqueada: " + blocked, 409);
        var directory = NfeXmlFolder.Prepare(job.DestinationDirectory);

        // 1) Inventário local, sem consultar o ADN: arquivo faltando ou corrompido volta da cópia íntegra no HANA.
        counters.Already = 0;
        foreach (var document in await store.ListDocumentsAsync(company.CompanyId, ct))
        {
            if (FileState(directory, document.AccessKey, document.Sha256) == XmlFileState.Valid) { counters.Already++; continue; }
            var xml = document.Nsu is { } nsu ? await store.ReadEnvelopeAsync(company.CompanyId, nsu, ct) : null;
            if (xml is null || NfseProtocol.ReadNfse(xml)?.AccessKey != document.AccessKey) { counters.Failures++; continue; }
            WriteFile(directory, document.AccessKey, xml, document.Sha256);
            counters.Recovered++;
        }

        // 2) Respeitar a espera gravada.
        var cursor = await store.GetCursorAsync(company.CompanyId, ct);
        if (cursor.NextAllowedAtUtc > Now)
            return Apply(job, counters) with
            {
                State = CaptureJob.WaitingSefaz, NextRunAtUtc = cursor.NextAllowedAtUtc,
                Message = "Arquivos locais conferidos. A próxima consulta ao ADN da NFS-e ocorre no horário indicado; a captura continua sozinha, sem novo clique."
            };

        // 3) Distribuição sequencial a partir do checkpoint.
        var lastNsu = cursor.LastNsu;
        using var certificate = companies.OpenCertificate(company.CompanyId);
        for (var lot = 0; lot < MaxLotsPerRun; lot++)
        {
            var response = await QueryAsync(company, certificate, lastNsu, ct);
            switch (response.Status)
            {
                case NfseResponseStatus.Found:
                    var persistence = Process(response.Documents, directory, counters);
                    var newLast = response.Documents.Max(d => d.Nsu) ?? lastNsu;
                    var stuck = newLast <= lastNsu; // sem NSU ou sem avanço: gravar e parar, para não repetir a mesma consulta
                    await store.SaveLotAsync(company.CompanyId, persistence with
                    {
                        LastNsu = stuck ? null : newLast, NextAllowedAtUtc = stuck ? Now + NfseProtocol.WaitAfterEmpty : null
                    }, Now, ct);
                    if (stuck) return Finish(job, counters, "Os documentos recebidos foram gravados, mas o ADN não informou a sequência para continuar. Nova consulta em 1 hora.");
                    lastNsu = newLast;
                    continue;

                case NfseResponseStatus.None:
                    await store.SaveLotAsync(company.CompanyId, new NfseLot([], [], [], null, Now + NfseProtocol.WaitAfterEmpty), Now, ct);
                    return Finish(job, counters, "Todas as NFS-e disponibilizadas pelo ADN até agora foram processadas.");

                case NfseResponseStatus.RateLimited:
                    var wait = response.RetryAfter is { } delay && delay > TimeSpan.Zero && delay < TimeSpan.FromHours(6) ? delay : NfseProtocol.WaitAfterEmpty;
                    await store.SaveLotAsync(company.CompanyId, new NfseLot([], [], [], null, Now + wait), Now, ct);
                    if (job.Attempts >= NfeCaptureEngine.MaxTransientAttempts)
                        return Finish(job, counters, "O ADN limitou as consultas deste CNPJ repetidas vezes. Tente novamente mais tarde.", failed: true);
                    return Apply(job, counters) with
                    {
                        State = CaptureJob.WaitingSefaz, LastStatus = 429, NextRunAtUtc = Now + wait,
                        Message = "O ADN da NFS-e limitou as consultas deste CNPJ. A captura continua sozinha no horário indicado, sem repetir a consulta antes."
                    };

                default:
                    throw new NexFiscalException("adn_rejected",
                        $"O ADN da NFS-e rejeitou a consulta ({response.ErrorCode}): {response.ErrorMessage} Nada foi alterado e o ponto de captura foi mantido.", 502);
            }
        }
        return Finish(job, counters, "A captura de NFS-e atingiu o limite de lotes desta execução. Capture novamente para continuar de onde parou.");
    }

    private CaptureJob Finish(CaptureJob job, Counters counters, string message, bool failed = false)
    {
        if (counters.Failures > 0) message += $" {counters.Failures} item(ns) com falha; veja os detalhes e tente novamente depois.";
        return Apply(job, counters) with
        {
            State = failed ? CaptureJob.Failed : counters.Failures > 0 ? CaptureJob.CompletedWithPending : CaptureJob.Completed,
            FinishedAtUtc = Now, Message = message
        };
    }

    private static CaptureJob Apply(CaptureJob job, Counters c) => job with
    {
        NewComplete = c.New, AlreadyPresent = c.Already, Recovered = c.Recovered, Ignored = c.Ignored, Failures = c.Failures, Pending = 0
    };

    private async Task<NfseDistributionResponse> QueryAsync(CompanyRecord company, X509Certificate2 certificate, long nsu, CancellationToken ct)
    {
        var started = Now;
        try
        {
            var (status, body, retryAfter) = await adn.GetAsync(NfseProtocol.BuildRequest(company.Cnpj, nsu), certificate, ct);
            var response = NfseProtocol.ParseResponse(status, body, retryAfter);
            await store.LogAttemptAsync(company.CompanyId,
                new CaptureAttempt(nsu, status, "DFe:" + response.Status, response.ErrorCode is null ? null : response.ErrorCode + " " + response.ErrorMessage, started, Now), ct);
            return response;
        }
        catch (NexFiscalException ex) when (ex.Kind.StartsWith("adn_", StringComparison.Ordinal))
        {
            await store.LogAttemptAsync(company.CompanyId, new CaptureAttempt(nsu, null, "DFe:" + ex.Kind, ex.Message, started, Now), ct);
            throw;
        }
    }

    /// <summary>Classifica o lote pelo próprio XML, grava as NFS-e na pasta e devolve o que vai ao banco.</summary>
    private NfseLot Process(IReadOnlyList<NfseDistributedDocument> documents, string directory, Counters counters)
    {
        var envelopes = new List<EnvelopeRow>();
        var rows = new List<NfseDocumentRow>();
        var cancelled = new List<string>();
        foreach (var document in documents)
        {
            if (document.Nsu is { } nsu)
                envelopes.Add(new EnvelopeRow(nsu, document.DocumentType is { Length: > 0 and <= 200 } type ? type : "DFE", document.Sha256, document.Xml));

            if (NfseProtocol.ReadNfse(document.Xml) is { } data)
            {
                // A chave informada pelo ADN, quando vem, precisa ser a mesma do XML.
                if (document.AccessKey is not null && document.AccessKey != data.AccessKey) { counters.Failures++; continue; }
                if (WriteFile(directory, data.AccessKey, document.Xml, document.Sha256)) counters.New++; else counters.Already++;
                rows.Add(new NfseDocumentRow(data.AccessKey, document.Nsu, document.Sha256, false, data.IssuerId, data.IssuerName, data.IssuedAtUtc, data.TotalValue));
            }
            else if (NfseProtocol.IsEvent(document.Xml))
            {
                counters.Ignored++;
                if (NfseProtocol.CancelledKeyOf(document.Xml) is { } key) cancelled.Add(key);
            }
            else
            {
                counters.Failures++;
                logger.LogWarning("Documento do ADN não reconhecido como NFS-e nem como evento; guardado apenas como evidência.");
            }
        }
        return new NfseLot(envelopes, rows, cancelled, null, null);
    }

    private static XmlFileState FileState(string directory, string key, string sha256)
    {
        try { return NfeXmlFolder.Check(directory, key, sha256); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw FolderFailure(directory); }
    }

    private static bool WriteFile(string directory, string key, byte[] xml, string sha256)
    {
        try { return NfeXmlFolder.Write(directory, key, xml, sha256); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw FolderFailure(directory); }
    }

    private static NexFiscalException FolderFailure(string directory) => new("folder_write_failed",
        $"Falha ao gravar na pasta {directory}. O ponto de captura não avançou: nenhum XML foi perdido e basta capturar de novo após resolver o problema de disco ou permissão.", 500);
}

/// <summary>Captura manual e monitoramento automático de NFS-e, com os mesmos contratos de tela da NF-e.</summary>
public sealed class NfseService(INfseStore store, ICaptureStore jobs, CompanyService companies, CaptureSignal signal, TimeProvider clock)
{
    private static readonly SemaphoreSlim StartLock = new(1, 1);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<CaptureView> GetCaptureAsync(string companyId, CancellationToken ct)
    {
        var (company, view) = await companies.GetAsync(companyId, ct);
        var blocked = MonitorService.Blocked(company, view);
        var cursor = await store.GetCursorAsync(companyId, ct);
        var job = await jobs.LatestNfseJobAsync(companyId, ct);
        return new CaptureView(companyId, (await store.GetMonitorAsync(companyId, ct))?.DestinationDirectory ?? job?.DestinationDirectory,
            blocked is null, blocked, cursor.NextAllowedAtUtc > Now ? cursor.NextAllowedAtUtc : null, 0, job is null ? null : CaptureJobView.From(job));
    }

    public async Task<CaptureJobView> StartAsync(string companyId, CaptureRequest? request, CancellationToken ct)
    {
        var (company, view) = await companies.GetAsync(companyId, ct);
        if (MonitorService.Blocked(company, view) is { } blocked)
            throw new NexFiscalException("certificate_unavailable", "Captura bloqueada: " + blocked, 409);
        var directory = NfeXmlFolder.Prepare(request?.DestinationDirectory);

        await StartLock.WaitAsync(ct);
        try
        {
            var latest = await jobs.LatestNfseJobAsync(companyId, ct);
            if (latest is { Active: true }) return CaptureJobView.From(latest);
            // A pasta é lembrada na configuração da NFS-e, sem ligar o monitoramento.
            var monitor = await store.GetMonitorAsync(companyId, ct);
            await store.SaveMonitorAsync(monitor is null
                ? new MonitorRow(companyId, false, MonitorService.DefaultIntervalMinutes, directory, null, null, null)
                : monitor with { DestinationDirectory = monitor.Enabled ? monitor.DestinationDirectory : directory }, Now, ct);
            var job = new CaptureJob(Guid.NewGuid().ToString(), companyId, CaptureJob.ManualNfse, CaptureJob.Queued, directory, Now);
            await jobs.InsertJobAsync(job, ct);
            signal.Notify();
            return CaptureJobView.From(job);
        }
        finally { StartLock.Release(); }
    }

    public async Task<MonitorView> GetMonitorAsync(string companyId, CancellationToken ct)
    {
        var (company, view) = await companies.GetAsync(companyId, ct);
        var blocked = MonitorService.Blocked(company, view);
        var row = await store.GetMonitorAsync(companyId, ct);
        var job = await jobs.LatestJobOfKindAsync(companyId, CaptureJob.AutomaticNfse, ct);
        DateTime? next = null;
        if (row is { Enabled: true })
        {
            var allowed = (await store.GetCursorAsync(companyId, ct)).NextAllowedAtUtc;
            next = row.NextRunAtUtc is { } planned && planned > allowed ? planned : allowed;
            if (next < Now) next = Now;
        }
        return new MonitorView(companyId, row?.Enabled ?? false, row?.IntervalMinutes ?? MonitorService.DefaultIntervalMinutes,
            row?.DestinationDirectory, blocked is null, blocked, next, row?.LastRunAtUtc, row?.LastMessage,
            job is null ? null : CaptureJobView.From(job));
    }

    public async Task<MonitorView> SaveMonitorAsync(string companyId, MonitorRequest? request, CancellationToken ct)
    {
        if (request is null) throw new NexFiscalException("invalid_input", "Informe a configuração do monitoramento.", 400);
        if (request.IntervalMinutes is < 1 or > MonitorService.MaxIntervalMinutes)
            throw new NexFiscalException("invalid_interval", "Informe o intervalo em minutos, de 1 a 10080 (uma semana).", 400);
        var (company, view) = await companies.GetAsync(companyId, ct);
        var current = await store.GetMonitorAsync(companyId, ct);

        string directory;
        if (request.Enabled)
        {
            if (MonitorService.Blocked(company, view) is { } blocked)
                throw new NexFiscalException("certificate_unavailable", "Não é possível ativar o monitoramento: " + blocked, 409);
            directory = NfeXmlFolder.Prepare(request.DestinationDirectory);
        }
        else
            directory = string.IsNullOrWhiteSpace(request.DestinationDirectory) ? current?.DestinationDirectory ?? "" : request.DestinationDirectory.Trim();

        var turningOn = request.Enabled && current is not { Enabled: true };
        await store.SaveMonitorAsync(new MonitorRow(companyId, request.Enabled, request.IntervalMinutes, directory,
            request.Enabled ? (turningOn ? Now : current!.NextRunAtUtc ?? Now) : null,
            current?.LastRunAtUtc, turningOn ? null : current?.LastMessage), Now, ct);
        signal.Notify();
        return await GetMonitorAsync(companyId, ct);
    }
}
