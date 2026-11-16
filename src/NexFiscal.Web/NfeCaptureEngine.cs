using System.Security.Cryptography.X509Certificates;

namespace NexFiscal.Web;

/// <summary>
/// Motor de captura de NF-e (Função 3; a Função 4 reutilizará este mesmo motor).
/// Segue a NT 2014.002 v1.40: distNSU sequencial a partir do checkpoint, espera de 1 hora após
/// 137 / ultNSU = maxNSU / 656, e consNSU somente para NSUs comprovadamente faltantes, com limite por hora.
/// Só procNFe conta como XML completo. Nunca retrocede o NSU e nunca varre NSUs arbitrários.
/// </summary>
public sealed class NfeCaptureEngine(
    ICaptureStore store, CompanyService companies, ISefazTransport sefaz, TimeProvider clock, ILogger<NfeCaptureEngine> logger)
{
    /// <summary>Teto de segurança: 400 lotes de até 50 documentos por execução.</summary>
    public const int MaxLotsPerRun = 400;
    /// <summary>A SEFAZ permite 20 consultas pontuais por hora; usamos no máximo 10.</summary>
    public const int RecoveryQueriesPerHour = 10;
    public const int MaxRecoveryAttempts = 2;
    public const int MaxGapsPerLot = 200;
    public const int MaxTransientAttempts = 3;
    public static readonly TimeSpan TransientRetry = TimeSpan.FromMinutes(10);
    /// <summary>Folga sobre a hora exigida, para diferenças de relógio.</summary>
    public static readonly TimeSpan WaitMargin = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> TransientKinds = new(StringComparer.Ordinal)
    {
        "sefaz_timeout", "sefaz_unreachable", "sefaz_paused", "sefaz_http_error", "sefaz_fault",
        "unreachable", "connection_refused", "connection_timeout", "host_not_found"
    };

    private sealed class Counters
    {
        public int New, Already, Recovered, Ignored, Failures;
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Executa (ou retoma) um pedido de captura e devolve o estado final gravado.</summary>
    public async Task<CaptureJob> RunAsync(CaptureJob job, CancellationToken ct)
    {
        job = job with { State = CaptureJob.Running, StartedAtUtc = job.StartedAtUtc ?? Now, Attempts = job.Attempts + 1, NextRunAtUtc = null };
        await store.UpdateJobAsync(job, ct);
        var counters = new Counters { New = job.NewComplete, Recovered = job.Recovered, Ignored = job.Ignored, Failures = job.Failures };
        try
        {
            job = await ExecuteAsync(job, counters, ct);
        }
        catch (NexFiscalException ex)
        {
            var transient = TransientKinds.Contains(ex.Kind) && job.Attempts < MaxTransientAttempts;
            logger.LogWarning("Captura {JobId} interrompida: {Kind} (nova tentativa: {Retry})", job.JobId, ex.Kind, transient);
            job = Apply(job, counters) with
            {
                State = transient ? CaptureJob.WaitingSefaz : CaptureJob.Failed,
                NextRunAtUtc = transient ? Now + TransientRetry : null,
                FinishedAtUtc = transient ? null : Now,
                Message = transient ? ex.Message + " Nova tentativa automática em alguns minutos." : ex.Message
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Captura {JobId} falhou de forma inesperada: {Type}", job.JobId, ex.GetType().Name);
            job = Apply(job, counters) with
            {
                State = CaptureJob.Failed, FinishedAtUtc = Now,
                Message = "Falha inesperada durante a captura. O ponto de captura não avançou além do que foi gravado; tente novamente."
            };
        }
        await store.UpdateJobAsync(job, CancellationToken.None);
        return job;
    }

    private async Task<CaptureJob> ExecuteAsync(CaptureJob job, Counters counters, CancellationToken ct)
    {
        var (company, view) = await companies.GetAsync(job.CompanyId, ct);
        if (company.ArchivedAtUtc is not null)
            throw new NexFiscalException("company_archived", "Esta empresa está arquivada. Reative-a na seção Empresas para capturar.", 409);
        if (view.CertificateStatus != "stored")
            throw new NexFiscalException("certificate_unavailable", "Captura bloqueada: " + view.CertificateMessage, 409);
        var directory = NfeXmlFolder.Prepare(job.DestinationDirectory);

        // 1) Inventário local: nada disto consulta a SEFAZ.
        await IndexLegacyEnvelopesAsync(company.CompanyId, ct);
        counters.Already = 0;
        await RestoreMissingFilesAsync(company.CompanyId, directory, counters, ct);

        // 2) Respeitar o intervalo oficial antes de qualquer consulta.
        var cursor = await store.GetCursorAsync(company.CompanyId, ct);
        if (cursor.NextAllowedAtUtc > Now)
            return Apply(job, counters) with
            {
                State = CaptureJob.WaitingSefaz, NextRunAtUtc = cursor.NextAllowedAtUtc,
                Pending = await CountPendingAsync(company.CompanyId, ct),
                Message = "Arquivos locais conferidos. A SEFAZ exige intervalo de 1 hora entre consultas; a captura continua sozinha no horário indicado, sem novo clique."
            };

        // 3) Distribuição sequencial e 4) recuperação pontual de lacunas conhecidas.
        string? note;
        bool blocked;
        using (var certificate = companies.OpenCertificate(company.CompanyId))
        {
            (blocked, note) = await DistributeAsync(company, certificate, directory, cursor.LastNsu, counters, ct);
            if (!blocked) await RecoverKnownGapsAsync(company, certificate, directory, counters, ct);
        }
        // Pausa imposta pela SEFAZ (656): o pedido fica gravado e é retomado sozinho depois de 1 hora, sem repetir a consulta antes.
        if (blocked && job.Attempts < MaxTransientAttempts)
            return Apply(job, counters) with
            {
                State = CaptureJob.WaitingSefaz, LastStatus = NfeProtocol.StatusImproperUse,
                NextRunAtUtc = (await store.GetCursorAsync(company.CompanyId, ct)).NextAllowedAtUtc,
                Pending = await CountPendingAsync(company.CompanyId, ct),
                Message = note + " A captura continua sozinha no horário indicado."
            };

        var pending = await CountPendingAsync(company.CompanyId, ct);
        var message = "Todos os documentos disponibilizados pela SEFAZ nesta janela foram processados.";
        if (note is not null) message = note;
        if (pending > 0)
            message += $" {pending} nota(s) ainda sem XML completo: a SEFAZ só libera o XML ao destinatário depois da manifestação. Registre a Ciência da Operação na lista de notas pendentes da tela Captura.";
        if (counters.Failures > 0) message += $" {counters.Failures} item(ns) com falha; veja os detalhes e tente novamente depois.";
        return Apply(job, counters) with
        {
            State = pending > 0 || counters.Failures > 0 ? CaptureJob.CompletedWithPending : CaptureJob.Completed,
            Pending = pending, FinishedAtUtc = Now, Message = message
        };
    }

    private static CaptureJob Apply(CaptureJob job, Counters c) => job with
    {
        NewComplete = c.New, AlreadyPresent = c.Already, Recovered = c.Recovered, Ignored = c.Ignored, Failures = c.Failures
    };

    private async Task<int> CountPendingAsync(string companyId, CancellationToken ct)
    {
        var summaries = (await store.ListDocumentsAsync(companyId, ct)).Count(IsPendingSummary);
        return summaries + await store.CountOpenRecoveryAsync(companyId, MaxRecoveryAttempts, ct);
    }

    /// <summary>NF-e autorizada conhecida apenas pelo resumo (canceladas e denegadas não têm XML a baixar).</summary>
    public static bool IsPendingSummary(DocumentRow document) => !document.HasCompleteXml && document.Situation is null or 1;

    // ---------- inventário ----------

    /// <summary>procNFe que já estão no HANA (inclusive de versões antigas) passam a constar no inventário por chave.</summary>
    private async Task IndexLegacyEnvelopesAsync(string companyId, CancellationToken ct)
    {
        long after = -1;
        while (true)
        {
            var batch = await store.ListUnindexedCompleteEnvelopesAsync(companyId, after, ct);
            if (batch.Count == 0) return;
            after = batch.Max(e => e.Nsu);
            var documents = new List<DocumentRow>();
            foreach (var envelope in batch)
                if (NfeProtocol.ReadDocument(NfeDocumentKind.Complete, envelope.Xml) is { } data)
                    documents.Add(CompleteRow(data, envelope.Nsu, envelope.Sha256));
            if (documents.Count > 0)
                await store.SaveLotAsync(companyId, new LotPersistence([], documents, [], [], null, null), Now, ct);
        }
    }

    /// <summary>XML completo conhecido cujo arquivo falta ou está corrompido é regravado a partir da cópia íntegra no HANA.</summary>
    private async Task RestoreMissingFilesAsync(string companyId, string directory, Counters counters, CancellationToken ct)
    {
        foreach (var document in await store.ListDocumentsAsync(companyId, ct))
        {
            if (!document.HasCompleteXml || document.CompleteSha256 is null) continue;
            if (FileState(directory, document.AccessKey, document.CompleteSha256) == XmlFileState.Valid) { counters.Already++; continue; }
            var xml = document.CompleteNsu is { } nsu ? await store.ReadEnvelopeAsync(companyId, nsu, ct) : null;
            if (xml is null || NfeProtocol.ReadDocument(NfeDocumentKind.Complete, xml)?.AccessKey != document.AccessKey)
            {
                counters.Failures++;
                logger.LogWarning("XML completo {Key} ausente na pasta e sem cópia íntegra no HANA.", document.AccessKey);
                continue;
            }
            WriteFile(directory, document.AccessKey, xml, document.CompleteSha256);
            counters.Recovered++;
        }
    }

    // ---------- SEFAZ ----------

    private async Task<(bool Blocked, string? Note)> DistributeAsync(
        CompanyRecord company, X509Certificate2 certificate, string directory, long lastNsu, Counters counters, CancellationToken ct)
    {
        long? previousNsu = null;
        for (var lot = 0; lot < MaxLotsPerRun; lot++)
        {
            var response = await QueryAsync(company, certificate, lastNsu, "distNSU",
                NfeProtocol.BuildDistributionRequest(company.Cnpj, company.Uf, lastNsu), ct);
            var wait = Now + NfeProtocol.MandatoryWait + WaitMargin;

            switch (response.Status)
            {
                case NfeProtocol.StatusDocumentsFound:
                    var newLast = response.LastNsu ?? response.Documents.Max(d => d.Nsu) ?? lastNsu;
                    var finished = newLast <= lastNsu || response.MaxNsu is null || newLast >= response.MaxNsu;
                    var persistence = ProcessDocuments(response.Documents, directory, counters, ref previousNsu, recovering: false);
                    await store.SaveLotAsync(company.CompanyId,
                        persistence with { LastNsu = newLast, NextAllowedAtUtc = finished ? wait : null }, Now, ct);
                    lastNsu = Math.Max(lastNsu, newLast);
                    if (finished) return (false, null);
                    continue;

                case NfeProtocol.StatusNoDocuments:
                    await store.SaveLotAsync(company.CompanyId, new LotPersistence([], [], [], [], response.LastNsu, wait), Now, ct);
                    return (false, lastNsu == 0 && lot == 0
                        ? "A SEFAZ ainda não tem documentos numerados para este CNPJ. Para quem não usava a distribuição nos últimos 60 dias, a numeração começa neste primeiro acesso e não é retroativa; novos documentos aparecem nas próximas capturas."
                        : null);

                case NfeProtocol.StatusImproperUse:
                    // Bloqueio de 1 hora. Não repetir, não mexer no NSU.
                    await store.SaveLotAsync(company.CompanyId, new LotPersistence([], [], [], [], null, wait), Now, ct);
                    return (true, $"A SEFAZ pausou as consultas deste CNPJ por 1 hora (consumo indevido): {response.Reason} " +
                                  "Se outro sistema também consulta a distribuição de NF-e deste CNPJ, os dois precisam seguir a mesma sequência.");

                case 108 or 109:
                    throw new NexFiscalException("sefaz_paused", "O serviço da SEFAZ está paralisado no momento.", 503);

                default:
                    throw new NexFiscalException("sefaz_rejected",
                        $"A SEFAZ rejeitou a consulta (código {response.Status}): {response.Reason} Nada foi alterado e o ponto de captura foi mantido.", 502);
            }
        }
        return (false, "A captura atingiu o limite de lotes desta execução. Clique novamente para continuar de onde parou.");
    }

    private async Task RecoverKnownGapsAsync(
        CompanyRecord company, X509Certificate2 certificate, string directory, Counters counters, CancellationToken ct)
    {
        var used = await store.CountRecoveryAttemptsSinceAsync(company.CompanyId, Now - NfeProtocol.MandatoryWait, ct);
        var budget = RecoveryQueriesPerHour - used;
        if (budget <= 0) return;
        foreach (var nsu in await store.ListPendingRecoveryAsync(company.CompanyId, MaxRecoveryAttempts, budget, ct))
        {
            var response = await QueryAsync(company, certificate, nsu, "consNSU",
                NfeProtocol.BuildNsuQueryRequest(company.Cnpj, company.Uf, nsu), ct);
            if (response.Status == NfeProtocol.StatusDocumentsFound)
            {
                long? none = null;
                var persistence = ProcessDocuments(response.Documents, directory, counters, ref none, recovering: true);
                await store.SaveLotAsync(company.CompanyId, persistence, Now, ct);
                await store.SaveRecoveryResultAsync(company.CompanyId, nsu, "recovered", response.Status, Now, ct);
            }
            else if (response.Status == NfeProtocol.StatusNoDocuments)
                // NT 3.6: o Ambiente Nacional não gerou esse NSU; desconsiderar.
                await store.SaveRecoveryResultAsync(company.CompanyId, nsu, "absent", response.Status, Now, ct);
            else
            {
                await store.SaveRecoveryResultAsync(company.CompanyId, nsu, "pending", response.Status, Now, ct);
                if (response.Status == NfeProtocol.StatusImproperUse)
                {
                    await store.SaveLotAsync(company.CompanyId,
                        new LotPersistence([], [], [], [], null, Now + NfeProtocol.MandatoryWait + WaitMargin), Now, ct);
                    return;
                }
                counters.Failures++;
            }
        }
    }

    private async Task<NfeDistributionResponse> QueryAsync(
        CompanyRecord company, X509Certificate2 certificate, long nsu, string operation, string envelope, CancellationToken ct)
    {
        var started = Now;
        try
        {
            var response = NfeProtocol.ParseResponse(await sefaz.SendAsync(NfeProtocol.ProductionUrl, envelope, certificate, ct));
            await store.LogAttemptAsync(company.CompanyId,
                new CaptureAttempt(nsu, response.Status, operation, response.Reason, started, Now), ct);
            return response;
        }
        catch (NexFiscalException ex) when (ex.Kind.StartsWith("sefaz_", StringComparison.Ordinal))
        {
            await store.LogAttemptAsync(company.CompanyId, new CaptureAttempt(nsu, null, operation + ":" + ex.Kind, ex.Message, started, Now), ct);
            throw;
        }
    }

    /// <summary>Classifica um lote, grava os procNFe na pasta e devolve o que deve ir ao banco. Os arquivos vêm antes do checkpoint.</summary>
    private LotPersistence ProcessDocuments(
        IReadOnlyList<NfeDistributedDocument> documents, string directory, Counters counters, ref long? previousNsu, bool recovering)
    {
        var envelopes = new List<EnvelopeRow>();
        var rows = new List<DocumentRow>();
        var cancelled = new List<string>();
        var missing = new List<long>();
        foreach (var document in documents)
        {
            if (document.Nsu is { } nsu)
            {
                envelopes.Add(new EnvelopeRow(nsu, document.Schema, document.Sha256, document.Xml));
                // Lacuna dentro de uma sequência recebida nesta execução: único caso em que um NSU entra na fila de recuperação.
                if (!recovering && previousNsu is { } previous && nsu > previous + 1 && nsu - previous - 1 <= MaxGapsPerLot)
                    for (var gap = previous + 1; gap < nsu; gap++) missing.Add(gap);
                if (!recovering) previousNsu = nsu;
            }

            switch (document.Kind)
            {
                case NfeDocumentKind.Complete:
                    if (NfeProtocol.ReadDocument(NfeDocumentKind.Complete, document.Xml) is not { } complete)
                    {
                        counters.Failures++;
                        logger.LogWarning("procNFe recebido com estrutura inválida; guardado apenas como evidência.");
                        break;
                    }
                    var written = WriteFile(directory, complete.AccessKey, document.Xml, document.Sha256);
                    if (!written) counters.Already++;
                    else if (recovering) counters.Recovered++;
                    else counters.New++;
                    rows.Add(CompleteRow(complete, document.Nsu, document.Sha256));
                    break;

                case NfeDocumentKind.Summary:
                    counters.Ignored++;
                    if (NfeProtocol.ReadDocument(NfeDocumentKind.Summary, document.Xml) is { } summary)
                        rows.Add(new DocumentRow(summary.AccessKey, false, null, null, document.Nsu, summary.Situation,
                            summary.IssuerId, summary.IssuerName, summary.IssuedAtUtc, summary.TotalValue));
                    break;

                case NfeDocumentKind.Event:
                    counters.Ignored++;
                    if (NfeProtocol.CancelledKeyOf(document.Xml) is { } key) cancelled.Add(key);
                    break;

                default:
                    counters.Ignored++;
                    break;
            }
        }
        return new LotPersistence(envelopes, rows, cancelled, missing, null, null);
    }

    private static DocumentRow CompleteRow(NfeDocumentData data, long? nsu, string sha256) =>
        new(data.AccessKey, true, nsu, sha256, null, 1, data.IssuerId, data.IssuerName, data.IssuedAtUtc, data.TotalValue);

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
