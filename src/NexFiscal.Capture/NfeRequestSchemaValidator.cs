using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace NexFiscal.Capture;

/// <summary>
/// Confere o XML distDFeInt antes de qualquer envio fiscal. Os XSDs v1.01
/// foram extraídos do repositório público nfephp-org/sped-nfe (PL_009_V4);
/// comparar com o pacote oficial de Distribuição DF-e v1.04 em homologação.
/// A validação nunca acessa rede nem arquivos externos.
/// </summary>
public static class NfeRequestSchemaValidator
{
    private static readonly Lazy<XmlSchemaSet> Schemas = new(CreateSchemas);
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    public static void Validate(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var errors = new List<string>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationType = ValidationType.Schema,
            Schemas = Schemas.Value,
            MaxCharactersInDocument = 32768
        };
        settings.ValidationEventHandler += (_, e) =>
            errors.Add(e.Exception?.Message ?? "Incompatibilidade com XSD.");
        using var validatingReader = XmlReader.Create(
            new StringReader(element.ToString(SaveOptions.DisableFormatting)), settings);
        while (validatingReader.Read()) { }
        if (errors.Count != 0)
            throw new InvalidDataException("Requisição distDFeInt inválida para o XSD local: " +
                                           string.Join("; ", errors.Take(3)));
    }

    private static XmlSchemaSet CreateSchemas()
    {
        var assembly = typeof(NfeRequestSchemaValidator).Assembly;
        XDocument Read(string name)
        {
            using var resource = assembly.GetManifestResourceStream(
                "NexFiscal.Capture.Schemas." + name)
                ?? throw new InvalidOperationException("XSD NF-e ausente: " + name);
            using var reader = XmlReader.Create(resource, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null
            });
            return XDocument.Load(reader);
        }

        var request = Read("distDFeInt_v1.01.xsd");
        var types = Read("tiposDistDFe_v1.01.xsd");
        // Substitui o include por seus tipos na mesma targetNamespace,
        // evitando resolvedores de arquivo/rede em produção.
        var include = request.Root?.Element(Xsd + "include")
            ?? throw new InvalidDataException("Referência ao XSD de tipos não encontrada.");
        include.ReplaceWith(types.Root!.Elements().Select(e => new XElement(e)).ToArray());
        var set = new XmlSchemaSet { XmlResolver = null };
        using var schemaReader = XmlReader.Create(
            new StringReader(request.ToString(SaveOptions.DisableFormatting)),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        set.Add(null, schemaReader);
        set.Compile();
        return set;
    }
}
