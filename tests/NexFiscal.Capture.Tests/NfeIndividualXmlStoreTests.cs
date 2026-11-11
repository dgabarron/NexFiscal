using System.Security.Cryptography;
using System.Text;
using NexFiscal.Capture;
using Xunit;

namespace NexFiscal.Capture.Tests;

public sealed class NfeIndividualXmlStoreTests
{
    [Fact]
    public async Task Status138_WritesEachXmlAsSeparateFile_AndDoesNotOverwrite()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nexfiscal-xml-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = Encoding.UTF8.GetBytes("<resNFe>first</resNFe>");
            var second = Encoding.UTF8.GetBytes("<procNFe>second</procNFe>");
            NfeDistributedDocument Doc(long nsu, byte[] bytes) => new(
                nsu, "resNFe_v1.01.xsd", bytes,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            var response = new NfeDistributionResult(138, "OK", 2, 2,
                [Doc(1, first), Doc(2, second)]);
            var store = new NfeIndividualXmlStore();
            var saved = await store.SaveAsync(folder, response, CancellationToken.None);
            Assert.Equal(2, saved.Created);
            Assert.Equal(0, saved.AlreadyPresent);
            Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(folder, "000000000000001.xml")));
            Assert.Equal(second, await File.ReadAllBytesAsync(Path.Combine(folder, "000000000000002.xml")));

            var repeated = await store.SaveAsync(folder, response, CancellationToken.None);
            Assert.Equal(0, repeated.Created);
            Assert.Equal(2, repeated.AlreadyPresent);

            await File.WriteAllTextAsync(Path.Combine(folder, "000000000000001.xml"), "alterado");
            await Assert.ThrowsAsync<IOException>(() =>
                store.SaveAsync(folder, response, CancellationToken.None));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void InvalidOrMissingDestination_StopsBeforeSeFaz()
    {
        Assert.Throws<ArgumentException>(() =>
            NfeIndividualXmlStore.ValidateDirectory(null));
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() =>
            NfeIndividualXmlStore.ValidateDirectory(missing));
    }

    [Fact]
    public async Task Status137_DoesNotWriteAnyXml()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nexfiscal-xml-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var result = await new NfeIndividualXmlStore().SaveAsync(
                folder, new NfeDistributionResult(137, "Sem XML", 0, 0, []), CancellationToken.None);
            Assert.Equal(0, result.Created);
            Assert.Empty(Directory.EnumerateFiles(folder));
        }
        finally { Directory.Delete(folder); }
    }
}
