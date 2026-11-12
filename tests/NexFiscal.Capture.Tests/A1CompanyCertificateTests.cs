using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexFiscal.Capture;
using Xunit;

namespace NexFiscal.Capture.Tests;

public sealed class A1CompanyCertificateTests
{
    private const string Cnpj = "11222333000181";

    private static byte[] CreateCertificate(string password, bool withCnpj)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=EMPRESA TESTE LTDA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new AsnWriter(AsnEncodingRules.DER);
        san.PushSequence();
        if (withCnpj)
        {
            san.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            san.WriteObjectIdentifier("2.16.76.1.3.3");
            san.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            san.WriteOctetString(System.Text.Encoding.ASCII.GetBytes(Cnpj));
            san.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            san.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        }
        san.PopSequence();
        request.CertificateExtensions.Add(new X509Extension("2.5.29.17", san.Encode(), false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        return certificate.Export(X509ContentType.Pkcs12, password);
    }

    [Fact]
    public void Extracts_Cnpj_And_Corporate_Name_From_A1()
    {
        var file = CreateCertificate("senha-teste", true);
        var company = A1CompanyCertificate.Inspect(file, "senha-teste");
        Assert.Equal(Cnpj, company.Cnpj);
        Assert.Equal("EMPRESA TESTE LTDA", company.LegalName);
        Assert.Null(company.State);
        Assert.True(company.HasPrivateKey);
    }

    [Fact]
    public void Rejects_Wrong_Password()
    {
        var file = CreateCertificate("senha-correta", true);
        Assert.ThrowsAny<CryptographicException>(() => A1CompanyCertificate.Inspect(file, "errada"));
    }

    [Fact]
    public void Rejects_Personal_Or_Unsupported_Certificate_Without_Cnpj()
    {
        var file = CreateCertificate("senha-teste", false);
        Assert.Throws<InvalidDataException>(() => A1CompanyCertificate.Inspect(file, "senha-teste"));
    }
}
