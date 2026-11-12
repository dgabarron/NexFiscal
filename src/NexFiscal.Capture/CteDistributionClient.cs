using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NexFiscal.Capture;

/// <summary>
/// Distribuição CT-e (NT 2015.002): namespace, serviço e NSU distintos dos da NF-e.
/// Não possui efeitos de estado: o chamador controla exclusividade e intervalo.
/// </summary>
public sealed class CteDistributionClient
{
    public static readonly Uri ProductionUrl =
        new("https://www1.cte.fazenda.gov.br/CTeDistribuicaoDFe/CTeDistribuicaoDFe.asmx");
    public static readonly Uri HomologationUrl =
        new("https://hom1.cte.fazenda.gov.br/CTeDistribuicaoDFe/CTeDistribuicaoDFe.asmx");

    private static readonly XNamespace Cte = "http://www.portalfiscal.inf.br/cte";
    private static readonly XNamespace Ws = "http://www.portalfiscal.inf.br/cte/wsdl/CTeDistribuicaoDFe";
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";

    private static readonly IReadOnlyDictionary<string, int> UfCode = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["RO"]=11, ["AC"]=12, ["AM"]=13, ["RR"]=14, ["PA"]=15, ["AP"]=16, ["TO"]=17,
        ["MA"]=21, ["PI"]=22, ["CE"]=23, ["RN"]=24, ["PB"]=25, ["PE"]=26, ["AL"]=27,
        ["SE"]=28, ["BA"]=29, ["MG"]=31, ["ES"]=32, ["RJ"]=33, ["SP"]=35, ["PR"]=41,
        ["SC"]=42, ["RS"]=43, ["MS"]=50, ["MT"]=51, ["GO"]=52, ["DF"]=53
    };

    public static string BuildRequest(string cnpj, string uf, long lastNsu, int environment)
    {
        if (cnpj is null || cnpj.Length != 14 || !cnpj.All(char.IsAsciiDigit))
            throw new ArgumentException("CNPJ do CT-e inválido.", nameof(cnpj));
        if (!UfCode.TryGetValue(uf ?? "", out var ufCode))
            throw new ArgumentException("A UF do certificado A1 não está disponível para o CT-e.", nameof(uf));
        if (lastNsu is < 0 or > 999_999_999_999_999L)
            throw new ArgumentOutOfRangeException(nameof(lastNsu));
        if (environment is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(environment));

        var data = new XElement(Cte + "distDFeInt", new XAttribute("versao", "1.00"),
            new XElement(Cte + "tpAmb", environment),
            new XElement(Cte + "cUFAutor", ufCode),
            new XElement(Cte + "CNPJ", cnpj),
            new XElement(Cte + "distNSU", new XElement(Cte + "ultNSU",
                lastNsu.ToString("D15", CultureInfo.InvariantCulture))));

        return new XDocument(new XElement(Soap + "Envelope",
            new XElement(Soap + "Body",
                new XElement(Ws + "cteDistDFeInteresse",
                    new XElement(Ws + "cteDadosMsg", data)))))
            .ToString(SaveOptions.DisableFormatting);
    }

    public async Task<NfeDistributionResult> QueryAsync(
        X509Certificate2 certificate, string cnpj, string uf, long lastNsu,
        int environment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime() ||
            DateTime.UtcNow < certificate.NotBefore.ToUniversalTime())
            throw new InvalidOperationException("Certificado A1 inválido ou fora da validade.");

        var soapXml = BuildRequest(cnpj, uf, lastNsu, environment);
        using var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            CheckCertificateRevocationList = true,
            SslProtocols = SslProtocols.Tls12,
            AutomaticDecompression = DecompressionMethods.None
        };
        handler.ClientCertificates.Add(certificate);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            environment == 1 ? ProductionUrl : HomologationUrl);
        request.Content = new StringContent(soapXml, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml")
        {
            CharSet = "utf-8"
        };
        request.Content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("action",
            "\"http://www.portalfiscal.inf.br/cte/wsdl/CTeDistribuicaoDFe/cteDistDFeInteresse\""));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + count > 12_000_000)
                throw new InvalidDataException("Resposta CT-e excedeu o limite de 12 MB.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return ParseResponse(Encoding.UTF8.GetString(bytes.ToArray()));
    }

    public static NfeDistributionResult ParseResponse(string soapXml)
    {
        using var reader = XmlReader.Create(new StringReader(soapXml),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 12_000_000
            });
        var xml = XDocument.Load(reader);
        if (xml.Descendants().Any(x => x.Name.LocalName == "Fault" &&
            (x.Name.NamespaceName == "http://www.w3.org/2003/05/soap-envelope" ||
             x.Name.NamespaceName == "http://schemas.xmlsoap.org/soap/envelope/")))
            throw new InvalidDataException("CT-e retornou SOAP Fault.");
        var payload = xml.Descendants(Cte + "retDistDFeInt").SingleOrDefault()
            ?? throw new InvalidDataException("CT-e sem retDistDFeInt.");
        if (!int.TryParse((string?)payload.Element(Cte + "cStat"), out var status))
            throw new InvalidDataException("CT-e retornou cStat inválido.");
        var reason = (string?)payload.Element(Cte + "xMotivo") ?? "";
        static long Nsu(XElement p, XName name)
        {
            var raw = (string?)p.Element(name) ?? "0";
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value is < 0 or > 999_999_999_999_999L)
                throw new InvalidDataException("NSU inválido no retorno CT-e.");
            return value;
        }
        var last = Nsu(payload, Cte + "ultNSU");
        var max = Nsu(payload, Cte + "maxNSU");
        if (status is 137 or 138 && last > max)
            throw new InvalidDataException("NSU do CT-e maior que maxNSU.");
        var documents = new List<NfeDistributedDocument>();
        var zipped = payload.Descendants(Cte + "docZip").ToArray();
        if (zipped.Length > 50 || (status != 138 && zipped.Length > 0) ||
            (status == 138 && zipped.Length == 0))
            throw new InvalidDataException("Lote CT-e inconsistente.");
        var seen = new HashSet<long>();
        foreach (var doc in zipped)
        {
            var nsuText = (string?)doc.Attribute("NSU");
            var schema = (string?)doc.Attribute("schema");
            if (!long.TryParse(nsuText, NumberStyles.None, CultureInfo.InvariantCulture, out var nsu) ||
                nsu <= 0 || nsu > last || !seen.Add(nsu) || string.IsNullOrWhiteSpace(schema) ||
                schema.Length > 200)
                throw new InvalidDataException("Documento CT-e com metadados inválidos.");
            byte[] compressed;
            try { compressed = Convert.FromBase64String(doc.Value.Trim()); }
            catch (FormatException ex) { throw new InvalidDataException("Base64 do CT-e inválido.", ex); }
            using var source = new MemoryStream(compressed);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = gzip.Read(buffer)) > 0)
            {
                if (output.Length + count > 10_000_000)
                    throw new InvalidDataException("XML CT-e maior que 10 MB.");
                output.Write(buffer, 0, count);
            }
            var data = output.ToArray();
            using var xmlReader = XmlReader.Create(new MemoryStream(data),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 10_000_000
                });
            while (xmlReader.Read()) { }
            documents.Add(new NfeDistributedDocument(nsu, schema, data,
                Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()));
        }
        return new NfeDistributionResult(status, reason, last, max, documents);
    }
}
