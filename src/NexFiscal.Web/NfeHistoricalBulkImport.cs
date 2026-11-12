using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using NexFiscal.Capture;

/// <summary>
/// Varredura histórica de fontes de arquivo autorizadas, separada do cursor SEFAZ.
/// XML completo e validado, chave de 44 dígitos, CNPJ pertencente à empresa.
/// Nenhuma consulta retroativa fictícia à SEFAZ nem gravação no checkpoint HANA.
/// </summary>
public sealed class NfeHistoricalBulkImport(
    IWebHostEnvironment host, IHostApplicationLifetime lifetime,
    CompanyCertificateVault vault, CaptureScheduleSettings settings)
{
    public sealed record StartRequest(string CompanyId, string FromDate, string[]? SourceDirectories);
    public sealed record Progress(string Status, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc,
        long Examined, long Imported, long Existing, long Conflicts, long Invalid,
        long OtherCompany, long OutsidePeriod, long XmlFromZip, long XmlFromFolder,
        int SourcesExamined, bool ScanComplete, string Message);

    private static readonly ConcurrentDictionary<string, Progress> Jobs = new();
    private const int MaxXmlSize = 10_000_000;
    private const int MaxSourceFiles = 200_000;
    private const int MaxZipEntries = 150_000;
    private string StateRoot => Path.Combine(host.ContentRootPath, ".nexfiscal-historical");
    private string ArchiveRoot => Path.Combine(host.ContentRootPath, ".nexfiscal-downloads");

    public Progress? Get(string companyId)
    {
        var company = NormalizeCompany(companyId);
        if (Jobs.TryGetValue(company, out var state)) return state;
        var file = Path.Combine(StateRoot, company + ".json");
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<Progress>(File.ReadAllText(file)); }
        catch (JsonException) { return null; }
    }

    public async Task<Progress> StartAsync(StartRequest request, CancellationToken ct)
    {
        var company = NormalizeCompany(request.CompanyId);
        if (!DateOnly.TryParseExact(request.FromDate, "dd/MM/yyyy",
                CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var from) ||
            from.Year < 1900 || from > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("Informe uma data inicial válida em DD/MM/AAAA.");
        var destination = await settings.RequireDestinationAsync(company, "NFE", ct);
        if (!vault.IsStored(company))
            throw new InvalidOperationException("A empresa precisa de certificado A1 cadastrado para validar os XMLs.");
        var dirs = (request.SourceDirectories ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => NfeIndividualXmlStore.ValidateDirectory(s))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        if (dirs.Length > 12)
            throw new ArgumentException("Informe no máximo 12 pastas históricas.");
        // Mesmo sem fonte adicional, importa os ZIPs internos de todos os períodos
        // e reconcilia documentos já existentes na pasta operacional.
        var initial = new Progress("running", DateTimeOffset.UtcNow, null,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, true,
            "Lendo fontes históricas sem alterar o NSU atual.");
        if (!Jobs.TryAdd(company, initial))
        {
            var current = Jobs[company];
            if (current.Status == "running")
                throw new InvalidOperationException("Já existe uma recuperação histórica em andamento para esta empresa.");
            if (!Jobs.TryUpdate(company, initial, current))
                throw new InvalidOperationException("Tente novamente: o status da recuperação mudou.");
        }
        // Processamento independente da conexão HTTP, com cancelamento no desligamento do serviço.
        _ = Task.Run(() => ExecuteAsync(company, from, destination, dirs, lifetime.ApplicationStopping));
        return initial;
    }

    private async Task ExecuteAsync(string company, DateOnly from, string destination,
        string[] sources, CancellationToken ct)
    {
        var state = Jobs[company];
        long examined = 0, imported = 0, existing = 0, conflicts = 0, invalid = 0;
        long otherCompany = 0, outside = 0, zipDocs = 0, folderDocs = 0;
        var complete = true;
        var sourceCount = 0;
        string? cnpj = null;
        var known = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // Só CNPJ do certificado cadastrado: não misturar documentos de empresas distintas.
            var (pfx, pass) = await vault.ReadAsync(company, ct);
            try { cnpj = A1CompanyCertificate.Inspect(pfx, pass).Cnpj; }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            var roots = new List<string> { destination };
            roots.AddRange(sources);
            // A pasta interna de lotes é incluída sempre, sem depender de NSU fornecido.
            var internalZip = Path.Combine(ArchiveRoot, company);
            if (Directory.Exists(internalZip)) roots.Add(internalZip);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            roots = roots.Distinct(comparer).ToList();
            var visited = new HashSet<string>(comparer);
            var filesSeen = 0;
            var zipEntriesSeen = 0;

            async Task ImportAsync(byte[] bytes, bool fromZip)
            {
                examined++;
                if (fromZip) zipDocs++; else folderDocs++;
                NfeHistoricalDocumentInspector.Document? doc;
                try { doc = NfeHistoricalDocumentInspector.Inspect(bytes); }
                catch (Exception ex) when (ex is XmlException or InvalidDataException)
                    { invalid++; return; }
                if (doc is null) return; // CT-e, resNFe, eventos, outros arquivos.
                if (doc.Issuer != cnpj && doc.Recipient != cnpj) { otherCompany++; return; }
                if (doc.IssueDate is null) { invalid++; return; }
                if (doc.IssueDate.Value < from || doc.IssueDate.Value > DateOnly.FromDateTime(DateTime.UtcNow))
                    { outside++; return; }
                if (!known.Add(doc.Key)) { existing++; return; }
                var target = Path.Combine(destination, doc.Key + ".xml");
                var hash = SHA256.HashData(bytes);
                if (File.Exists(target))
                {
                    await using var stream = File.OpenRead(target);
                    var onDisk = await SHA256.HashDataAsync(stream, ct);
                    if (CryptographicOperations.FixedTimeEquals(hash, onDisk)) existing++;
                    else conflicts++;
                    return;
                }
                var tmp = Path.Combine(destination, "." + Guid.NewGuid().ToString("N") + ".xml.tmp");
                try
                {
                    await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await output.WriteAsync(bytes, ct);
                        await output.FlushAsync(ct);
                    }
                    File.Move(tmp, target, overwrite: false);
                    imported++;
                }
                catch (IOException) { conflicts++; }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
            }

            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) { invalid++; complete = false; continue; }
                sourceCount++;
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = false,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    });
                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var ext = Path.GetExtension(file);
                        if (!ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) &&
                            !ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!visited.Add(file)) continue;
                        if (++filesSeen > MaxSourceFiles) { complete = false; break; }
                        try
                        {
                            if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                            {
                                var info = new FileInfo(file);
                                if (info.Length is < 1 or > MaxXmlSize) { invalid++; continue; }
                                await ImportAsync(await File.ReadAllBytesAsync(file, ct), false);
                            }
                            else
                            {
                                using var zip = ZipFile.OpenRead(file);
                                foreach (var entry in zip.Entries)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    if (!entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    if (++zipEntriesSeen > MaxZipEntries) { complete = false; break; }
                                    if (entry.Length is < 1 or > MaxXmlSize) { invalid++; continue; }
                                    await using var input = entry.Open();
                                    using var memory = new MemoryStream();
                                    var buffer = new byte[81920];
                                    int n;
                                    while ((n = await input.ReadAsync(buffer, ct)) > 0)
                                    {
                                        if (memory.Length + n > MaxXmlSize)
                                            throw new InvalidDataException("XML descompactado excede 10 MB.");
                                        await memory.WriteAsync(buffer.AsMemory(0, n), ct);
                                    }
                                    await ImportAsync(memory.ToArray(), true);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                            or InvalidDataException or InvalidOperationException)
                        {
                            invalid++;
                        }
                        if (examined % 50 == 0)
                            Publish("running", "Lendo e conciliando fontes históricas.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { invalid++; complete = false; }
                if (!complete) break;
            }
            Publish("completed", "Recuperação das fontes acessíveis concluída. Isto não comprova a inexistência de outras NF-e.");
        }
        catch (OperationCanceledException)
        { complete = false; Publish("interrupted", "Execução interrompida ao desligar o serviço."); }
        catch (Exception)
        { complete = false; Publish("failed", "Falha na recuperação. Verifique permissões e os logs do servidor."); }

        async Task PersistAsync(Progress snapshot)
        {
            try
            {
                Directory.CreateDirectory(StateRoot);
                var file = Path.Combine(StateRoot, company + ".json");
                var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot));
                    File.Move(temp, file, overwrite: true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (IOException) { }
        }
        void Publish(string status, string message)
        {
            var snapshot = new Progress(status, state.StartedAtUtc,
                status == "running" ? null : DateTimeOffset.UtcNow,
                examined, imported, existing, conflicts, invalid, otherCompany,
                outside, zipDocs, folderDocs, sourceCount, complete, message);
            Jobs[company] = snapshot;
            if (status != "running") _ = PersistAsync(snapshot);
        }
    }

    private static string NormalizeCompany(string value) =>
        Guid.TryParse(value, out var id) ? id.ToString("D") :
            throw new ArgumentException("Empresa inválida.");
}
