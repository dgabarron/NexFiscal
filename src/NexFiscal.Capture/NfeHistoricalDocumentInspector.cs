using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace NexFiscal.Capture;

/// <summary>
/// Inspeção de procNFe histórico independente da camada web, sem acesso à rede.
/// Exige chave/protocolo coerentes e extrai data e CNPJs do XML completo.
/// </summary>
public static class NfeHistoricalDocumentInspector
{
    private const int MaxXmlSize = 10_000_000;
    public sealed record Document(string Key, DateOnly? IssueDate, string? Issuer, string? Recipient);

    public static Document? Inspect(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaxXmlSize)
            throw new InvalidDataException("XML fora do limite.");
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaxXmlSize
        });
        var root = XDocument.Load(reader).Root;
        XNamespace ns = "http://www.portalfiscal.inf.br/nfe";
        if (root?.Name != ns + "nfeProc") return null;
        var info = root.Element(ns + "NFe")?.Element(ns + "infNFe");
        var prot = root.Element(ns + "protNFe")?.Element(ns + "infProt");
        if (info is null || prot is null) throw new InvalidDataException("NF-e sem protocolo.");
        var id = (string?)info.Attribute("Id");
        if (id is not { Length: 47 } || !id.StartsWith("NFe", StringComparison.Ordinal) ||
            !id[3..].All(char.IsAsciiDigit) ||
            id[3..] != (string?)prot.Element(ns + "chNFe"))
            throw new InvalidDataException("Chave do documento não corresponde ao protocolo.");
        var issuer = (string?)info.Element(ns + "emit")?.Element(ns + "CNPJ");
        var recipient = (string?)info.Element(ns + "dest")?.Element(ns + "CNPJ");
        var rawDate = (string?)info.Element(ns + "ide")?.Element(ns + "dhEmi") ??
            (string?)info.Element(ns + "ide")?.Element(ns + "dEmi");
        DateOnly? date = DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateOnly.FromDateTime(parsed.DateTime)
            : DateOnly.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day : null;
        return new Document(id[3..], date, issuer, recipient);
    }
}
