using System.Data.Common;
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
            var certificateStored = vault.IsStored(company.Id);
            var reason = !certificateStored ? "Certificado A1 não está armazenado."
                : !company.CursorConfigured ? "Checkpoint de NSU não inicializado. É necessário coordenar a migração do consumidor atual."
                : !company.Enabled ? "Empresa permanece pausada; o administrador deve autorizar a captura."
                : company.NextAllowedUtc is not null && company.NextAllowedUtc > DateTime.UtcNow
                    ? "Aguardando intervalo permitido de consulta."
                : !IsExplicitlyAuthorized ? "A captura exige confirmação administrativa da exclusividade do consumidor de NSU."
                : "Pré-requisitos internos atendidos. A validade do A1 será verificada antes da consulta.";

            result.Add(new
            {
                companyId = company.Id, cnpj = company.Cnpj,
                legalName = company.Name, company.Enabled, certificateStored,
                company.CursorConfigured,
                ready = certificateStored && company.CursorConfigured && company.Enabled &&
                        IsExplicitlyAuthorized &&
                        (company.NextAllowedUtc is null || company.NextAllowedUtc <= DateTime.UtcNow),
                reason,
                documentsStored = company.DocumentsStored,
                lastConsultationUtc = company.LastConsultationUtc,
                nextAllowedUtc = company.NextAllowedUtc,
            });
        }
        return new { companies = result, automaticCaptureEnabled = false };
    }

    /// <summary>
    /// Diagnóstico não fiscal: testa cadeia A1 e handshake HTTPS do serviço,
    /// sem SOAP e sem consultar/alterar NSU.
    /// </summary>
    public async Task<object> TestConnectivityAsync(string companyId, CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out _))
            throw new ArgumentException("Empresa inválida.");
        var company = (await ReadCompaniesAsync(ct)).SingleOrDefault(x => x.Id == companyId)
            ?? throw new InvalidOperationException("Empresa não localizada.");
        if (!vault.IsStored(companyId))
            throw new InvalidOperationException("Certificado A1 não encontrado.");

        var (pfx, password) = await vault.ReadAsync(companyId, ct);
        try
        {
            using var certificate = new X509Certificate2(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
            var chainOk = chain.Build(certificate);
            if (!chainOk)
                return new { reachable = false, certificateValid = false,
                    message = "Não foi possível validar a cadeia ou revogação do certificado A1. Confira os certificados ICP-Brasil no Windows." };

            using var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                CheckCertificateRevocationList = true,
                AllowAutoRedirect = false
            };
            handler.ClientCertificates.Add(certificate);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(NfeDistributionClient.ProductionUrl + "?WSDL"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new
            {
                reachable = response.IsSuccessStatusCode,
                certificateValid = true,
                httpStatus = (int)response.StatusCode,
                message = response.IsSuccessStatusCode
                    ? "Conectividade HTTPS com a SEFAZ validada. A autorização para distribuir NF-e ainda não foi confirmada."
                    : "Servidor respondeu ao teste HTTPS, mas não confirmou o WSDL. Verifique o acesso à SEFAZ."
            };
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
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
                using var certificate = new X509Certificate2(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
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
