using System.Xml;
using System.Xml.Linq;

namespace NexFiscal.Capture;

/// <summary>Extrai e valida chave de acesso de 44 dígitos de procNFe e procCTe.</summary>
public static class FiscalAccessKey
{
    private static readonly XNamespace Nfe = "http://www.portalfiscal.inf.br/nfe";
    private static readonly XNamespace Cte = "http://www.portalfiscal.inf.br/cte";

    public static string Extract(NfeDistributedDocument document, string documentType)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var stream = new MemoryStream(document.XmlBytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10_000_000
        });
        var root = XDocument.Load(reader).Root
            ?? throw new InvalidDataException("XML fiscal sem elemento raiz.");
        string? id;
        string? protocol;
        string prefix;
        if (documentType == "NFE")
        {
            if (root.Name != Nfe + "nfeProc")
                throw new InvalidDataException("Documento não é procNFe.");
            id = (string?)root.Element(Nfe + "NFe")?.Element(Nfe + "infNFe")?.Attribute("Id");
            protocol = (string?)root.Element(Nfe + "protNFe")?.Element(Nfe + "infProt")?.Element(Nfe + "chNFe");
            prefix = "NFe";
        }
        else if (documentType == "CTE")
        {
            if (root.Name != Cte + "cteProc")
                throw new InvalidDataException("Documento não é procCTe.");
            id = (string?)root.Element(Cte + "CTe")?.Element(Cte + "infCte")?.Attribute("Id");
            protocol = (string?)root.Element(Cte + "protCTe")?.Element(Cte + "infProt")?.Element(Cte + "chCTe");
            prefix = "CTe";
        }
        else throw new ArgumentException("Tipo fiscal não suportado.", nameof(documentType));

        if (id is null || !id.StartsWith(prefix, StringComparison.Ordinal) ||
            id.Length != prefix.Length + 44)
            throw new InvalidDataException("Identificador fiscal não contém chave válida.");
        var key = id[prefix.Length..];
        if (key.Length != 44 || !key.All(char.IsAsciiDigit))
            throw new InvalidDataException("Chave de acesso deve ter 44 dígitos.");
        if (protocol != key)
            throw new InvalidDataException("Chave da nota difere da chave do protocolo.");
        return key;
    }
}
