using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexFiscal.Capture;
using Xunit;

namespace NexFiscal.Capture.Tests;

public sealed class A1CompanyRegistrationTests
{
    private const string Cnpj = "11222333000181";
    private static byte[] FakePfx(bool withUf = false)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(withUf ? "CN=EMPRESA TESTE LTDA, S=SP, C=BR" : "CN=EMPRESA TESTE LTDA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        var tag = new Asn1Tag(TagClass.ContextSpecific, 0);
        writer.PushSequence(tag);
        writer.WriteObjectIdentifier("2.16.76.1.3.3");
        writer.PushSequence(tag);
        writer.WriteOctetString(System.Text.Encoding.ASCII.GetBytes(Cnpj));
        writer.PopSequence(tag);
        writer.PopSequence(tag);
        writer.PopSequence();
        req.CertificateExtensions.Add(new X509Extension("2.5.29.17", writer.Encode(), false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return cert.Export(X509ContentType.Pkcs12, "senha-teste");
    }

    private sealed class FakeStore : ICompanyRegistrationStore
    {
        public int Calls { get; private set; }
        public Task<CompanyRegistration> RegisterPausedAsync(A1CompanyIdentity identity, int env, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(Cnpj, identity.Cnpj);
            Assert.Equal("EMPRESA TESTE LTDA", identity.LegalName);
            Assert.Equal(1, env);
            return Task.FromResult(new CompanyRegistration(
                Guid.NewGuid().ToString(), identity.Cnpj, identity.LegalName, env, identity.Thumbprint, identity.ValidUntil, false, "AGUARDANDO_INICIALIZACAO"));
        }
    }

    [Fact]
    public async Task ValidA1_RegistersPausedCompany()
    {
        var store = new FakeStore();
        var sut = new A1CompanyRegistration(store);
        var company = await sut.RegisterAsync(FakePfx(), "senha-teste");
        Assert.False(company.Enabled);
        Assert.Equal(Cnpj, company.Cnpj);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public void Extracts_Uf_From_Subject_State()
    {
        var identity = A1CompanyCertificate.Inspect(FakePfx(withUf: true), "senha-teste");
        Assert.Equal("SP", identity.State);
    }

    [Fact]
    public async Task WrongPassword_DoesNotCreateCompany()
    {
        var store = new FakeStore();
        var sut = new A1CompanyRegistration(store);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => sut.RegisterAsync(FakePfx(), "errada"));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Register_Is_Paused_And_Has_No_User_Nsu()
    {
        var store = new FakeStore();
        var company = await new A1CompanyRegistration(store).RegisterAsync(FakePfx(), "senha-teste");
        Assert.False(company.Enabled);
        Assert.Equal("AGUARDANDO_INICIALIZACAO", company.CaptureStatus);
        Assert.Equal(1, company.Environment);
    }
}
