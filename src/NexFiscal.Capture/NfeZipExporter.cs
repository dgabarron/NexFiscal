using System.IO.Compression;
using System.Text.Json;

namespace NexFiscal.Capture;

/// <summary>Gera um ZIP de XMLs distribuídos, sem SAP HANA, sem acesso à rede.</summary>
public static class NfeZipExporter
{
    public static async Task WriteAsync(Stream destination,
        NfeDistributionResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status != 138 || result.Documents.Count is < 1 or > 50)
            throw new InvalidDataException("Somente lotes 138 com 1 a 50 XMLs podem ser exportados.");
        if (result.Documents.Select(d => d.Nsu).Distinct().Count() != result.Documents.Count)
            throw new InvalidDataException("NSU repetido no lote fiscal.");

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var doc in result.Documents.OrderBy(x => x.Nsu))
        {
            if (doc.XmlBytes.Length is < 1 or > 10_000_000)
                throw new InvalidDataException("XML inválido para exportação.");
            var entry = zip.CreateEntry(doc.Nsu.ToString("D15") + ".xml", CompressionLevel.Optimal);
            await using var output = entry.Open();
            await output.WriteAsync(doc.XmlBytes, ct);
        }
        var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var manifestOutput = manifest.Open();
        await JsonSerializer.SerializeAsync(manifestOutput, new
        {
            createdAtUtc = DateTime.UtcNow,
            cStat = result.Status,
            lastNsu = result.LastNsu.ToString("D15"),
            maxNsu = result.MaxNsu.ToString("D15"),
            documents = result.Documents.OrderBy(d => d.Nsu).Select(d => new
            {
                nsu = d.Nsu.ToString("D15"),
                schema = d.Schema,
                sha256 = d.Sha256
            }).ToArray()
        }, cancellationToken: ct);
    }
}
