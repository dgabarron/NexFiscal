using System.IO.Compression;
using System.Text;
using NexFiscal.Capture;
using Xunit;

namespace NexFiscal.Capture.Tests;

public sealed class NfeDistributionClientTests
{
    [Fact]
    public void Request_ContainsCorrectSoapAndFormattedNsu()
    {
        var xml = NfeDistributionClient.BuildRequest("11222333000181", 183248, 1);
        Assert.Contains("http://www.w3.org/2003/05/soap-envelope", xml);
        Assert.Contains("nfeDistDFeInteresse", xml);
        Assert.Contains("000000000183248", xml);
        Assert.Contains("11222333000181", xml);
    }

    [Fact]
    public void Response_137_ReturnsNoDocuments()
    {
        var response = Wrap("<cStat>137</cStat><xMotivo>Nenhum documento localizado</xMotivo><ultNSU>000000000183248</ultNSU><maxNSU>000000000183248</maxNSU>");
        var result = NfeDistributionClient.ParseResponse(response);
        Assert.True(result.NoDocuments);
        Assert.Empty(result.Documents);
        Assert.Equal(183248, result.LastNsu);
    }

    [Fact]
    public void Response_656_IsBlockedAndDoesNotCreateDocuments()
    {
        var result = NfeDistributionClient.ParseResponse(Wrap("<cStat>656</cStat><xMotivo>Consumo indevido</xMotivo>"));
        Assert.True(result.ConsumptionBlocked);
        Assert.Empty(result.Documents);
    }

    [Fact]
    public void Response_138_DecompressesAndHashesXml()
    {
        var xml = "<resNFe xmlns=\"http://www.portalfiscal.inf.br/nfe\"><chNFe>123</chNFe></resNFe>";
        var data = Encoding.UTF8.GetBytes(xml);
        using var zipped = new MemoryStream();
        using (var gzip = new GZipStream(zipped, CompressionLevel.Optimal, true))
            gzip.Write(data);
        var base64 = Convert.ToBase64String(zipped.ToArray());
        var result = NfeDistributionClient.ParseResponse(Wrap(
            "<cStat>138</cStat><ultNSU>000000000000002</ultNSU><maxNSU>000000000000002</maxNSU>" +
            "<loteDistDFeInt><docZip NSU=\"000000000000002\" schema=\"resNFe_v1.01.xsd\">" +
            base64 + "</docZip></loteDistDFeInt>"));
        Assert.True(result.DocumentsFound);
        Assert.Single(result.Documents);
        Assert.Equal(xml, Encoding.UTF8.GetString(result.Documents[0].XmlBytes));
        Assert.Equal(64, result.Documents[0].Sha256.Length);
    }

    [Fact]
    public void Response_WithoutRetDistDFeInt_Throws()
    {
        Assert.Throws<InvalidDataException>(() => NfeDistributionClient.ParseResponse("<Envelope/>"));
    }

    private static string Wrap(string inner) =>
        "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\">" +
        "<soap:Body><nfeDistDFeInteresseResponse xmlns=\"http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe\">" +
        "<nfeDistDFeInteresseResult><retDistDFeInt xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"1.01\">" +
        inner + "</retDistDFeInt></nfeDistDFeInteresseResult></nfeDistDFeInteresseResponse></soap:Body></soap:Envelope>";
}
