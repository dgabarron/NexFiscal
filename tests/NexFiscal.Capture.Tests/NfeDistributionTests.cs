using System.IO.Compression;
using System.Text;
using System.Xml;
using NexFiscal.Capture;
using Xunit;

namespace NexFiscal.Capture.Tests;

public class NfeDistributionTests
{
    private static string Batch(int status, long last, long max, string documents = "") =>
        $"<retDistDFeInt><cStat>{status}</cStat><xMotivo>Teste</xMotivo><ultNSU>{last:D15}</ultNSU><maxNSU>{max:D15}</maxNSU><loteDistDFeInt>{documents}</loteDistDFeInt></retDistDFeInt>";

    private static string Document(long nsu, string xml = "<resNFe><chNFe>123</chNFe></resNFe>")
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(xml);
            gzip.Write(bytes);
        }
        return $"<docZip NSU=\"{nsu:D15}\" schema=\"resNFe_v1.01.xsd\">{Convert.ToBase64String(memory.ToArray())}</docZip>";
    }

    [Fact] public void Parses_Compressed_Xml() 
    {
        var result = NfeDistribution.Parse(Batch(138, 11, 20, Document(11)));
        Assert.Equal(11, result.LastNsu);
        Assert.Single(result.Documents);
        Assert.Contains("resNFe", result.Documents[0].Xml);
        Assert.Equal(64, result.Documents[0].Sha256.Length);
    }

    [Fact] public void Rejected_Response_Does_Not_Return_Documents()
    {
        Assert.Empty(NfeDistribution.Parse(Batch(656, 4, 7)).Documents);
    }

    [Fact] public void Refuses_Nsu_Rollback()
    {
        var previous = new NfeCheckpoint("12345678000190", 1, 9, DateTimeOffset.MinValue);
        Assert.Throws<InvalidDataException>(() => NfeDistribution.Advance(previous, NfeDistribution.Parse(Batch(138, 8, 20)), DateTimeOffset.UtcNow));
    }

    [Fact] public void Refuses_Duplicated_Nsu()
    {
        Assert.Throws<InvalidDataException>(() => NfeDistribution.Parse(Batch(138, 11, 20, Document(11) + Document(11))));
    }

    [Fact] public void Blocks_Too_Early_Queries()
    {
        var now = DateTimeOffset.Parse("2026-11-11T12:00:00Z");
        Assert.Equal(now.AddHours(1), NfeDistribution.NextAllowed(now, NfeDistribution.Parse(Batch(137, 20, 20))));
        Assert.Equal(now.AddMinutes(3), NfeDistribution.NextAllowed(now, NfeDistribution.Parse(Batch(138, 20, 21))));
    }

    [Fact] public void Rejects_Xml_With_Doctype()
    {
        Assert.Throws<XmlException>(() => NfeDistribution.Parse(Batch(138, 11, 20, Document(11, "<!DOCTYPE a [<!ENTITY x 'test'>]><a/>"))));
    }
}
