using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NexFiscal.Capture;
using Xunit;

namespace NexFiscal.Capture.Tests;

public sealed class CteDistributionClientTests
{
    [Fact]
    public void Request_UsesCteNamespaceSoapOperationAndOwnNsu()
    {
        var request = CteDistributionClient.BuildRequest("14492564000101", "SP", 183248, 1);
        Assert.Contains("cteDistDFeInteresse", request);
        Assert.Contains("http://www.portalfiscal.inf.br/cte", request);
        Assert.Contains("000000000183248", request);
        Assert.Contains("cUFAutor", request);
        Assert.Contains(">35</", request);
        Assert.DoesNotContain("nfeDistDFeInteresse", request);
    }

    [Fact]
    public void Request_RejectsUnknownStateBeforeNetwork()
    {
        Assert.Throws<ArgumentException>(() =>
            CteDistributionClient.BuildRequest("14492564000101", "??", 0, 1));
    }

    [Fact]
    public void Response_138_DecompressesCteProcXml()
    {
        var xml = "<cteProc xmlns=\"http://www.portalfiscal.inf.br/cte\"><CTe><infCte/></CTe><protCTe><infProt/></protCTe></cteProc>";
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true))
        {
            var bytes = Encoding.UTF8.GetBytes(xml);
            gzip.Write(bytes, 0, bytes.Length);
        }
        var data = Convert.ToBase64String(memory.ToArray());
        var soap = "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
                   "<retDistDFeInt xmlns=\"http://www.portalfiscal.inf.br/cte\" versao=\"1.00\">" +
                   "<cStat>138</cStat><xMotivo>OK</xMotivo><ultNSU>000000000000005</ultNSU>" +
                   "<maxNSU>000000000000005</maxNSU><loteDistDFeInt>" +
                   "<docZip NSU=\"000000000000005\" schema=\"procCTe_v4.00.xsd\">" + data +
                   "</docZip></loteDistDFeInt></retDistDFeInt></s:Body></s:Envelope>";
        var result = CteDistributionClient.ParseResponse(soap);
        Assert.Equal(138, result.Status);
        Assert.Single(result.Documents);
        Assert.True(CteCompleteDocument.IsProcCTe(result.Documents[0]));
        Assert.Equal(xml, Encoding.UTF8.GetString(result.Documents[0].XmlBytes));
    }

    [Theory]
    [InlineData(137)]
    [InlineData(656)]
    public void NoDocumentsOrConsumptionBlocked_ProducesNoXml(int cstat)
    {
        var soap = "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
                   "<retDistDFeInt xmlns=\"http://www.portalfiscal.inf.br/cte\" versao=\"1.00\">" +
                   "<cStat>" + cstat + "</cStat><xMotivo>Consulta indisponível</xMotivo>" +
                   "<ultNSU>000000000000000</ultNSU><maxNSU>000000000000000</maxNSU>" +
                   "</retDistDFeInt></s:Body></s:Envelope>";
        var result = CteDistributionClient.ParseResponse(soap);
        Assert.Equal(cstat, result.Status);
        Assert.Empty(result.Documents);
    }

    [Fact]
    public async Task FolderWriter_SavesOnlyProcCteWithDistinctFilename()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexfiscal-cte-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var xml = Encoding.UTF8.GetBytes("<cteProc xmlns=\"http://www.portalfiscal.inf.br/cte\"><CTe><infCte/></CTe><protCTe><infProt/></protCTe></cteProc>");
            var summary = Encoding.UTF8.GetBytes("<resCTe xmlns=\"http://www.portalfiscal.inf.br/cte\"/>");
            NfeDistributedDocument Doc(long nsu, string schema, byte[] data) =>
                new(nsu, schema, data, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
            var batch = new NfeDistributionResult(138, "OK", 2, 2, [
                Doc(1, "resCTe_v1.00.xsd", summary), Doc(2, "procCTe_v4.00.xsd", xml)
            ]);
            var stored = await new NfeIndividualXmlStore().SaveAsync(path, batch, CancellationToken.None, "CTE");
            Assert.Equal(1, stored.Created);
            Assert.Equal(1, stored.SkippedNonProcNFe);
            Assert.True(File.Exists(Path.Combine(path, "CTe_000000000000002.xml")));
            Assert.False(File.Exists(Path.Combine(path, "CTe_000000000000001.xml")));
            Assert.Equal(xml, await File.ReadAllBytesAsync(Path.Combine(path, "CTe_000000000000002.xml")));
        }
        finally { Directory.Delete(path, true); }
    }
}
