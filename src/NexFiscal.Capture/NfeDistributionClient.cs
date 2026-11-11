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

public sealed record NfeDistributedDocument(long Nsu, string Schema, byte[] XmlBytes, string Sha256);
public sealed record NfeDistributionResult(int Status, string Reason, long LastNsu, long MaxNsu,
    IReadOnlyList<NfeDistributedDocument> Documents)
{
    public bool NoDocuments => Status == 137;
    public bool DocumentsFound => Status == 138;
    public bool ConsumptionBlocked => Status == 656;
}

/// <summary>
/// Motor de transporte NF-e. Não consulta automaticamente: o chamador deve validar
/// autorização, checkpoint persistente, prazo mínimo e exclusividade do consumidor NSU.
/// </summary>
public sealed class NfeDistributionClient
{
    public static readonly Uri ProductionUrl = new(
        "https://www1.nfe.fazenda.gov.br/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx");
    public static readonly Uri HomologationUrl = new(
        "https://hom.nfe.fazenda.gov.br/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx");

    private static readonly XNamespace NfeNs = "http://www.portalfiscal.inf.br/nfe";
    private static readonly XNamespace WsNs = "http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe";
    private static readonly XNamespace SoapNs = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly HashSet<int> ValidUfCodes =
        [11,12,13,14,15,16,17,21,22,23,24,25,26,27,28,29,31,32,33,35,41,42,43,50,51,52,53];
    private static readonly IReadOnlyDictionary<string, int> StateCodes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["RO"]=11, ["AC"]=12, ["AM"]=13, ["RR"]=14, ["PA"]=15, ["AP"]=16, ["TO"]=17,
        ["MA"]=21, ["PI"]=22, ["CE"]=23, ["RN"]=24, ["PB"]=25, ["PE"]=26, ["AL"]=27,
        ["SE"]=28, ["BA"]=29, ["MG"]=31, ["ES"]=32, ["RJ"]=33, ["SP"]=35, ["PR"]=41,
        ["SC"]=42, ["RS"]=43, ["MS"]=50, ["MT"]=51, ["GO"]=52, ["DF"]=53
    };

    public static string BuildRequest(string cnpj, long lastNsu, int environment, int? authorizerUf = null)
    {
        if (cnpj is null || cnpj.Length != 14 || !cnpj.All(char.IsAsciiDigit))
            throw new ArgumentException("CNPJ deve conter 14 dígitos sem máscara.", nameof(cnpj));
        if (lastNsu is < 0 or > 999_999_999_999_999L)
            throw new ArgumentOutOfRangeException(nameof(lastNsu));
        if (environment is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(environment));
        if (authorizerUf.HasValue && !ValidUfCodes.Contains(authorizerUf.Value))
            throw new ArgumentOutOfRangeException(nameof(authorizerUf), "Informe um código IBGE de UF válido.");

        var data = new XElement(NfeNs + "distDFeInt",
            new XAttribute("versao", "1.01"),
            new XElement(NfeNs + "tpAmb", environment));
        // XSD distDFeInt_v1.01: cUFAutor é opcional (minOccurs=0),
        // porém, quando enviado, DEVE ser UF IBGE; 91 é inválido.
        if (authorizerUf is int uf)
            data.Add(new XElement(NfeNs + "cUFAutor", uf));
        data.Add(new XElement(NfeNs + "CNPJ", cnpj));
        data.Add(new XElement(NfeNs + "distNSU",
            new XElement(NfeNs + "ultNSU", lastNsu.ToString("D15", CultureInfo.InvariantCulture))));

        NfeRequestSchemaValidator.Validate(data);

        var request = new XDocument(new XElement(SoapNs + "Envelope",
            new XElement(SoapNs + "Body",
                new XElement(WsNs + "nfeDistDFeInteresse",
                    new XElement(WsNs + "nfeDadosMsg", data)))));
        return request.ToString(SaveOptions.DisableFormatting);
    }

    public async Task<NfeDistributionResult> QueryAsync(
        X509Certificate2 certificate, string cnpj, long lastNsu, int environment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey) throw new ArgumentException("A1 sem chave privada.", nameof(certificate));
        if (DateTimeOffset.UtcNow >= certificate.NotAfter.ToUniversalTime() ||
            DateTimeOffset.UtcNow < certificate.NotBefore.ToUniversalTime())
            throw new InvalidOperationException("A1 fora da validade.");

        // Derivar a UF do endereço do titular no certificado, não usar o 91
        // (código de órgão da RFB, fora do tipo TCodUfIBGE).
        var state = A1CompanyCertificate.GetSubjectState(certificate);
        int? authorizerUf = state is not null && StateCodes.TryGetValue(state, out var ibge)
            ? ibge : null;
        var xml = BuildRequest(cnpj, lastNsu, environment, authorizerUf);
        using var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            CheckCertificateRevocationList = true,
            SslProtocols = SslProtocols.Tls12,
            AutomaticDecompression = DecompressionMethods.None
        };
        handler.ClientCertificates.Add(certificate);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        var url = environment == 1 ? ProductionUrl : HomologationUrl;
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(xml, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml")
        {
            CharSet = "utf-8"
        };
        request.Content.Headers.ContentType.Parameters.Add(
            new NameValueHeaderValue("action", "\"http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe/nfeDistDFeInteresse\""));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        // Retornos com muitos docZip não devem consumir memória ilimitada.
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (limited.Length + n > 12_000_000) throw new InvalidDataException("Resposta SEFAZ excedeu o limite de 12 MB.");
            await limited.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        return ParseResponse(Encoding.UTF8.GetString(limited.ToArray()));
    }

    public static NfeDistributionResult ParseResponse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Length > 12_000_000) throw new InvalidDataException("Retorno SOAP muito grande.");
        using var reader = XmlReader.Create(new StringReader(response), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 12_000_000
        });
        var xml = XDocument.Load(reader);
        if (xml.Descendants().Any(x => x.Name.LocalName == "Fault" &&
            (x.Name.NamespaceName == "http://www.w3.org/2003/05/soap-envelope" ||
             x.Name.NamespaceName == "http://schemas.xmlsoap.org/soap/envelope/")))
            throw new InvalidDataException("O serviço SEFAZ retornou SOAP Fault; verifique o contrato e os logs, sem repetir a consulta imediatamente.");
        var payload = xml.Descendants(NfeNs + "retDistDFeInt").FirstOrDefault()
            ?? throw new InvalidDataException("Resposta SEFAZ sem retDistDFeInt.");
        var statusText = (string?)payload.Element(NfeNs + "cStat");
        if (!int.TryParse(statusText, NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            throw new InvalidDataException("Resposta SEFAZ sem cStat válido.");
        var reason = (string?)payload.Element(NfeNs + "xMotivo") ?? "";
        static long Nsu(XElement parent, XName name)
        {
            var s = (string?)parent.Element(name) ?? "0";
            if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
                result is < 0 or > 999_999_999_999_999L)
                throw new InvalidDataException("NSU inválido na resposta SEFAZ.");
            return result;
        }
        var last = Nsu(payload, NfeNs + "ultNSU");
        var max = Nsu(payload, NfeNs + "maxNSU");
        if (last > max && status is 137 or 138)
            throw new InvalidDataException("ultNSU maior que maxNSU.");
        var documents = new List<NfeDistributedDocument>();
        var docZips = payload.Descendants(NfeNs + "docZip").ToArray();
        if (status != 138 && docZips.Length != 0)
            throw new InvalidDataException("Resposta fiscal inconsistente: docZip sem cStat 138.");
        if (status == 138 && docZips.Length > 50)
            throw new InvalidDataException("Resposta fiscal excede o lote máximo de 50 documentos.");
        if (status == 138)
        {
            var seenNsus = new HashSet<long>();
            foreach (var doc in docZips)
            {
                var nsu = (string?)doc.Attribute("NSU");
                var schema = (string?)doc.Attribute("schema");
                if (string.IsNullOrWhiteSpace(nsu) ||
                    !long.TryParse(nsu, NumberStyles.None, CultureInfo.InvariantCulture, out var docNsu) ||
                    docNsu <= 0 || docNsu > last ||
                    string.IsNullOrWhiteSpace(schema) || schema.Length > 200 ||
                    !seenNsus.Add(docNsu))
                    throw new InvalidDataException("docZip sem metadados válidos.");
                byte[] zipped;
                try { zipped = Convert.FromBase64String(doc.Value.Trim()); }
                catch (FormatException ex) { throw new InvalidDataException("docZip base64 inválido.", ex); }
                using var compressed = new MemoryStream(zipped);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + count > 10_000_000)
                        throw new InvalidDataException("XML descompactado excede 10 MB.");
                    output.Write(buffer, 0, count);
                }
                var bytes = output.ToArray();
                // Verificação estrutural, sem entidades externas.
                using var xmlStream = new MemoryStream(bytes);
                using var xmlReader = XmlReader.Create(xmlStream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = 10_000_000
                });
                while (xmlReader.Read()) { }
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                documents.Add(new NfeDistributedDocument(docNsu, schema, bytes, hash));
            }
            if (documents.Count == 0) throw new InvalidDataException("SEFAZ indicou documentos mas não retornou docZip.");
        }
        return new NfeDistributionResult(status, reason, last, max, documents);
    }
}
