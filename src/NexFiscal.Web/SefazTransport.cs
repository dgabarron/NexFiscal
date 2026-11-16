using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NexFiscal.Web;

/// <summary>Envia um envelope SOAP à SEFAZ autenticando com o A1 da empresa. Os testes usam uma SEFAZ simulada.</summary>
public interface ISefazTransport
{
    Task<string> SendAsync(Uri url, string soapEnvelope, X509Certificate2 clientCertificate, CancellationToken ct);

    /// <summary>Mesmo envio, para outro serviço da SEFAZ (ex.: recepção de evento), identificado pela ação SOAP.</summary>
    Task<string> SendAsync(Uri url, string soapAction, string soapEnvelope, X509Certificate2 clientCertificate, CancellationToken ct);
}

/// <summary>
/// HTTPS com autenticação mútua (certificado do cliente). A validação do certificado do servidor
/// é a padrão do Windows e nunca é desligada.
/// </summary>
public sealed class HttpSefazTransport : ISefazTransport
{
    private const int MaxResponseBytes = 12_000_000;

    /// <summary>Somente para testes automatizados com servidor local: aceita o certificado de teste indicado.</summary>
    internal X509Certificate2? TrustedTestServerCertificate { get; init; }

    public Task<string> SendAsync(Uri url, string soapEnvelope, X509Certificate2 clientCertificate, CancellationToken ct) =>
        SendAsync(url, NfeProtocol.SoapAction, soapEnvelope, clientCertificate, ct);

    public async Task<string> SendAsync(Uri url, string soapAction, string soapEnvelope, X509Certificate2 clientCertificate, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
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
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(soapEnvelope, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
        request.Content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("action", "\"" + soapAction + "\""));

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new NexFiscalException("sefaz_certificate_rejected",
                    "A SEFAZ recusou o certificado A1 desta empresa (acesso negado). Confira se o A1 é válido, não foi revogado e pertence a este CNPJ.", 502);
            if (!response.IsSuccessStatusCode)
                throw new NexFiscalException("sefaz_http_error",
                    $"A SEFAZ respondeu com erro HTTP {(int)response.StatusCode}. A consulta não foi repetida; tente mais tarde.", 502);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > MaxResponseBytes)
                    throw new NexFiscalException("sefaz_invalid_response", "A resposta da SEFAZ excedeu o tamanho esperado. Nada foi alterado.", 502);
                body.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(body.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NexFiscalException("sefaz_timeout", "A SEFAZ não respondeu a tempo. Será tentado novamente mais tarde.", 504);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new NexFiscalException("sefaz_tls",
                "Não foi possível estabelecer conexão segura com a SEFAZ. Verifique se a cadeia de certificados ICP-Brasil está instalada no Windows e se a data e hora do computador estão corretas.", 502);
        }
        catch (HttpRequestException)
        {
            throw new NexFiscalException("sefaz_unreachable",
                "Não foi possível conectar à SEFAZ. Verifique a internet e o firewall deste computador. Será tentado novamente mais tarde.", 503);
        }
    }
}
