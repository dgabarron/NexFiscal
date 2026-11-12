using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NexFiscal.Capture;
using Xunit;

namespace NexFiscal.Capture.Tests;

public sealed class NfeZipExporterTests
{
    [Fact]
    public async Task RealXmlBytes_AreDownloadableFromZip_WithoutHana()
    {
        var xml = Encoding.UTF8.GetBytes("<resNFe xmlns=\"http://www.portalfiscal.inf.br/nfe\"/>");
        var hash = Convert.ToHexString(SHA256.HashData(xml)).ToLowerInvariant();
        var response = new NfeDistributionResult(138, "Documentos localizados", 123, 123,
            [new NfeDistributedDocument(123, "resNFe_v1.01.xsd", xml, hash)]);
        using var output = new MemoryStream();
        await NfeZipExporter.WriteAsync(output, response);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(2, zip.Entries.Count);
        var entry = zip.GetEntry("000000000000123.xml");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal(Encoding.UTF8.GetString(xml), await reader.ReadToEndAsync());
        var manifest = zip.GetEntry("manifest.json");
        Assert.NotNull(manifest);
        using var manifestReader = new StreamReader(manifest.Open());
        var info = await manifestReader.ReadToEndAsync();
        Assert.Contains(hash, info);
        Assert.Contains("resNFe_v1.01.xsd", info);
    }

    [Fact]
    public async Task Status137_DoesNotGenerateEmptyZip()
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NfeZipExporter.WriteAsync(output, new NfeDistributionResult(
                137, "Sem documentos", 0, 0, [])));
    }

    [Fact]
    public async Task DuplicatedNsu_RefusesZip()
    {
        var doc = new NfeDistributedDocument(5, "resNFe_v1.01.xsd", [60, 47, 62], "hash");
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NfeZipExporter.WriteAsync(output, new NfeDistributionResult(
                138, "Documentos", 5, 5, [doc, doc])));
    }
}
