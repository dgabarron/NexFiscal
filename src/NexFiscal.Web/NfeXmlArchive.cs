using NexFiscal.Capture;

/// <summary>
/// Entrega os XMLs do lote diretamente em um ZIP local, independente
/// da persistência dos envelopes no HANA. Nunca aceita caminhos do cliente.
/// </summary>
public sealed class NfeXmlArchive(NexFiscalDataDirectory dataDirectory)
{
    private string Root => dataDirectory.PathFor(".nexfiscal-downloads");

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
                await NfeZipExporter.WriteAsync(file, result, ct);
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
