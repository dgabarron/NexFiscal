using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Configuração persistente do futuro serviço automático. Não dispara consultas.
/// Intervalos são inteiros positivos em minutos, sem limite superior de negócio;
/// usa Int64 por segurança/representação no processo.
/// </summary>
public sealed class CaptureScheduleSettings(IWebHostEnvironment environment)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string Root => Path.Combine(environment.ContentRootPath, ".nexfiscal-schedules");

    public sealed record Schedule(string DocumentType, bool Enabled, long IntervalMinutes, string? DestinationDirectory, bool AutomaticConfirmed = false);
    public sealed record ScheduleInput(string DocumentType, bool Enabled, string IntervalMinutes, string? DestinationDirectory, bool AutomaticConfirmed = false);

    public async Task<IReadOnlyList<Schedule>> GetAsync(string companyId, CancellationToken ct)
    {
        var path = FilePath(companyId);
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return Defaults();
            await using var stream = File.OpenRead(path);
            var saved = await JsonSerializer.DeserializeAsync<Schedule[]>(stream, cancellationToken: ct);
            if (saved is null) throw new InvalidDataException("Arquivo de agendamento inválido.");
            return saved;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// O monitoramento usa Enabled; a consulta manual só exige pasta válida.
    /// Nunca aceitar caminho alternativo enviado na requisição de captura.
    /// </summary>
    public async Task<string> RequireDestinationAsync(
        string companyId, string documentType, CancellationToken ct,
        bool requireEnabled = true)
    {
        var schedules = await GetAsync(companyId, ct);
        var schedule = schedules.SingleOrDefault(s => s.DocumentType == documentType)
            ?? throw new InvalidOperationException("Configuração de captura não encontrada para " + documentType + ".");
        if (requireEnabled && !schedule.Enabled)
            throw new InvalidOperationException("Habilite o monitoramento em " + documentType + " antes de consultar.");
        if (string.IsNullOrWhiteSpace(schedule.DestinationDirectory))
            throw new InvalidOperationException("Configure a pasta de XMLs em " + documentType + " e salve antes de consultar.");
        return NfeIndividualXmlStore.ValidateDirectory(schedule.DestinationDirectory);
    }

    public async Task<IReadOnlyList<Schedule>> SaveAsync(
        string companyId, IReadOnlyList<ScheduleInput> input, CancellationToken ct)
    {
        var path = FilePath(companyId);
        if (input is null || input.Count != 3 ||
            !new HashSet<string>(input.Select(s => s.DocumentType), StringComparer.Ordinal)
                .SetEquals(["NFE", "CTE", "NFSE"]))
            throw new ArgumentException("Informe uma configuração para NF-e, CT-e e NFS-e.");

        var validated = input.Select(s =>
        {
            // Sem teto de negócio fixo: 1, 30, 1000, 1000000 etc.
            if (!Regex.IsMatch(s.IntervalMinutes ?? "", @"^[0-9]+$") ||
                !long.TryParse(s.IntervalMinutes, out var interval) || interval < 1)
                throw new ArgumentException("O intervalo deve ser um número inteiro de minutos, a partir de 1.");
            string? dir = null;
            if (s.Enabled)
                dir = NfeIndividualXmlStore.ValidateDirectory(s.DestinationDirectory);
            else if (!string.IsNullOrWhiteSpace(s.DestinationDirectory))
                dir = s.DestinationDirectory.Trim();
            return new Schedule(s.DocumentType, s.Enabled, interval, dir,
                s.DocumentType == "CTE" && s.Enabled && s.AutomaticConfirmed);
        }).OrderBy(s => s.DocumentType, StringComparer.Ordinal).ToArray();

        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Root);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(file, validated, cancellationToken: ct);
                    await file.FlushAsync(ct);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return validated;
        }
        finally { gate.Release(); }
    }

    private string FilePath(string companyId) =>
        Guid.TryParse(companyId, out var id)
            ? Path.Combine(Root, id.ToString("D") + ".json")
            : throw new ArgumentException("Empresa inválida.");

    private static Schedule[] Defaults() =>
    [
        new("NFE", false, 60, null),
        new("CTE", false, 60, null),
        new("NFSE", false, 60, null)
    ];
}
