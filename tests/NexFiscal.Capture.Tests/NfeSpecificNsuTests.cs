using System.Xml.Linq;
using NexFiscal.Capture;
using Xunit;

namespace NexFiscal.Capture.Tests;

public sealed class NfeSpecificNsuTests
{
    [Fact]
    public void SpecificRequest_HasConsNsuButNeverDistNsu()
    {
        var xml = NfeDistributionClient.BuildSpecificNsuRequest("14492564000101", 183248, 1, 35);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://www.portalfiscal.inf.br/nfe";
        var payload = doc.Descendants(ns + "distDFeInt").Single();
        Assert.Equal("000000000183248", payload.Descendants(ns + "NSU").Single().Value);
        Assert.Empty(payload.Descendants(ns + "distNSU"));
        Assert.Equal("35", payload.Element(ns + "cUFAutor")?.Value);
    }

    [Fact]
    public void SpecificRequest_RejectsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NfeDistributionClient.BuildSpecificNsuRequest("14492564000101", 0, 1));
    }

    [Fact]
    public void SpecificResponse_RejectsNsuDifferentFromRequested()
    {
        var xml = "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\">" +
                  "<soap:Body><retDistDFeInt xmlns=\"http://www.portalfiscal.inf.br/nfe\">" +
                  "<cStat>137</cStat><xMotivo>Nenhum documento</xMotivo>" +
                  "<ultNSU>0</ultNSU><maxNSU>0</maxNSU>" +
                  "</retDistDFeInt></soap:Body></soap:Envelope>";
        var parsed = NfeDistributionClient.ParseResponse(xml, 12);
        Assert.Equal(137, parsed.Status);
        Assert.Empty(parsed.Documents);
    }
}
