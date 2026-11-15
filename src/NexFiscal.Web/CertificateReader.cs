using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace NexFiscal.Web;

/// <summary>Dados extraídos do certificado. Nunca contém chave privada nem senha.</summary>
public sealed record CertificateInfo(
    string Cnpj, string LegalName, string? Uf, string Issuer,
    DateTime NotBeforeUtc, DateTime NotAfterUtc, string Thumbprint);

public static class Cnpj
{
    // Aceita o CNPJ numérico e o alfanumérico (12 posições A–Z/0–9 + 2 dígitos verificadores).
    private static readonly Regex Shape = new("^[0-9A-Z]{12}[0-9]{2}$", RegexOptions.Compiled);

    public static bool IsValid(string? value)
    {
        if (value is null || !Shape.IsMatch(value) || value.Distinct().Count() == 1) return false;
        return Digit(value, 12) == value[12] - '0' && Digit(value, 13) == value[13] - '0';
    }

    private static int Digit(string value, int length)
    {
        var sum = 0;
        for (var i = 0; i < length; i++)
            sum += (value[i] - '0') * ((length - 1 - i) % 8 + 2);
        var rest = sum % 11;
        return rest < 2 ? 0 : 11 - rest;
    }

    public static string Format(string cnpj) => cnpj.Length == 14
        ? $"{cnpj[..2]}.{cnpj[2..5]}.{cnpj[5..8]}/{cnpj[8..12]}-{cnpj[12..]}"
        : cnpj;
}

/// <summary>
/// Abre um PFX/P12 e extrai a identificação da empresa do próprio certificado (padrão ICP-Brasil).
/// Não instala nada no repositório de certificados do Windows.
/// </summary>
public static class CertificateReader
{
    public const int MaxPfxBytes = 256 * 1024;
    private const string IcpBrasilCnpjOid = "2.16.76.1.3.3";
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    private static readonly HashSet<string> Ufs = new(StringComparer.Ordinal)
    {
        "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG", "PA", "PB", "PR", "PE", "PI",
        "RJ", "RN", "RS", "RO", "RR", "SC", "SP", "SE", "TO"
    };

    /// <summary>Lê e valida o certificado. Validade (datas) é avaliada por quem chama.</summary>
    public static CertificateInfo Read(byte[] pfx, string password)
    {
        if (pfx.Length == 0 || pfx.Length > MaxPfxBytes)
            throw new NexFiscalException("invalid_file", "Arquivo de certificado inválido ou grande demais. Envie o arquivo .pfx ou .p12 do A1.", 400);

        // Distinguir "arquivo não é PFX" de "senha errada" antes de carregar a chave.
        Pkcs12Info structure;
        try { structure = Pkcs12Info.Decode(pfx, out _, skipCopy: true); }
        catch (CryptographicException)
        {
            throw new NexFiscalException("invalid_file", "O arquivo enviado não é um certificado A1 válido (.pfx ou .p12).", 400);
        }
        if (structure.IntegrityMode == Pkcs12IntegrityMode.Password && !structure.VerifyMac(password))
            throw new NexFiscalException("invalid_password", "Senha do certificado incorreta.", 400);

        X509Certificate2 certificate;
        try { certificate = new X509Certificate2(pfx, password, X509KeyStorageFlags.EphemeralKeySet); }
        catch (CryptographicException)
        {
            throw new NexFiscalException("invalid_file", "Não foi possível abrir o certificado. Confira a senha e se o arquivo é o A1 completo (.pfx ou .p12).", 400);
        }

        using (certificate)
        {
            if (!certificate.HasPrivateKey)
                throw new NexFiscalException("no_private_key",
                    "O arquivo não contém a chave privada. Envie o A1 completo (.pfx ou .p12), não apenas o certificado público.", 400);

            var commonName = Attribute(certificate.SubjectName, "2.5.4.3") ?? "";
            var cnpj = CnpjFromSubjectAlternativeName(certificate) ?? CnpjFromCommonName(commonName);
            if (cnpj is null)
                throw new NexFiscalException("no_cnpj",
                    "Este certificado não identifica um CNPJ válido. O NexFiscal aceita apenas certificado A1 de pessoa jurídica (e-CNPJ).", 400);

            var legalName = commonName.Split(':')[0].Trim();
            if (legalName.Length == 0) legalName = "Razão social não identificada";
            if (legalName.Length > 200) legalName = legalName[..200];

            var state = Attribute(certificate.SubjectName, "2.5.4.8")?.Trim().ToUpperInvariant();
            var issuer = Attribute(certificate.IssuerName, "2.5.4.3") ?? certificate.IssuerName.Name;
            if (issuer.Length > 200) issuer = issuer[..200];

            return new CertificateInfo(cnpj, legalName, state is not null && Ufs.Contains(state) ? state : null, issuer,
                certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(),
                certificate.GetCertHashString(HashAlgorithmName.SHA256));
        }
    }

    private static string? Attribute(X500DistinguishedName name, string oid)
    {
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == oid)
                return rdn.GetSingleElementValue();
        }
        return null;
    }

    private static string? CnpjFromCommonName(string commonName)
    {
        var index = commonName.LastIndexOf(':');
        if (index < 0) return null;
        var candidate = commonName[(index + 1)..].Trim().ToUpperInvariant();
        return Cnpj.IsValid(candidate) ? candidate : null;
    }

    /// <summary>ICP-Brasil: otherName 2.16.76.1.3.3 no Subject Alternative Name traz o CNPJ do titular.</summary>
    private static string? CnpjFromSubjectAlternativeName(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions[SubjectAlternativeNameOid];
        if (extension is null) return null;
        try
        {
            var names = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            var otherNameTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            while (names.HasData)
            {
                if (!names.PeekTag().HasSameClassAndValue(otherNameTag)) { names.ReadEncodedValue(); continue; }
                var otherName = names.ReadSequence(otherNameTag);
                if (otherName.ReadObjectIdentifier() != IcpBrasilCnpjOid) continue;
                var value = otherName.ReadSequence(otherNameTag); // [0] EXPLICIT
                var tag = value.PeekTag();
                var text = tag.HasSameClassAndValue(Asn1Tag.PrimitiveOctetString)
                    ? Encoding.ASCII.GetString(value.ReadOctetString())
                    : value.ReadCharacterString((UniversalTagNumber)tag.TagValue);
                var candidate = text.Trim().ToUpperInvariant();
                if (Cnpj.IsValid(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is AsnContentException or InvalidOperationException or ArgumentException)
        {
            // Extensão fora do padrão: tratar como "sem CNPJ no SAN".
        }
        return null;
    }
}
