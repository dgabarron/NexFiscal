using System.Xml;
using NexFiscal.Capture;
using System.Text;
using Xunit;

namespace NexFiscal.Capture.Tests;

public sealed class NfeHistoricalBulkImportTests
{
    private const string Key = "35260804854181000174550010000352481664948100";

    private static byte[] Build(string key = Key, string? recipient = "04854181000174",
        string? issuer = "42150391001495", string date = "2019-08-17T10:15:00-03:00")
    {
        var xml = "<nfeProc xmlns='http://www.portalfiscal.inf.br/nfe'>" +
            "<NFe><infNFe Id='NFe" + key + "'><ide><dhEmi>" + date +
            "</dhEmi></ide><emit><CNPJ>" + issuer +
            "</CNPJ></emit><dest><CNPJ>" + recipient +
            "</CNPJ></dest></infNFe></NFe><protNFe><infProt><chNFe>" +
            key + "</chNFe></infProt></protNFe></nfeProc>";
        return Encoding.UTF8.GetBytes(xml);
    }

    [Fact]
    public void HistoricalDocument_ExtractsDateAndKeyBeforeNinetyDayWindow()
    {
        var doc = NfeHistoricalDocumentInspector.Inspect(Build());
        Assert.NotNull(doc);
        Assert.Equal(Key, doc.Key);
        Assert.Equal(new DateOnly(2019, 8, 17), doc.IssueDate);
        Assert.Equal("04854181000174", doc.Recipient);
        Assert.Equal("42150391001495", doc.Issuer);
    }

    [Fact]
    public void HistoricalDocument_RejectsDifferentProtocolKey()
    {
        var xml = Encoding.UTF8.GetString(Build()).Replace("<chNFe>" + Key + "</chNFe>",
            "<chNFe>35260804854181000174550010000352481664948101</chNFe>");
        Assert.Throws<InvalidDataException>(() =>
            NfeHistoricalDocumentInspector.Inspect(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void SummaryIsNotACompleteInvoice()
    {
        Assert.Null(NfeHistoricalDocumentInspector.Inspect(
            Encoding.UTF8.GetBytes("<resNFe xmlns='http://www.portalfiscal.inf.br/nfe'/>")));
    }

    [Fact]
    public void XmlWithExternalEntitiesIsRejected()
    {
        var xml = "<!DOCTYPE x [<!ENTITY ext SYSTEM 'file:///etc/passwd'>]>" +
                  "<nfeProc xmlns='http://www.portalfiscal.inf.br/nfe'>&ext;</nfeProc>";
        Assert.Throws<XmlException>(() =>
            NfeHistoricalDocumentInspector.Inspect(Encoding.UTF8.GetBytes(xml)));
    }
}
