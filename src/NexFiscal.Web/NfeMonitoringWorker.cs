using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// Serviço permanente de monitoramento NF-e: permanece ativo, mas só consulta
/// no intervalo configurado e após a liberação do cursor SEFAZ no SAP HANA.
/// Não executa recuperação retroativa e nunca reinicia o NSU.
/// </summary>
public sealed class NfeMonitoringWorker(
    NfeCaptureCoordinator coordinator,
    CaptureScheduleSettings schedules,
    NfeFirstDownload capture,
    NfeSefazCatchup onDemand,
    ILogger<NfeMonitoringWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextAttempt = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Monitoramento NF-e: ciclo adiado."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(await coordinator.StatusAsync(ct)));
        foreach (var company in payload.RootElement.GetProperty("companies").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var id = company.GetProperty("companyId").GetString()!;
            try
            {
                var schedule = (await schedules.GetAsync(id, ct))
                    .SingleOrDefault(s => s.DocumentType == "NFE");
                if (schedule is not { Enabled: true } ||
                    string.IsNullOrWhiteSpace(schedule.DestinationDirectory))
                    continue;
                if (!company.GetProperty("certificateStored").GetBoolean())
                    continue;
                if (_nextAttempt.TryGetValue(id, out var next) && next > DateTimeOffset.UtcNow)
                    continue;
                if (onDemand.Get(id)?.Status is "running" or "scheduled") continue;

                // O próximo horário do HANA prevalece sobre qualquer intervalo
                // menor configurado no frontend.
                if (company.TryGetProperty("nextAllowedUtc", out var allowed) &&
                    allowed.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(allowed.GetString(), out var utc) &&
                    DateTime.SpecifyKind(utc, DateTimeKind.Utc) > DateTime.UtcNow)
                    continue;

                var interval = TimeSpan.FromMinutes(Math.Max(1, Math.Min(schedule.IntervalMinutes, 525600)));
                _nextAttempt[id] = DateTimeOffset.UtcNow.Add(interval);
                try
                {
                    var result = await capture.ExecuteAsync(id, schedule.DestinationDirectory, ct);
                    using var response = JsonDocument.Parse(JsonSerializer.Serialize(result));
                    var root = response.RootElement;
                    var ok = root.GetProperty("saveSucceeded").GetBoolean() &&
                        root.GetProperty("hanaSaved").GetBoolean();
                    if (!ok)
                    {
                        logger.LogError("Monitoramento NF-e: persistência incompleta, empresa {CompanyId}.", id);
                        _nextAttempt[id] = DateTimeOffset.UtcNow.AddHours(1);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogInformation("Monitoramento NF-e aguardando autorização fiscal para empresa {CompanyId}: {Reason}", id, ex.GetType().Name);
                    _nextAttempt[id] = DateTimeOffset.UtcNow.AddMinutes(1);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no monitoramento NF-e para empresa {CompanyId}", id);
                _nextAttempt[id] = DateTimeOffset.UtcNow.AddMinutes(5);
            }
        }
    }
}
