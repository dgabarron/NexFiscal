using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

/// <summary>Captura todos os lotes CT-e atualmente disponíveis, mantendo cursor fiscal CT-e independente da NF-e.</summary>
public sealed class CteSefazCatchup(
    CteFirstDownload capture, CaptureScheduleSettings settings,
    IHostApplicationLifetime lifetime, ILogger<CteSefazCatchup> logger)
{
    public sealed record Request(string CompanyId);
    public sealed record Progress(string Status, int Batches, long Downloaded,
        long CompleteSaved, long AlreadyPresent, long IgnoredNonProcCTe,
        string? LastNsu, string? MaxNsu, int? LastSefazStatus,
        DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, string Message);

    private static readonly ConcurrentDictionary<string, Progress> Jobs = new();

    public Progress? Get(string companyId)
    {
        var id = ValidateId(companyId);
        return Jobs.TryGetValue(id, out var job) ? job : null;
    }

    public async Task<Progress> StartAsync(Request request, CancellationToken ct)
    {
        var company = ValidateId(request.CompanyId);
        var folder = await settings.RequireDestinationAsync(company, "CTE", ct, requireEnabled: false);
        var state = new Progress("running", 0, 0, 0, 0, 0, null, null, null,
            DateTimeOffset.UtcNow, null, "Solicitação CT-e recebida.");
        if (!Jobs.TryAdd(company, state))
        {
            var previous = Jobs[company];
            if (previous.Status is "running" or "scheduled")
                throw new InvalidOperationException("Já existe captura CT-e em execução ou agendada.");
            if (!Jobs.TryUpdate(company, state, previous))
                throw new InvalidOperationException("Estado da captura CT-e mudou; atualize a página.");
        }

        _ = Task.Run(() => RunAsync(company, folder, lifetime.ApplicationStopping));
        return state;
    }

    private async Task RunAsync(string company, string folder, CancellationToken ct)
    {
        var state = Jobs[company];
        int batches = 0;
        long downloaded = 0, saved = 0, existing = 0, ignored = 0;
        string? last = null, max = null;
        int? sefaz = null;
        long? previousNsu = null;
        void Publish(string status, string message) =>
            Jobs[company] = new Progress(status, batches, downloaded, saved, existing, ignored,
                last, max, sefaz, state.StartedAtUtc,
                status is "running" or "scheduled" ? null : DateTimeOffset.UtcNow, message);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await AwaitFiscalWindowAsync(company, Publish, ct);
                Publish("running", "Consultando CT-e disponíveis na SEFAZ.");

                // CT-e mantém reserva fiscal e checkpoint persistidos separadamente.
                var response = await capture.ExecuteAsync(company, true, folder, ct);
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
                var root = json.RootElement;
                int ReadInt(string field) => root.TryGetProperty(field, out var value) &&
                    value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
                var status = ReadInt("status");
                sefaz = status;
                batches++;
                downloaded += ReadInt("downloaded");
                saved += ReadInt("filesSaved");
                existing += ReadInt("filesAlreadyPresent");
                ignored += ReadInt("skippedNonProcCTe");
                last = root.TryGetProperty("lastNsu", out var lastValue) ? lastValue.ToString() : null;
                max = root.TryGetProperty("maxNsu", out var maxValue) ? maxValue.ToString() : null;
                bool persisted = root.TryGetProperty("saveSucceeded", out var ok) &&
                    ok.ValueKind == JsonValueKind.True;

                if (status == 656)
                {
                    Publish("paused", "SEFAZ restringiu temporariamente a consulta CT-e (656).");
                    return;
                }
                if (!persisted)
                {
                    Publish("attention", "CT-e não foi gravado integralmente. Verifique os logs e arquivos antes de repetir.");
                    return;
                }
                if (status == 137)
                {
                    Publish("completed", "Nenhum outro CT-e disponível neste momento.");
                    return;
                }
                if (status != 138)
                {
                    Publish("attention", "Resposta CT-e inesperada. Confira os logs.");
                    return;
                }
                if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var lastNumber) ||
                    !long.TryParse(max, NumberStyles.None, CultureInfo.InvariantCulture, out var maxNumber) ||
                    lastNumber > maxNumber)
                {
                    Publish("attention", "Resposta CT-e com checkpoint inconsistente.");
                    return;
                }
                if (lastNumber == maxNumber)
                {
                    Publish("completed", "Todos os lotes CT-e atualmente disponíveis foram consultados.");
                    return;
                }
                if (previousNsu.HasValue && lastNumber <= previousNsu.Value)
                {
                    Publish("attention", "Consulta CT-e interrompida: cursor não avançou.");
                    return;
                }
                previousNsu = lastNumber;
                Publish("running", "Lote CT-e recebido; consultando os demais.");
            }
        }
        catch (OperationCanceledException)
        {
            Publish("interrupted", "Captura CT-e interrompida ao desligar o servidor; solicite novamente.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Captura CT-e interrompida: {Type}", ex.GetType().Name);
            Publish("paused", "CT-e temporariamente indisponível. Confira intervalo fiscal e configurações.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro na captura CT-e da empresa {Company}", company);
            Publish("failed", "Falha na captura CT-e. Confira os logs antes de repetir.");
        }
    }

    private async Task AwaitFiscalWindowAsync(
        string company, Action<string,string> publish, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(await capture.StatusAsync(company, ct)));
            var root = json.RootElement;
            var allowedAt = FiscalWindow.NextAllowedUtc(root);
            if (allowedAt is null || allowedAt <= DateTimeOffset.UtcNow)
                return;
            var allowed = allowedAt.Value;

            var remaining = allowed - DateTimeOffset.UtcNow;
            publish("scheduled", "Captura CT-e agendada para após " +
                allowed.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) + " (horário do servidor).");
            await Task.Delay(remaining > TimeSpan.FromSeconds(30)
                ? TimeSpan.FromSeconds(30) : remaining, ct);
        }
    }

    private static string ValidateId(string value) =>
        Guid.TryParse(value, out var guid) ? guid.ToString("D") :
            throw new ArgumentException("Empresa inválida.");
}
