using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NexFiscal.Web;

/// <summary>Tipo de documento devolvido em um docZip. Só <see cref="Complete"/> é XML completo de NF-e.</summary>
public enum NfeDocumentKind { Complete, Summary, Event, Other }

/// <summary>Um docZip já descompactado. <see cref="Nsu"/> é opcional desde a NT 2014.002 v1.15.</summary>
public sealed record NfeDistributedDocument(long? Nsu, string Schema, byte[] Xml, string Sha256)
{
    public NfeDocumentKind Kind =>
        Schema.StartsWith("procNFe_", StringComparison.OrdinalIgnoreCase) ? NfeDocumentKind.Complete :
        Schema.StartsWith("resNFe_", StringComparison.OrdinalIgnoreCase) ? NfeDocumentKind.Summary :
        Schema.StartsWith("resEvento_", StringComparison.OrdinalIgnoreCase) ||
        Schema.StartsWith("procEventoNFe_", StringComparison.OrdinalIgnoreCase) ? NfeDocumentKind.Event :
        NfeDocumentKind.Other;
}

/// <summary>Resposta retDistDFeInt. ultNSU e maxNSU são opcionais no leiaute.</summary>
public sealed record NfeDistributionResponse(
    int Status, string Reason, long? LastNsu, long? MaxNsu, IReadOnlyList<NfeDistributedDocument> Documents);

/// <summary>Dados fiscais mínimos de uma NF-e, extraídos do procNFe ou do resNFe.</summary>
public sealed record NfeDocumentData(
    string AccessKey, bool Complete, int? Situation, string? IssuerId, string? IssuerName, DateTime? IssuedAtUtc, decimal? TotalValue);

/// <summary>
/// Mensagens do Web Service NFeDistribuicaoDFe do Ambiente Nacional, conforme a
/// Nota Técnica 2014.002 v1.40 (03/07/2026) e o pacote de schemas PL_NFeDistDFe_104.
/// Somente montagem e leitura de XML: não acessa rede.
/// </summary>
public static class NfeProtocol
{
    /// <summary>Endereço publicado no Portal Nacional da NF-e, "Relação de Serviços Web", Ambiente Nacional.</summary>
    public static readonly Uri ProductionUrl = new("https://www1.nfe.fazenda.gov.br/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx");
    public const string SoapAction = "http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe/nfeDistDFeInteresse";

    public const int StatusNoDocuments = 137;
    public const int StatusDocumentsFound = 138;
    public const int StatusImproperUse = 656;
    public const int MaxDocumentsPerLot = 50;
    public const long MaxNsu = 999_999_999_999_999L;

    /// <summary>NT item 3.5 e 3.11.4: após 137, ultNSU = maxNSU ou 656, aguardar uma hora.</summary>
    public static readonly TimeSpan MandatoryWait = TimeSpan.FromHours(1);

    private const int MaxResponseChars = 12_000_000;
    private const int MaxDocumentBytes = 6_000_000;
    private static readonly XNamespace Nfe = "http://www.portalfiscal.inf.br/nfe";
    private static readonly XNamespace Ws = "http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe";
    private static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly Regex AccessKeyShape = new("^[0-9A-Z]{44}$", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, int> UfCodes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["RO"] = 11, ["AC"] = 12, ["AM"] = 13, ["RR"] = 14, ["PA"] = 15, ["AP"] = 16, ["TO"] = 17, ["MA"] = 21, ["PI"] = 22,
        ["CE"] = 23, ["RN"] = 24, ["PB"] = 25, ["PE"] = 26, ["AL"] = 27, ["SE"] = 28, ["BA"] = 29, ["MG"] = 31, ["ES"] = 32,
        ["RJ"] = 33, ["SP"] = 35, ["PR"] = 41, ["SC"] = 42, ["RS"] = 43, ["MS"] = 50, ["MT"] = 51, ["GO"] = 52, ["DF"] = 53
    };

    public static bool IsAccessKey(string? value) => value is not null && AccessKeyShape.IsMatch(value);

    /// <summary>distNSU: continua a distribuição a partir do último NSU recebido.</summary>
    public static string BuildDistributionRequest(string cnpj, string? uf, long lastNsu) =>
        BuildRequest(cnpj, uf, new XElement(Nfe + "distNSU", new XElement(Nfe + "ultNSU", FormatNsu(lastNsu))));

    /// <summary>consNSU: consulta pontual de um NSU identificado como faltante (NT item 3.6).</summary>
    public static string BuildNsuQueryRequest(string cnpj, string? uf, long nsu) =>
        BuildRequest(cnpj, uf, new XElement(Nfe + "consNSU", new XElement(Nfe + "NSU", FormatNsu(nsu))));

    /// <summary>Área de dados (distDFeInt) de um envelope, para validação contra o schema oficial.</summary>
    public static XElement DataOf(string soapEnvelope) =>
        XDocument.Parse(soapEnvelope).Descendants(Nfe + "distDFeInt").Single();

    private static string BuildRequest(string cnpj, string? uf, XElement query)
    {
        if (!Cnpj.IsValid(cnpj)) throw new ArgumentException("CNPJ inválido.", nameof(cnpj));
        var data = new XElement(Nfe + "distDFeInt", new XAttribute("versao", "1.01"),
            new XElement(Nfe + "tpAmb", CompanyService.ProductionEnvironment));
        // cUFAutor é opcional (ocorrência 0-1) e só aceita UF da tabela do IBGE: omitir quando a UF não é conhecida.
        if (uf is not null && UfCodes.TryGetValue(uf, out var code)) data.Add(new XElement(Nfe + "cUFAutor", code));
        data.Add(new XElement(Nfe + "CNPJ", cnpj), query);

        var envelope = new XElement(Soap12 + "Envelope", new XAttribute(XNamespace.Xmlns + "soap12", Soap12),
            new XElement(Soap12 + "Body",
                new XElement(Ws + "nfeDistDFeInteresse", new XElement(Ws + "nfeDadosMsg", data))));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + envelope.ToString(SaveOptions.DisableFormatting);
    }

    private static string FormatNsu(long nsu)
    {
        if (nsu is < 0 or > MaxNsu) throw new ArgumentOutOfRangeException(nameof(nsu));
        return nsu.ToString("D15", CultureInfo.InvariantCulture);
    }

    public static NfeDistributionResponse ParseResponse(string soap)
    {
        if (soap.Length > MaxResponseChars) throw Invalid("resposta grande demais");
        XDocument xml;
        try { xml = Load(soap); }
        catch (XmlException) { throw Invalid("resposta não é XML"); }

        if (xml.Descendants().Any(e => e.Name.LocalName == "Fault"))
            throw new NexFiscalException("sefaz_fault",
                "A SEFAZ devolveu um erro de serviço (SOAP Fault). A consulta não foi repetida; tente mais tarde.", 502);
        var ret = xml.Descendants(Nfe + "retDistDFeInt").FirstOrDefault() ?? throw Invalid("sem retDistDFeInt");
        if (!int.TryParse((string?)ret.Element(Nfe + "cStat"), NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            throw Invalid("sem cStat");

        var documents = new List<NfeDistributedDocument>();
        foreach (var zip in ret.Descendants(Nfe + "docZip"))
        {
            if (documents.Count >= MaxDocumentsPerLot) throw Invalid("lote com mais de 50 documentos");
            var schema = (string?)zip.Attribute("schema");
            if (string.IsNullOrWhiteSpace(schema) || schema.Length > 200) throw Invalid("docZip sem schema");
            var bytes = Decompress(zip.Value);
            documents.Add(new NfeDistributedDocument(Nsu((string?)zip.Attribute("NSU")), schema.Trim(), bytes,
                Convert.ToHexString(SHA256.HashData(bytes))));
        }
        return new NfeDistributionResponse(status, ((string?)ret.Element(Nfe + "xMotivo") ?? "").Trim(),
            Nsu((string?)ret.Element(Nfe + "ultNSU")), Nsu((string?)ret.Element(Nfe + "maxNSU")), documents);
    }

    private static long? Nsu(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > MaxNsu)
            throw Invalid("NSU inválido");
        return value;
    }

    private static byte[] Decompress(string base64)
    {
        try
        {
            using var input = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (output.Length + read > MaxDocumentBytes) throw Invalid("documento grande demais");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            throw Invalid("docZip corrompido");
        }
    }

    /// <summary>
    /// Lê um procNFe ou resNFe. Devolve null se o XML não for um documento íntegro daquele tipo —
    /// um procNFe só é "completo" com nfeProc/NFe/infNFe e protocolo para a mesma chave.
    /// </summary>
    public static NfeDocumentData? ReadDocument(NfeDocumentKind kind, byte[] xmlBytes)
    {
        XElement root;
        try { root = Load(xmlBytes).Root!; }
        catch (XmlException) { return null; }

        if (kind == NfeDocumentKind.Complete)
        {
            if (root.Name != Nfe + "nfeProc") return null;
            var inf = root.Element(Nfe + "NFe")?.Element(Nfe + "infNFe");
            var protocol = root.Element(Nfe + "protNFe")?.Element(Nfe + "infProt");
            var id = (string?)inf?.Attribute("Id");
            if (inf is null || protocol is null || id is null || !id.StartsWith("NFe", StringComparison.Ordinal)) return null;
            var key = id[3..];
            if (!IsAccessKey(key) || (string?)protocol.Element(Nfe + "chNFe") != key) return null;
            var issuer = inf.Element(Nfe + "emit");
            return new NfeDocumentData(key, true, null,
                (string?)issuer?.Element(Nfe + "CNPJ") ?? (string?)issuer?.Element(Nfe + "CPF"),
                Limit((string?)issuer?.Element(Nfe + "xNome")),
                Utc((string?)inf.Element(Nfe + "ide")?.Element(Nfe + "dhEmi")),
                Money((string?)inf.Element(Nfe + "total")?.Element(Nfe + "ICMSTot")?.Element(Nfe + "vNF")));
        }
        if (kind == NfeDocumentKind.Summary)
        {
            var key = (string?)root.Element(Nfe + "chNFe");
            if (root.Name != Nfe + "resNFe" || !IsAccessKey(key)) return null;
            return new NfeDocumentData(key!, false,
                int.TryParse((string?)root.Element(Nfe + "cSitNFe"), out var situation) ? situation : null,
                (string?)root.Element(Nfe + "CNPJ") ?? (string?)root.Element(Nfe + "CPF"),
                Limit((string?)root.Element(Nfe + "xNome")), Utc((string?)root.Element(Nfe + "dhEmi")),
                Money((string?)root.Element(Nfe + "vNF")));
        }
        return null;
    }

    /// <summary>Chave da NF-e cancelada, se o documento for um evento de cancelamento (110111). Senão null.</summary>
    public static string? CancelledKeyOf(byte[] eventXml)
    {
        try
        {
            var root = Load(eventXml).Root!;
            var type = root.Descendants(Nfe + "tpEvento").FirstOrDefault()?.Value;
            var key = root.Descendants(Nfe + "chNFe").FirstOrDefault()?.Value;
            return type == "110111" && IsAccessKey(key) ? key : null;
        }
        catch (XmlException) { return null; }
    }

    private static string? Limit(string? text) => text is { Length: > 200 } ? text[..200] : text;

    private static DateTime? Utc(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime : null;

    private static decimal? Money(string? text) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxResponseChars
    };

    internal static XDocument Load(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), SafeXml);
        return XDocument.Load(reader);
    }

    private static XDocument Load(byte[] xml)
    {
        using var reader = XmlReader.Create(new MemoryStream(xml), SafeXml);
        return XDocument.Load(reader);
    }

    private static NexFiscalException Invalid(string detail) =>
        new("sefaz_invalid_response", $"A SEFAZ devolveu uma resposta que não pôde ser interpretada ({detail}). Nada foi alterado.", 502);
}
