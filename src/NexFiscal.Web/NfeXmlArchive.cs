using System.IO.Compression;
using System.Text.Json;
using NexFiscal.Capture;

/// <summary>
/// Entrega os XMLs do lote diretamente em um ZIP local, independente
/// da persistência dos envelopes no HANA. Nunca aceita caminhos do cliente.
/// </summary>
public sealed class NfeXmlArchive(IWebHostEnvironment host)
{
    private string Root => Path.Combine(host.ContentRootPath, ".nexfiscal-downloads");

    public async Task<(string Id, int Count, string[] Schemas)> SaveAsync(
        string companyId, NfeDistributionResult result, CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out var companyGuid))
            throw new ArgumentException("Empresa inválida.");
        if (result.Status != 138 || result.Documents.Count == 0)
            throw new InvalidOperationException("Não há XMLs para exportar.");

        var id = Guid.NewGuid().ToString("N");
        var companyDir = Path.Combine(Root, companyGuid.ToString("D"));
        Directory.CreateDirectory(companyDir);
        var destination = Path.Combine(companyDir, id + ".zip");
        var temporary = destination + ".tmp";
        var docs = result.Documents.OrderBy(x => x.Nsu).ToArray();
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var doc in docs)
                    {
                        if (doc.XmlBytes.Length == 0 || doc.XmlBytes.Length > 10_000_000)
                            throw new InvalidDataException("XML inválido para exportação.");
                        // Nenhum nome proveniente do XML é usado como caminho ZIP.
                        var entry = zip.CreateEntry(
                            doc.Nsu.ToString("D15") + ".xml", CompressionLevel.Optimal);
                        await using var output = entry.Open();
                        await output.WriteAsync(doc.XmlBytes, ct);
                    }

                    var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    await using (var stream = manifest.Open())
                    {
                        await JsonSerializer.SerializeAsync(stream, new
                        {
                            createdAtUtc = DateTime.UtcNow,
                            cStat = result.Status,
                            lastNsu = result.LastNsu.ToString("D15"),
                            maxNsu = result.MaxNsu.ToString("D15"),
                            documents = docs.Select(x => new
                            {
                                nsu = x.Nsu.ToString("D15"), schema = x.Schema,
                                sha256 = x.Sha256
                            }).ToArray()
                        }, cancellationToken: ct);
                    }
                }
                await file.FlushAsync(ct);
            }
            File.Move(temporary, destination);
            return (id, docs.Length, docs.Select(d => d.Schema).Distinct(StringComparer.Ordinal).ToArray());
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string? Find(string companyId, string archiveId)
    {
        if (!Guid.TryParse(companyId, out var companyGuid) ||
            !Guid.TryParseExact(archiveId, "N", out var archiveGuid))
            return null;
        var path = Path.Combine(Root, companyGuid.ToString("D"), archiveGuid.ToString("N") + ".zip");
        return File.Exists(path) ? path : null;
    }
}
