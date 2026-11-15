using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NexFiscal.Web;

public enum NfseResponseStatus { Found, None, Rejected, RateLimited }

/// <summary>Um documento do lote do ADN já descompactado.</summary>
public sealed record NfseDistributedDocument(long? Nsu, string? AccessKey, string? DocumentType, byte[] Xml, string Sha256);

public sealed record NfseDistributionResponse(
    NfseResponseStatus Status, IReadOnlyList<NfseDistributedDocument> Documents, string? ErrorCode, string? ErrorMessage, TimeSpan? RetryAfter);

/// <summary>Dados mínimos de uma NFS-e completa.</summary>
public sealed record NfseDocumentData(string AccessKey, string? IssuerId, string? IssuerName, DateTime? IssuedAtUtc, decimal? TotalValue);

/// <summary>
/// API de distribuição de DF-e do ADN (Ambiente de Dados Nacional da NFS-e) para contribuintes.
///
/// Conferido na fonte oficial (gov.br/nfse, "Manual dos Contribuintes — APIs do ADN" v1.1 de 03/11/2026,
/// Anexo IV e esquemas XSD v1.01): método GET /DFe/{NSU}, parâmetro cnpjConsulta, perfis atendidos,
/// códigos de erro (E2220 = sem documentos a partir do NSU) e a estrutura do XML da NFS-e e dos eventos.
///
/// NÃO conferido na fonte oficial: os nomes dos campos do corpo JSON (LoteDFe, NSU, ChaveAcesso,
/// TipoDocumento, ArquivoXml em GZip+Base64, StatusProcessamento) e o parâmetro lote. O Swagger oficial
/// exige certificado até para ser lido. Esses nomes vêm de implementações públicas independentes que
/// concordam entre si; por isso a leitura é tolerante e qualquer resposta inesperada vira erro claro.
/// </summary>
public static class NfseProtocol
{
    /// <summary>Endereço de produção publicado em gov.br/nfse, "APIs - Prod. Restrita e Produção".</summary>
    public static readonly Uri ProductionBase = new("https://adn.nfse.gov.br/contribuintes/");

    public const int MaxDocumentsPerLot = 50;
    /// <summary>O manual não fixa intervalo entre consultas. Adota-se a mesma hora da NF-e depois que a fila esvazia.</summary>
    public static readonly TimeSpan WaitAfterEmpty = TimeSpan.FromHours(1);

    private const int MaxDocumentBytes = 6_000_000;
    private static readonly XNamespace Ns = "http://www.sped.fazenda.gov.br/nfse";
    private static readonly Regex KeyShape = new("^[0-9]{50}$", RegexOptions.Compiled);
    private static readonly HashSet<string> CancellationEvents = new(StringComparer.Ordinal) { "e101101", "e105102", "e305101" };

    public static bool IsAccessKey(string? value) => value is not null && KeyShape.IsMatch(value);

    /// <summary>GET /DFe/{NSU}: documentos com NSU maior que o informado, para o CNPJ consultado (obrigatório desde 03/11/2026).</summary>
    public static Uri BuildRequest(string cnpj, long lastNsu)
    {
        if (!Cnpj.IsValid(cnpj)) throw new ArgumentException("CNPJ inválido.", nameof(cnpj));
        if (lastNsu is < 0 or > NfeProtocol.MaxNsu) throw new ArgumentOutOfRangeException(nameof(lastNsu));
        return new Uri(ProductionBase, $"DFe/{lastNsu.ToString(CultureInfo.InvariantCulture)}?cnpjConsulta={cnpj}&lote=true");
    }

    public static NfseDistributionResponse ParseResponse(int httpStatus, string body, TimeSpan? retryAfter = null)
    {
        if (httpStatus == 429) return new(NfseResponseStatus.RateLimited, [], "429", "Limite de consultas do ADN atingido.", retryAfter);

        JsonElement root = default;
        var hasJson = false;
        if (!string.IsNullOrWhiteSpace(body))
            try { root = JsonDocument.Parse(body).RootElement; hasJson = root.ValueKind == JsonValueKind.Object; }
            catch (JsonException) { hasJson = false; }

        var (code, message) = hasJson ? FirstError(root) : (null, null);
        var status = hasJson ? Text(root, "StatusProcessamento") : null;

        if (httpStatus == 404 || status == "NENHUM_DOCUMENTO_LOCALIZADO" || code is "E2220" or "E2215")
            return new(NfseResponseStatus.None, [], code, message, null);
        if (httpStatus is >= 400 and < 500 || status == "REJEICAO")
            return new(NfseResponseStatus.Rejected, [], code ?? httpStatus.ToString(CultureInfo.InvariantCulture),
                message ?? "O ADN rejeitou a consulta.", null);
        if (httpStatus is < 200 or >= 300 || !hasJson) throw Invalid("resposta HTTP " + httpStatus);

        if (!TryGet(root, "LoteDFe", out var lot) || lot.ValueKind != JsonValueKind.Array)
            // 200 sem lote: tratar como fila vazia apenas se o próprio ADN disser isso; senão é formato desconhecido.
            throw Invalid("sem a lista LoteDFe");
        var documents = new List<NfseDistributedDocument>();
        foreach (var item in lot.EnumerateArray())
        {
            if (documents.Count >= MaxDocumentsPerLot) throw Invalid("lote com mais de 50 documentos");
            var encoded = Text(item, "ArquivoXml") ?? throw Invalid("documento sem ArquivoXml");
            var xml = Decompress(encoded);
            documents.Add(new NfseDistributedDocument(Number(item, "NSU"), Text(item, "ChaveAcesso"), Text(item, "TipoDocumento"),
                xml, Convert.ToHexString(SHA256.HashData(xml))));
        }
        return documents.Count == 0
            ? new(NfseResponseStatus.None, [], code, message, null)
            : new(NfseResponseStatus.Found, documents, null, null, null);
    }

    /// <summary>Lê uma NFS-e completa. Devolve null se o XML não for uma NFS-e íntegra do padrão nacional.</summary>
    public static NfseDocumentData? ReadNfse(byte[] xml)
    {
        XElement root;
        try { root = Load(xml).Root!; }
        catch (XmlException) { return null; }
        var inf = root.Element(Ns + "infNFSe");
        var id = (string?)inf?.Attribute("Id");
        if (root.Name != Ns + "NFSe" || inf is null || id is null || !id.StartsWith("NFS", StringComparison.Ordinal)) return null;
        var key = id[3..];
        if (!IsAccessKey(key)) return null;
        var issuer = inf.Element(Ns + "emit");
        var name = (string?)issuer?.Element(Ns + "xNome");
        return new NfseDocumentData(key,
            (string?)issuer?.Element(Ns + "CNPJ") ?? (string?)issuer?.Element(Ns + "CPF"),
            name is { Length: > 200 } ? name[..200] : name,
            DateTimeOffset.TryParse((string?)inf.Element(Ns + "dhProc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? date.UtcDateTime : null,
            decimal.TryParse((string?)inf.Element(Ns + "valores")?.Element(Ns + "vLiq"), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value) ? value : null);
    }

    /// <summary>Chave da NFS-e cancelada, se o XML for um evento de cancelamento. Senão null.</summary>
    public static string? CancelledKeyOf(byte[] xml)
    {
        try
        {
            var root = Load(xml).Root!;
            if (root.Name.LocalName != "evento") return null;
            var key = root.Descendants(Ns + "chNFSe").FirstOrDefault()?.Value;
            return IsAccessKey(key) && root.Descendants().Any(e => CancellationEvents.Contains(e.Name.LocalName)) ? key : null;
        }
        catch (XmlException) { return null; }
    }

    public static bool IsEvent(byte[] xml)
    {
        try { return Load(xml).Root!.Name.LocalName == "evento"; }
        catch (XmlException) { return false; }
    }

    // ---------- leitura tolerante do JSON (nomes de campo sem distinção de maiúsculas) ----------

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string? Text(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static (string? Code, string? Message) FirstError(JsonElement root)
    {
        if (!TryGet(root, "Erros", out var errors) || errors.ValueKind != JsonValueKind.Array) return (null, null);
        foreach (var error in errors.EnumerateArray())
            return (Text(error, "Codigo"), Text(error, "Descricao"));
        return (null, null);
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
        catch (Exception ex) when (ex is FormatException or InvalidDataException) { throw Invalid("ArquivoXml corrompido"); }
    }

    private static XDocument Load(byte[] xml)
    {
        using var reader = XmlReader.Create(new MemoryStream(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 12_000_000 });
        return XDocument.Load(reader);
    }

    private static NexFiscalException Invalid(string detail) => new("adn_invalid_response",
        $"O ADN da NFS-e devolveu uma resposta em formato inesperado ({detail}). Nada foi alterado. Envie esta mensagem ao suporte.", 502);
}

/// <summary>Consulta HTTPS ao ADN autenticando com o A1 da empresa. Os testes usam um ADN simulado.</summary>
public interface IAdnTransport
{
    Task<(int Status, string Body, TimeSpan? RetryAfter)> GetAsync(Uri url, X509Certificate2 clientCertificate, CancellationToken ct);
}

public sealed class HttpAdnTransport : IAdnTransport
{
    private const int MaxResponseBytes = 12_000_000;
    internal X509Certificate2? TrustedTestServerCertificate { get; init; }

    public async Task<(int Status, string Body, TimeSpan? RetryAfter)> GetAsync(Uri url, X509Certificate2 clientCertificate, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = [clientCertificate],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate
            }
        };
        if (TrustedTestServerCertificate is { } trusted)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && certificate.GetCertHashString() == trusted.GetCertHashString();

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new NexFiscalException("adn_certificate_rejected",
                    "O ADN da NFS-e recusou o certificado A1 desta empresa (acesso negado). Confira se o A1 é válido e pertence a este CNPJ.", 502);
            if ((int)response.StatusCode >= 500)
                throw new NexFiscalException("adn_http_error",
                    $"O ADN da NFS-e respondeu com erro HTTP {(int)response.StatusCode}. Será tentado novamente mais tarde.", 502);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > MaxResponseBytes)
                    throw new NexFiscalException("adn_invalid_response", "A resposta do ADN excedeu o tamanho esperado. Nada foi alterado.", 502);
                body.Write(buffer, 0, read);
            }
            return ((int)response.StatusCode, Encoding.UTF8.GetString(body.ToArray()), response.Headers.RetryAfter?.Delta);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NexFiscalException("adn_timeout", "O ADN da NFS-e não respondeu a tempo. Será tentado novamente mais tarde.", 504);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new NexFiscalException("adn_tls",
                "Não foi possível estabelecer conexão segura com o ADN da NFS-e. Verifique a cadeia de certificados ICP-Brasil no Windows e a data e hora do computador.", 502);
        }
        catch (HttpRequestException)
        {
            throw new NexFiscalException("adn_unreachable",
                "Não foi possível conectar ao ADN da NFS-e. Verifique a internet e o firewall deste computador. Será tentado novamente mais tarde.", 503);
        }
    }
}
