using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using NexFiscal.Capture;

/// <summary>
/// Auditoria local de procNFe: arquivos individuais da pasta configurada e ZIPs
/// internos de lotes recebidos. Não faz chamadas à SEFAZ nem promete cobertura
/// das notas emitidas no período.
/// </summary>
public sealed class NfeHistoricalRecovery(
    IWebHostEnvironment host, CaptureScheduleSettings settings)
{
    private string ArchiveRoot => Path.Combine(host.ContentRootPath, ".nexfiscal-downloads");

    public sealed record Candidate(string Key, string? Nsu, DateTimeOffset DocumentDate,
        string Source, bool XmlPresent);
    public sealed record Preview(DateTimeOffset From, DateTimeOffset To, int Candidates,
        int IndividualFiles, int ArchiveDocuments, int WithoutDate,
        int InvalidFiles, bool ScanComplete, IReadOnlyList<Candidate> Documents,
        string Warning);

    public async Task<Preview> PreviewAsync(string companyId, DateTimeOffset from, CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out var companyGuid))
            throw new ArgumentException("Empresa inválida.");
        var now = DateTimeOffset.UtcNow;
        // A data é o início do dia de emissão, para que o período escolhido
        // não exclua notas emitidas algumas horas antes do horário atual.
        if (from > now || from < now.AddDays(-90))
            throw new ArgumentException("Selecione uma data dentro dos últimos 90 dias.");

        var folder = await settings.RequireDestinationAsync(companyGuid.ToString("D"), "NFE", ct);
        var found = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var individualFiles = 0;
        var archives = 0;
        var withoutDate = 0;
        var invalidFiles = 0;
        var scanComplete = true;

        // O diretório configurado é a fonte primária. Aceita arquivos nomeados
        // pela chave e arquivos antigos ainda nomeados pelo NSU.
        const int maxFiles = 100_000;
        foreach (var file in Directory.EnumerateFiles(folder, "*.xml", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (++individualFiles > maxFiles) { scanComplete = false; break; }
            try
            {
                var info = new FileInfo(file);
                if (info.Length is < 1 or > 10_000_000) { invalidFiles++; continue; }
                var bytes = await File.ReadAllBytesAsync(file, ct);
                var parsed = ParseCompleteDocument(bytes);
                if (parsed is null) { invalidFiles++; continue; }
                if (parsed.Value.Date is not { } date) { withoutDate++; continue; }
                if (date < from || date > now) continue;
                var item = new Candidate(parsed.Value.Key, null, date, "Pasta de XMLs", true);
                found[item.Key] = item;
            }
            catch (IOException) { invalidFiles++; }
            catch (UnauthorizedAccessException) { invalidFiles++; }
            catch (XmlException) { invalidFiles++; }
            catch (InvalidDataException) { invalidFiles++; }
        }

        var archiveDirectory = Path.Combine(ArchiveRoot, companyGuid.ToString("D"));
        if (Directory.Exists(archiveDirectory))
        {
            var zipCount = 0;
            foreach (var zipPath in Directory.EnumerateFiles(archiveDirectory, "*.zip"))
            {
                ct.ThrowIfCancellationRequested();
                if (++zipCount > 5_000) { scanComplete = false; break; }
                try
                {
                    using var zip = ZipFile.OpenRead(zipPath);
                    var manifestEntry = zip.GetEntry("manifest.json");
                    if (manifestEntry is null || manifestEntry.Length is < 1 or > 500_000)
                        { invalidFiles++; continue; }
                    using var manifestStream = manifestEntry.Open();
                    using var manifest = await JsonDocument.ParseAsync(manifestStream,
                        new JsonDocumentOptions { MaxDepth = 20 }, ct);
                    if (!manifest.RootElement.TryGetProperty("documents", out var documents) ||
                        documents.ValueKind != JsonValueKind.Array) { invalidFiles++; continue; }
                    foreach (var doc in documents.EnumerateArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (doc.ValueKind != JsonValueKind.Object ||
                            !doc.TryGetProperty("schema", out var schemaNode) ||
                            schemaNode.ValueKind != JsonValueKind.String ||
                            !(schemaNode.GetString() ?? "").StartsWith("procNFe_v", StringComparison.OrdinalIgnoreCase) ||
                            !doc.TryGetProperty("nsu", out var nsuNode) ||
                            nsuNode.ValueKind != JsonValueKind.String ||
                            !long.TryParse(nsuNode.GetString(), out var nsu) || nsu <= 0)
                            continue;
                        var entry = zip.GetEntry(nsu.ToString("D15", CultureInfo.InvariantCulture) + ".xml");
                        if (entry is null || entry.Length is < 1 or > 10_000_000)
                            { invalidFiles++; continue; }
                        await using var entryStream = entry.Open();
                        using var memory = new MemoryStream();
                        await entryStream.CopyToAsync(memory, ct);
                        if (memory.Length > 10_000_000) { invalidFiles++; continue; }
                        var bytes = memory.ToArray();
                        if (!doc.TryGetProperty("sha256", out var digest) ||
                            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),
                                digest.GetString(), StringComparison.OrdinalIgnoreCase))
                            { invalidFiles++; continue; }
                        var parsed = ParseCompleteDocument(bytes);
                        if (parsed is null) { invalidFiles++; continue; }
                        if (parsed.Value.Date is not { } date) { withoutDate++; continue; }
                        if (date < from || date > now) continue;
                        archives++;
                        if (!found.ContainsKey(parsed.Value.Key))
                        {
                            // O ZIP indica que o procNFe foi recebido, mas isso
                            // não significa que o arquivo esteja na pasta operacional.
                            var hasFile = File.Exists(Path.Combine(folder, parsed.Value.Key + ".xml")) ||
                                File.Exists(Path.Combine(folder, nsu.ToString("D15") + ".xml"));
                            found[parsed.Value.Key] = new Candidate(parsed.Value.Key,
                                nsu.ToString("D15"), date, "ZIP de lote SEFAZ", hasFile);
                        }
                    }
                }
                catch (IOException) { invalidFiles++; }
                catch (InvalidDataException) { invalidFiles++; }
                catch (JsonException) { invalidFiles++; }
                catch (XmlException) { invalidFiles++; }
            }
        }

        var list = found.Values.OrderBy(x => x.DocumentDate).ThenBy(x => x.Key).ToArray();
        var warning = "Conferência de XMLs completos existentes na pasta e nos ZIPs internos, " +
            "sem consulta à SEFAZ. Não garante a existência ou ausência de outras NF-e emitidas no período.";
        if (!scanComplete)
            warning += " O limite de segurança de arquivos foi atingido e a conferência está incompleta.";
        if (invalidFiles > 0)
            warning += " Há arquivos inválidos ou não legíveis; confira os registros antes de considerar o resultado.";

        return new Preview(from, now, list.Length, individualFiles, archives,
            withoutDate, invalidFiles, scanComplete, list, warning);
    }

    private static (string Key, DateTimeOffset? Date)? ParseCompleteDocument(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 10_000_000) return null;
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 10_000_000
        });
        var root = XDocument.Load(reader).Root;
        XNamespace ns = "http://www.portalfiscal.inf.br/nfe";
        if (root?.Name != ns + "nfeProc") return null;
        var info = root.Element(ns + "NFe")?.Element(ns + "infNFe");
        var protocol = root.Element(ns + "protNFe")?.Element(ns + "infProt");
        if (info is null || protocol is null) return null;
        var id = (string?)info.Attribute("Id");
        var key = id is { Length: 47 } && id.StartsWith("NFe", StringComparison.Ordinal)
            ? id[3..] : null;
        if (key is null || key.Length != 44 || !key.All(char.IsAsciiDigit) ||
            (string?)protocol.Element(ns + "chNFe") != key)
            return null;
        var raw = (string?)info.Element(ns + "ide")?.Element(ns + "dhEmi") ??
            (string?)info.Element(ns + "ide")?.Element(ns + "dEmi");
        return (key, DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : null);
    }
}
