namespace NexFiscal.Capture;

public sealed record CompanyRegistration(
    string CompanyId, string Cnpj, string LegalName, int Environment,
    string CertificateThumbprint, DateTimeOffset CertificateExpiresAt, bool Enabled);

public interface ICompanyRegistrationStore
{
    Task<CompanyRegistration> RegisterPausedAsync(
        A1CompanyIdentity identity, int environment, long initialNsu, CancellationToken cancellationToken);
}

/// <summary>
/// Provisiona empresa e cursor NF-e em estado pausado. Não ativa captura,
/// não armazena o arquivo PKCS#12 e não consulta a SEFAZ.
/// </summary>
public sealed class A1CompanyRegistration(ICompanyRegistrationStore store)
{
    public async Task<CompanyRegistration> RegisterAsync(
        byte[] pkcs12, string password, int environment, long initialNsu,
        CancellationToken cancellationToken = default)
    {
        if (environment is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(environment), "Ambiente deve ser 1 (produção) ou 2 (homologação).");
        if (initialNsu < 0 || initialNsu > NfeDistribution.MaxNsu)
            throw new ArgumentOutOfRangeException(nameof(initialNsu));

        // O NSU é fornecido explicitamente pelo operador ou recuperado de
        // checkpoint anterior comprovado. Nunca inferir NSU a partir do A1.
        var identity = A1CompanyCertificate.Inspect(pkcs12, password);
        return await store.RegisterPausedAsync(identity, environment, initialNsu, cancellationToken);
    }
}
