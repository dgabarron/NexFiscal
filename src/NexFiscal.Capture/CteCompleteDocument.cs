using System.Xml;
using System.Xml.Linq;

namespace NexFiscal.Capture;

/// <summary>CT-e processado, completo e protocolado; não confundir com eventos.</summary>
public static class CteCompleteDocument
{
    private static readonly XNamespace Cte = "http://www.portalfiscal.inf.br/cte";

    public static bool IsProcCTe(NfeDistributedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Schema.StartsWith("procCTe_v", StringComparison.OrdinalIgnoreCase) ||
            !document.Schema.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase))
            return false;

        using var stream = new MemoryStream(document.XmlBytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10_000_000
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != Cte + "cteProc")
            throw new InvalidDataException("docZip declara procCTe, mas não contém cteProc.");
        if (root.Element(Cte + "CTe")?.Element(Cte + "infCte") is null ||
            root.Element(Cte + "protCTe")?.Element(Cte + "infProt") is null)
            throw new InvalidDataException("CT-e completo sem CTe/infCte ou protCTe/infProt.");
        return true;
    }
}
