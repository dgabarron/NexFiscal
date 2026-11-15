using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NexFiscal.Web;

namespace NexFiscal.Tests;

/// <summary>Certificados de teste autoassinados no formato ICP-Brasil (CNPJ no SAN, OID 2.16.76.1.3.3). CNPJs fictícios.</summary>
public static class TestCertificates
{
    public const string Password = "Senha#A1;\"x' y";
    public const string CnpjA = "11222333000181";
    public const string CnpjB = "11444777000161";
    public static readonly DateTimeOffset Today = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public static byte[] Create(string? sanCnpj = CnpjA, string commonName = "EMPRESA TESTE LTDA:" + CnpjA,
        string? state = "SP", DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null,
        string password = Password, bool withPrivateKey = true)
    {
        using var key = RSA.Create(2048);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCountryOrRegion("BR");
        subject.AddOrganizationName("ICP-Brasil");
        if (state is not null) subject.AddStateOrProvinceName(state);
        subject.AddCommonName(commonName);
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (sanCnpj is not null) request.CertificateExtensions.Add(new X509Extension("2.5.29.17", San(sanCnpj), false));
        using var certificate = request.CreateSelfSigned(notBefore ?? Today.AddDays(-30), notAfter ?? Today.AddDays(335));
        return withPrivateKey
            ? certificate.Export(X509ContentType.Pfx, password)
            : new X509Certificate2(certificate.RawData).Export(X509ContentType.Pfx, password);
    }

    private static byte[] San(string cnpj)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            var otherName = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            using (writer.PushSequence(otherName))
            {
                writer.WriteObjectIdentifier("2.16.76.1.3.3");
                using (writer.PushSequence(otherName)) writer.WriteOctetString(Encoding.ASCII.GetBytes(cnpj));
            }
            writer.WriteCharacterString(UniversalTagNumber.IA5String, "fiscal@empresa.example",
                new Asn1Tag(TagClass.ContextSpecific, 1));
        }
        return writer.Encode();
    }

    public static CertificateUpload Upload(byte[]? pfx = null, string? password = Password, bool replace = false) =>
        new(Convert.ToBase64String(pfx ?? Create()), password, replace);
}

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now = TestCertificates.Today;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Substitui o HANA nos testes da Função 2. Reproduz a regra de unicidade CNPJ+ambiente e o bloqueio por histórico.</summary>
public sealed class InMemoryCompanyStore : ICompanyStore
{
    public readonly List<CompanyRecord> Rows = [];
    public readonly HashSet<string> History = [];
    public bool Unavailable;
    public bool FailWrites;
    public int Writes;

    private void Guard(bool write)
    {
        if (Unavailable)
            throw new NexFiscalException("hana_not_configured", "Configure e valide a conexão SAP HANA antes de usar o cadastro de empresas.", 409);
        if (write && FailWrites) throw new NexFiscalException("hana_error", "O SAP HANA recusou a operação (código 2048).", 502);
        if (write) Writes++;
    }

    public Task<IReadOnlyList<CompanyRecord>> ListAsync(CancellationToken ct)
    {
        Guard(false);
        return Task.FromResult<IReadOnlyList<CompanyRecord>>(
            Rows.Select(r => r with { HasFiscalHistory = History.Contains(r.CompanyId) }).ToList());
    }

    public Task InsertAsync(CompanyRecord company, CancellationToken ct)
    {
        Guard(true);
        if (Rows.Any(r => r.Cnpj == company.Cnpj && r.Environment == company.Environment))
            throw new NexFiscalException("duplicate", "Já existe um registro com esses dados no SAP HANA.", 409);
        Rows.Add(company);
        return Task.CompletedTask;
    }

    public Task SetCertificateAsync(string companyId, CertificateInfo c, DateTime nowUtc, CancellationToken ct)
    {
        Guard(true);
        var i = Rows.FindIndex(r => r.CompanyId == companyId);
        Rows[i] = Rows[i] with
        {
            Uf = c.Uf, Issuer = c.Issuer, Thumbprint = c.Thumbprint, NotBeforeUtc = c.NotBeforeUtc,
            NotAfterUtc = c.NotAfterUtc, CertificateUpdatedAtUtc = nowUtc, ArchivedAtUtc = null
        };
        return Task.CompletedTask;
    }

    public Task<bool> DeleteIfNoHistoryAsync(string companyId, CancellationToken ct)
    {
        Guard(true);
        if (History.Contains(companyId)) return Task.FromResult(false);
        Rows.RemoveAll(r => r.CompanyId == companyId);
        return Task.FromResult(true);
    }

    public Task ArchiveAsync(string companyId, DateTime nowUtc, CancellationToken ct)
    {
        Guard(true);
        var i = Rows.FindIndex(r => r.CompanyId == companyId);
        Rows[i] = Rows[i] with { ArchivedAtUtc = nowUtc };
        return Task.CompletedTask;
    }
}

/// <summary>Diretório de dados temporário + repositório em memória. Start() equivale a reiniciar o backend.</summary>
public sealed class CompanyInstallation : IDisposable
{
    public readonly string Root = Directory.CreateTempSubdirectory("nexfiscal-a1-").FullName;
    public readonly InMemoryCompanyStore Store = new();
    public readonly TestClock Clock = new();
    public readonly ListLoggerProvider Logs = new();
    public string KeysDirectory => Path.Combine(Root, ".nexfiscal-keys");
    public string VaultDirectory => Path.Combine(Root, ".nexfiscal-certs");

    public CompanyService Start()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(KeysDirectory), b => b.SetApplicationName("NexFiscal"));
        var logs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        var vault = new CertificateVault(provider, new NexFiscalDataDirectory(Root), logs.CreateLogger<CertificateVault>());
        return new CompanyService(Store, vault, Clock, logs.CreateLogger<CompanyService>());
    }

    public string[] VaultFiles() => Directory.Exists(VaultDirectory) ? Directory.GetFiles(VaultDirectory).Order().ToArray() : [];
    public string VaultFile(string companyId) => Path.Combine(VaultDirectory, companyId + ".a1.dat");
    public void LoseKeys() => Directory.Delete(KeysDirectory, recursive: true);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
