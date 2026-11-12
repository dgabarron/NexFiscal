using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

/// <summary>
/// Continua a distribuição NF-e oficial da SEFAZ para o CNPJ cadastrado,
/// por distNSU sequencial, usando a mesma reserva e checkpoint da captura manual.
/// Nunca busca por data, reinicia cursor ou varre NSUs arbitrários.
/// </summary>
public sealed class NfeSefazCatchup(
    NfeFirstDownload capture, CaptureScheduleSettings settings,
    IHostApplicationLifetime lifetime, ILogger<NfeSefazCatchup> logger)
{
    public sealed record Request(string CompanyId, bool PreviousConsumerStopped);
    public sealed record Progress(string Status, int Batches, long Downloaded,
        long CompleteSaved, long AlreadyPresent, long IgnoredNonProcNFe,
        string? LastNsu, string? MaxNsu, int? LastSefazStatus,
        DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, string Message);
    private static readonly ConcurrentDictionary<string, Progress> Jobs = new();

    public Progress? Get(string companyId)
    {
        var company = ValidateId(companyId);
        return Jobs.TryGetValue(company, out var state) ? state : null;
    }

    public async Task<Progress> StartAsync(Request request, CancellationToken ct)
    {
        var company = ValidateId(request.CompanyId);
        if (!request.PreviousConsumerStopped)
            throw new InvalidOperationException("Confirme que não existe outro distribuidor consultando este CNPJ.");
        // Validação de destino ANTES de iniciar tarefa.
        var destination = await settings.RequireDestinationAsync(company, "NFE", ct);
        var starting = new Progress("running", 0, 0, 0, 0, 0,
            null, null, null, DateTimeOffset.UtcNow, null,
            "Consultando documentos disponíveis na SEFAZ para o CNPJ cadastrado.");
        if (!Jobs.TryAdd(company, starting))
        {
            var previous = Jobs[company];
            if (previous.Status == "running")
                throw new InvalidOperationException("A captura completa já está em execução para esta empresa.");
            if (!Jobs.TryUpdate(company, starting, previous))
                throw new InvalidOperationException("Estado de execução alterado. Tente novamente.");
        }
        // A tarefa continua após a resposta HTTP, encerrando no desligamento do host.
        _ = Task.Run(() => RunAsync(company, destination, lifetime.ApplicationStopping));
        return starting;
    }

    private async Task RunAsync(string company, string folder, CancellationToken ct)
    {
        var state = Jobs[company];
        var batches = 0;
        long downloaded = 0, saved = 0, existing = 0, ignored = 0;
        string? last = null, max = null;
        int? sefaz = null;
        try
        {
            // O limite evita processos intermináveis: clique novamente depois
            // para continuar do cursor HANA atual, sem reset.
            const int maxBatches = 100;
            while (batches < maxBatches)
            {
                ct.ThrowIfCancellationRequested();
                // ExecuteAsync confirma certificado/CNPJ, reserva cota e consulta
                // o cursor HANA existente. Proíbe repetição durante cooldown.
                var response = await capture.ExecuteAsync(company, true, folder, ct);
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
                var x = json.RootElement;
                int ReadInt(string key) => x.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
                string? ReadString(string key) => x.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var status = ReadInt("status");
                sefaz = status;
                batches++;
                downloaded += ReadInt("downloaded");
                saved += ReadInt("filesSaved");
                existing += ReadInt("filesAlreadyPresent");
                ignored += ReadInt("skippedNonProcNFe");
                last = x.TryGetProperty("lastNsu", out var lastValue)
                    ? lastValue.ToString() : null;
                max = x.TryGetProperty("maxNsu", out var maxValue)
                    ? maxValue.ToString() : null;
                var savedSuccessfully = x.TryGetProperty("saveSucceeded", out var ok) &&
                    ok.ValueKind == JsonValueKind.True;
                var hanaSaved = x.TryGetProperty("hanaSaved", out var hana) &&
                    hana.ValueKind == JsonValueKind.True;
                if (!savedSuccessfully || !hanaSaved)
                {
                    Publish("attention", "Arquivos recebidos, mas gravação de XML ou checkpoint não foi confirmada. Verifique o log antes de continuar.");
                    return;
                }
                if (status == 656)
                {
                    Publish("paused", "SEFAZ 656: consulta bloqueada. Aguarde o intervalo obrigatório.");
                    return;
                }
                if (status == 137)
                {
                    Publish("completed", "SEFAZ informou que não há mais documentos disponíveis neste momento.");
                    return;
                }
                if (status != 138)
                {
                    Publish("attention", "SEFAZ retornou uma situação não esperada. Verifique os logs.");
                    return;
                }
                if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var lastNumber) ||
                    !long.TryParse(max, NumberStyles.None, CultureInfo.InvariantCulture, out var maxNumber) ||
                    lastNumber >= maxNumber)
                {
                    Publish("completed", "Todos os lotes atualmente disponibilizados pela SEFAZ foram consultados.");
                    return;
                }
                // Cursor HANA é atualizado somente após arquivo + lote persistidos.
                Publish("running", "Lote recebido; consultando o próximo NSU disponível.");
            }
            Publish("paused", "Limite de 100 lotes atingido. Execute novamente para continuar de onde parou.");
        }
        catch (OperationCanceledException)
        {
            Publish("interrupted", "Captura interrompida ao desligar o serviço.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Recuperação SEFAZ pausada: {ErrorType}", ex.GetType().Name);
            Publish("paused", "Consulta não autorizada no momento. Verifique o intervalo SEFAZ e o estado do checkpoint.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha na captura sequencial NF-e CNPJ protegido Company={Company}", company);
            Publish("failed", "Falha na consulta ou persistência. Verifique os logs antes de repetir.");
        }

        void Publish(string status, string message) => Jobs[company] = new Progress(status,
            batches, downloaded, saved, existing, ignored, last, max, sefaz,
            state.StartedAtUtc, status == "running" ? null : DateTimeOffset.UtcNow, message);
    }

    private static string ValidateId(string value) =>
        Guid.TryParse(value, out var id) ? id.ToString("D") :
            throw new ArgumentException("Empresa inválida.");
}
