using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>Monitora CT-e no intervalo configurado e respeita o checkpoint e a janela fiscal próprios.</summary>
public sealed class CteMonitoringWorker(
    NfeCaptureCoordinator coordinator, CaptureScheduleSettings settings,
    CteFirstDownload capture, CteSefazCatchup manual,
    ILogger<CteMonitoringWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextAttempt = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Monitoramento CT-e: ciclo adiado."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(await coordinator.StatusAsync(ct)));
        foreach (var company in json.RootElement.GetProperty("companies").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var id = company.GetProperty("companyId").GetString()!;
            try
            {
                var schedule = (await settings.GetAsync(id, ct))
                    .SingleOrDefault(s => s.DocumentType == "CTE");
                if (schedule is not { Enabled: true } ||
                    string.IsNullOrWhiteSpace(schedule.DestinationDirectory) ||
                    !company.GetProperty("certificateStored").GetBoolean())
                    continue;
                if (_nextAttempt.TryGetValue(id, out var next) && next > DateTimeOffset.UtcNow)
                    continue;
                if (manual.Get(id)?.Status is "running" or "scheduled") continue;

                using var cursor = JsonDocument.Parse(JsonSerializer.Serialize(await capture.StatusAsync(id, ct)));
                var root = cursor.RootElement;
                if (root.TryGetProperty("nextAllowedUtc", out var allowed) &&
                    allowed.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(allowed.GetString(), out var utc) &&
                    DateTime.SpecifyKind(utc, DateTimeKind.Utc) > DateTime.UtcNow)
                    continue;

                var interval = TimeSpan.FromMinutes(Math.Max(1, Math.Min(schedule.IntervalMinutes, 525600)));
                _nextAttempt[id] = DateTimeOffset.UtcNow.Add(interval);
                try
                {
                    var response = await capture.ExecuteAsync(id, true, schedule.DestinationDirectory, ct);
                    using var result = JsonDocument.Parse(JsonSerializer.Serialize(response));
                    if (!result.RootElement.TryGetProperty("saveSucceeded", out var ok) ||
                        ok.ValueKind != JsonValueKind.True)
                    {
                        logger.LogWarning("Monitoramento CT-e: resposta não persistida para {Company}", id);
                        _nextAttempt[id] = DateTimeOffset.UtcNow.AddHours(1);
                    }
                }
                catch (InvalidOperationException)
                {
                    _nextAttempt[id] = DateTimeOffset.UtcNow.AddMinutes(1);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no monitoramento CT-e para {Company}", id);
                _nextAttempt[id] = DateTimeOffset.UtcNow.AddMinutes(5);
            }
        }
    }
}
