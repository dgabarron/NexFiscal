using System.Data.Common;
using System.Net.Sockets;
using System.Security.Authentication;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexFiscal.Capture;

/// <summary>
/// Estado de pré-voo e tentativa unitária de captura NF-e.
/// Não cria checkpoint nem assume exclusividade com outros consumidores.
/// </summary>
public sealed class NfeCaptureCoordinator(
    HanaSetup hana, CompanyCertificateVault vault, IConfiguration configuration)
{
    private static readonly SemaphoreSlim LocalQueryLock = new(1, 1);
    private bool IsExplicitlyAuthorized =>
        configuration.GetValue<bool>("NexFiscal:Capture:AllowControlledQuery") &&
        configuration.GetValue<bool>("NexFiscal:Capture:ExclusiveConsumerConfirmed");

    public async Task<object> StatusAsync(CancellationToken ct)
    {
        var companies = await ReadCompaniesAsync(ct);
        var result = new List<object>();
        foreach (var company in companies)
        {
            var certificateStatus = vault.StorageStatus(company.Id);
            var certificateStored = certificateStatus == "stored";
            var reason = certificateStatus == "missing"
                ? "Arquivo A1 não localizado nesta instalação."
                : certificateStatus == "unreadable"
                    ? "O arquivo A1 existe, mas não pode ser aberto. Preserve as chaves e verifique o usuário Windows que executa o serviço."
                : company.NextAllowedUtc is not null && company.NextAllowedUtc > DateTime.UtcNow
                    ? "Consulta anterior registrada. Aguarde o intervalo obrigatório antes de tentar novamente."
                : !company.CursorConfigured ? "Pronto para a primeira consulta. O NSU inicial será criado automaticamente ao clicar em Baixar NF-e."
                : "Pronto para a próxima consulta manual. O NSU é controlado automaticamente pelo NexFiscal.";

            result.Add(new
            {
                companyId = company.Id, cnpj = company.Cnpj,
                legalName = company.Name, company.Enabled, certificateStored, certificateStatus,
                company.CursorConfigured,
                lastNsu = company.LastNsu?.ToString("D15"),
                maxNsu = (string?)null, // maxNSU ainda não é persistido pelo cursor HANA NF-e.
                ready = certificateStored &&
                        (company.NextAllowedUtc is null || company.NextAllowedUtc <= DateTime.UtcNow),
                reason,
                documentsStored = company.DocumentsStored,
                lastConsultationUtc = company.LastConsultationUtc,
                nextAllowedUtc = company.NextAllowedUtc,
            });
        }
        return new { companies = result, automaticCaptureEnabled = true };
    }

    /// <summary>
    /// Diagnóstico não fiscal: testa cadeia A1 e handshake HTTPS do serviço,
    /// sem SOAP e sem consultar/alterar NSU.
    /// </summary>
    public async Task<object> TestConnectivityAsync(string companyId, CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out _))
            throw new ArgumentException("Empresa inválida.");

        // Retornar etapa e categoria, nunca mensagens técnicas que possam
        // conter nomes de arquivos, dados do certificado ou credenciais.
        string stage = "consulta_empresa_hana";
        try
        {
            var company = (await ReadCompaniesAsync(ct)).SingleOrDefault(x => x.Id == companyId)
                ?? throw new InvalidOperationException("Empresa não localizada.");
            stage = "cofre_a1";
            if (!vault.IsStored(companyId))
                return new { reachable = false, certificateValid = false,
                    stage, code = "A1_NOT_FOUND", message = "Certificado A1 não encontrado no cofre." };

            var (pfx, password) = await vault.ReadAsync(companyId, ct);
            try
            {
                stage = "abertura_a1";
                // Schannel no Windows não suporta chaves privadas efêmeras no TLS cliente (0x8009030E).
                // UserKeySet não usa PersistKeySet: .NET 8 remove o contêiner temporário ao liberar o A1.
                using var certificate = new X509Certificate2(pfx, password,
                    OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
                if (!certificate.HasPrivateKey)
                    return new { reachable = false, certificateValid = false, stage,
                        code = "A1_NO_PRIVATE_KEY", message = "Certificado A1 sem chave privada." };
                var now = DateTime.UtcNow;
                if (now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
                    return new { reachable = false, certificateValid = false, stage,
                        code = "A1_EXPIRED", message = "Certificado A1 fora do prazo de validade." };

                stage = "validacao_cadeia_a1";
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
                if (!chain.Build(certificate))
                    return new
                    {
                        reachable = false, certificateValid = false, stage,
                        code = "A1_CHAIN_REJECTED",
                        chainErrors = chain.ChainStatus.Select(s => s.Status.ToString()).Distinct().ToArray(),
                        message = "Falha na validação ICP-Brasil. Confira a cadeia de confiança, CRL/OCSP e acesso à Internet no servidor."
                    };

                stage = "https_sefaz";
                using var handler = new HttpClientHandler
                {
                    ClientCertificateOptions = ClientCertificateOption.Manual,
                    CheckCertificateRevocationList = true,
                    AllowAutoRedirect = false,
                SslProtocols = SslProtocols.Tls12
                };
                handler.ClientCertificates.Add(certificate);
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    new Uri(NfeDistributionClient.ProductionUrl + "?WSDL"));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                return new
                {
                    reachable = response.IsSuccessStatusCode,
                    certificateValid = true, stage,
                    code = response.IsSuccessStatusCode ? "HTTPS_OK" : "HTTP_NON_SUCCESS",
                    httpStatus = (int)response.StatusCode,
                    message = response.IsSuccessStatusCode
                        ? "Conexão HTTPS SEFAZ estabelecida. Isso não confirma autorização para distribuição NF-e."
                        : "A SEFAZ respondeu ao HTTPS, mas não retornou sucesso para o WSDL."
                };
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // HResult e tipo são limitados a metadados de diagnóstico.
            // Nunca enviar ex.Message/StackTrace ao navegador.
            var chain = new List<string>();
            for (Exception? cause = ex; cause is not null && chain.Count < 8; cause = cause.InnerException)
            {
                var detail = cause.GetType().Name + ":0x" + cause.HResult.ToString("X8");
                if (cause is SocketException socket)
                    detail += ":SOCKET_" + socket.SocketErrorCode;
                if (cause is Win32Exception win32)
                    detail += ":WIN32_" + win32.NativeErrorCode;
                chain.Add(detail);
            }
            var rootCause = ex;
            while (rootCause.InnerException is not null)
                rootCause = rootCause.InnerException;
            var code = rootCause switch
            {
                SocketException socket when socket.SocketErrorCode == SocketError.HostNotFound => "DNS_HOST_NOT_FOUND",
                SocketException socket when socket.SocketErrorCode == SocketError.ConnectionRefused => "TCP_CONNECTION_REFUSED",
                SocketException socket when socket.SocketErrorCode == SocketError.TimedOut => "TCP_TIMEOUT",
                SocketException => "SOCKET_CONNECTION_ERROR",
                AuthenticationException => "TLS_HANDSHAKE_OR_CERTIFICATE_ERROR",
                System.Security.Cryptography.CryptographicException => "CERTIFICATE_CRYPTO_ERROR",
                TaskCanceledException => "HTTP_TIMEOUT",
                IOException => "TLS_CONNECTION_CLOSED",
                _ when ex is HttpRequestException => "HTTP_TRANSPORT_ERROR",
                _ => "UNEXPECTED_ERROR"
            };
            return new
            {
                reachable = false, certificateValid = false, stage, code,
                exceptionType = ex.GetType().Name,
                hresult = $"0x{ex.HResult:X8}",
                causes = chain,
                tlsPolicy = "TLS 1.2",
                message = stage switch
                {
                    "consulta_empresa_hana" => "Erro ao consultar o cadastro no HANA.",
                    "cofre_a1" => "Não foi possível ler ou descriptografar o certificado A1 armazenado.",
                    "abertura_a1" => "Não foi possível abrir o certificado A1 protegido.",
                    "validacao_cadeia_a1" => "Erro durante a validação da cadeia do certificado.",
                    "https_sefaz" => "A conexão HTTPS com a SEFAZ falhou. Confira as causas técnicas abaixo.",
                    _ => "Falha no diagnóstico de conectividade."
                }
            };
        }
    }

    public async Task<object> TestOnceAsync(string companyId, CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out _))
            throw new ArgumentException("Empresa inválida.");

        // Esse bloqueio é deliberado: a configuração não cria novos checkpoints
        // e não autoriza capturar do CNPJ que ainda tenha consumidor concorrente.
        if (!IsExplicitlyAuthorized)
            throw new InvalidOperationException("Confirmação administrativa de exclusividade do consumidor NF-e ainda não registrada.");

        if (!await LocalQueryLock.WaitAsync(0, ct))
            throw new InvalidOperationException("Já existe consulta NF-e em andamento.");
        try
        {
            var company = (await ReadCompaniesAsync(ct)).SingleOrDefault(x => x.Id == companyId)
                ?? throw new InvalidOperationException("Empresa não localizada.");
            if (!company.Enabled || !company.CursorConfigured || company.LastNsu is null)
                throw new InvalidOperationException("Empresa pausada ou checkpoint ainda não autorizado.");
            if (company.NextAllowedUtc is not null && company.NextAllowedUtc > DateTime.UtcNow)
                throw new InvalidOperationException("A SEFAZ ainda exige aguardar o intervalo de consulta.");
            if (!vault.IsStored(companyId))
                throw new InvalidOperationException("Certificado A1 não encontrado.");

            var (pfx, password) = await vault.ReadAsync(companyId, ct);
            try
            {
                using var certificate = new X509Certificate2(pfx, password,
                    OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
                if (!certificate.HasPrivateKey)
                    throw new InvalidOperationException("A1 não contém chave privada.");
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
                if (!chain.Build(certificate))
                    throw new InvalidOperationException("Não foi possível validar a cadeia e revogação do certificado A1.");

                // Revalidação imediatamente anterior à chamada da SEFAZ.
                var response = await new NfeDistributionClient().QueryAsync(
                    certificate, company.Cnpj, company.LastNsu.Value, 1, ct);
                if (response.Status is not (137 or 138 or 656))
                    throw new InvalidOperationException("Resposta SEFAZ não autorizou o avanço do checkpoint.");
                var inserted = await new HanaNfeDistributionStore(HanaSetup.Factory, hana.ConnectionString!)
                    .SaveBatchAsync(companyId, company.LastNsu.Value, response, ct);
                return new
                {
                    status = response.Status, reason = response.Reason, documentsStored = inserted,
                    consultationCompletedUtc = DateTime.UtcNow
                };
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        finally { LocalQueryLock.Release(); }
    }

    private async Task<List<CompanyCaptureState>> ReadCompaniesAsync(CancellationToken ct)
    {
        var cs = hana.ConnectionString ?? throw new InvalidOperationException("HANA ainda não configurado.");
        await using var conn = HanaSetup.Factory.CreateConnection()
            ?? throw new InvalidOperationException("Provider HANA indisponível.");
        conn.ConnectionString = cs;
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT C."CompanyId", C."Cnpj", C."LegalName", C."Enabled",
                   R."LastNsu", R."NextAllowedAtUtc",
                   (SELECT COUNT(*) FROM "BRAI_NEX"."BRAI_NEX_Envelope" E
                    WHERE E."CompanyId" = C."CompanyId" AND E."DocumentType" = 'NFE') AS "Docs",
                   (SELECT MAX(L."StartedAtUtc") FROM "BRAI_NEX"."BRAI_NEX_CaptureLog" L
                    WHERE L."CompanyId" = C."CompanyId" AND L."DocumentType" = 'NFE') AS "LastAttempt"
            FROM "BRAI_NEX"."BRAI_NEX_Company" C
            LEFT JOIN "BRAI_NEX"."BRAI_NEX_CaptureCursor" R
              ON R."CompanyId" = C."CompanyId" AND R."DocumentType" = 'NFE'
            ORDER BY C."CreatedAtUtc" DESC
            """;
        var result = new List<CompanyCaptureState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CompanyCaptureState(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Convert.ToInt32(reader.GetValue(3)) != 0,
                !reader.IsDBNull(4),
                reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4)),
                reader.IsDBNull(5) ? null : Convert.ToDateTime(reader.GetValue(5)),
                Convert.ToInt64(reader.GetValue(6)),
                reader.IsDBNull(7) ? null : Convert.ToDateTime(reader.GetValue(7))));
        }
        return result;
    }

    private sealed record CompanyCaptureState(
        string Id, string Cnpj, string Name, bool Enabled, bool CursorConfigured,
        long? LastNsu, DateTime? NextAllowedUtc, long DocumentsStored, DateTime? LastConsultationUtc);
}
