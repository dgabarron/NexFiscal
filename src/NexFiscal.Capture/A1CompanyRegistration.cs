namespace NexFiscal.Capture;

public sealed record CompanyRegistration(
    string CompanyId, string Cnpj, string LegalName, int Environment,
    string CertificateThumbprint, DateTimeOffset CertificateExpiresAt, bool Enabled,
    string CaptureStatus);

public interface ICompanyRegistrationStore
{
    Task<CompanyRegistration> RegisterPausedAsync(
        A1CompanyIdentity identity, int environment, CancellationToken cancellationToken);
}

/// <summary>
/// Cadastro automático por e-CNPJ A1. Não aceita NSU do usuário.
/// A empresa é criada pausada; o checkpoint só poderá surgir após
/// inicialização autorizada do fluxo fiscal por outro serviço.
/// </summary>
public sealed class A1CompanyRegistration(ICompanyRegistrationStore store)
{
    public async Task<CompanyRegistration> RegisterAsync(
        byte[] pkcs12, string password, CancellationToken cancellationToken = default)
    {
        var identity = A1CompanyCertificate.Inspect(pkcs12, password);
        return await store.RegisterPausedAsync(identity, environment: 1, cancellationToken);
    }
}
