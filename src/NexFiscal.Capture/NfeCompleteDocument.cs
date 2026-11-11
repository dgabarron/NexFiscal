using System.Xml;
using System.Xml.Linq;

namespace NexFiscal.Capture;

/// <summary>
/// A entrega operacional aceita somente NF-e autorizada completa,
/// com nfeProc contendo NFe e protNFe. Resumos e eventos não são arquivos NF-e.
/// </summary>
public static class NfeCompleteDocument
{
    private static readonly XNamespace NfeNs = "http://www.portalfiscal.inf.br/nfe";

    public static bool IsProcNFe(NfeDistributedDocument doc)
    {
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        // O schema declarado pela SEFAZ e a raiz XML devem concordar.
        if (!doc.Schema.StartsWith("procNFe_v", StringComparison.OrdinalIgnoreCase) ||
            !doc.Schema.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase))
            return false;

        using var input = new MemoryStream(doc.XmlBytes, writable: false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10_000_000
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != NfeNs + "nfeProc")
            throw new InvalidDataException("docZip declara procNFe, mas o XML não contém nfeProc.");

        var nfe = root.Element(NfeNs + "NFe");
        var protocol = root.Element(NfeNs + "protNFe");
        if (nfe?.Element(NfeNs + "infNFe") is null ||
            protocol?.Element(NfeNs + "infProt") is null)
            throw new InvalidDataException("NF-e completa sem NFe/infNFe ou protNFe/infProt.");
        return true;
    }
}
