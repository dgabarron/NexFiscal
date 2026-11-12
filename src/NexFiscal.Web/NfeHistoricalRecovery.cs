using System.Text.Json;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using NexFiscal.Capture;

/// <summary>
/// Conferência local de NF-e completa (procNFe) a partir dos ZIPs
/// já recebidos, sem realizar consultas à SEFAZ.
/// </summary>
public sealed class NfeHistoricalRecovery(
    IWebHostEnvironment host, CaptureScheduleSettings settings)
{
    private string ArchiveRoot => Path.Combine(host.ContentRootPath, ".nexfiscal-downloads");

    public sealed record Candidate(string Nsu, string Schema, DateTimeOffset? DocumentDate,
        bool Complete, string Source, bool XmlPresent);
    public sealed record Preview(DateTimeOffset From, DateTimeOffset To, int Candidates,
        int WithoutDate, int AlreadyComplete, IReadOnlyList<Candidate> Documents,
        string Warning);

    public async Task<Preview> PreviewAsync(string companyId, DateTimeOffset from, CancellationToken ct)
    {
        var id = Company(companyId);
        if (from > DateTimeOffset.UtcNow || from < DateTimeOffset.UtcNow.AddDays(-90))
            throw new ArgumentException("A data deve estar dentro dos últimos 90 dias.");
        var folder = await settings.RequireDestinationAsync(id, "NFE", ct);
        var now = DateTimeOffset.UtcNow;
        var dir = Path.Combine(ArchiveRoot, id);
        var found = new Dictionary<long, Candidate>();
        var noDate = 0;
        if (Directory.Exists(dir))
        {
            foreach (var zipPath in Directory.EnumerateFiles(dir, "*.zip").OrderByDescending(File.GetCreationTimeUtc).Take(500))
            {
                ct.ThrowIfCancellationRequested();
                using var zip = ZipFile.OpenRead(zipPath);
                var manifest = zip.GetEntry("manifest.json");
                if (manifest is null || manifest.Length > 500_000) continue;
                using var manifestStream = manifest.Open();
                using var json = await JsonDocument.ParseAsync(manifestStream,
                    new JsonDocumentOptions { MaxDepth = 20 }, ct);
                if (!json.RootElement.TryGetProperty("documents", out var docs) || docs.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in docs.EnumerateArray())
                {
                    if (!entry.TryGetProperty("nsu", out var nsuValue) ||
                        !long.TryParse(nsuValue.GetString(), out var nsu) || nsu <= 0 ||
                        !entry.TryGetProperty("schema", out var schemaValue)) continue;
                    var schema = schemaValue.GetString() ?? "";
                    if (schema.Length > 200 || !schema.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase) ||
                        !schema.StartsWith("procNFe_v", StringComparison.OrdinalIgnoreCase)) continue;
                    var xmlEntry = zip.GetEntry(nsu.ToString("D15", CultureInfo.InvariantCulture) + ".xml");
                    if (xmlEntry is null || xmlEntry.Length is < 1 or > 10_000_000) continue;
                    await using var input = xmlEntry.Open();
                    using var copy = new MemoryStream();
                    await input.CopyToAsync(copy, ct);
                    var bytes = copy.ToArray();
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (!entry.TryGetProperty("sha256", out var sha) ||
                        !string.Equals(hash, sha.GetString(), StringComparison.OrdinalIgnoreCase)) continue;
                    var dt = ExtractDate(bytes);
                    if (dt is null) { noDate++; continue; }
                    if (dt < from || dt > now.AddDays(1)) continue;
                    var complete = true;
                    // Documentos completos têm nome por chave, enquanto o ZIP bruto usa NSU.
                    var present = complete && File.Exists(Path.Combine(folder,
                        FiscalAccessKey.Extract(
                            new NfeDistributedDocument(nsu, schema, bytes, hash), "NFE") + ".xml"));
                    var item = new Candidate(nsu.ToString("D15"), schema, dt, complete, "Lote SEFAZ armazenado", present);
                    // Preferir registros completos quando houver versões repetidas do mesmo NSU.
                    if (!found.TryGetValue(nsu, out var prev) || (!prev.Complete && complete))
                        found[nsu] = item;
                }
            }
        }
        var list = found.Values.OrderBy(x => x.DocumentDate).ThenBy(x => x.Nsu).ToArray();
        return new Preview(from, now, list.Length, noDate,
            list.Count(x => x.Complete || x.XmlPresent), list,
            "Apenas procNFe já recebidos e armazenados em lotes locais; " +
            "não é uma varredura de todas as notas emitidas na SEFAZ por data.");
    }

    private static string Company(string companyId) => Guid.TryParse(companyId, out var id)
        ? id.ToString("D") : throw new ArgumentException("Empresa inválida.");

    public static DateTimeOffset? ExtractDate(byte[] xml)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 10_000_000
            });
            var root = XDocument.Load(reader).Root;
            if (root is null) return null;
            // Resumos resNFe têm dhEmi; procNFe possui ide/dhEmi.
            var raw = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "dhEmi")?.Value
                ?? root.Descendants().FirstOrDefault(e => e.Name.LocalName == "dEmi")?.Value;
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date)) return date;
        }
        catch (XmlException) { }
        return null;
    }

}
