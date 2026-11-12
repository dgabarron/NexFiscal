using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NexFiscal.Capture;

public sealed record A1CompanyIdentity(
    string Cnpj, string LegalName, string? State,
    DateTimeOffset ValidFrom, DateTimeOffset ValidUntil,
    string Issuer, string Thumbprint, bool HasPrivateKey);

/// <summary>
/// Inspects e-CNPJ A1 in memory. Never logs, persists, or returns PKCS#12 bytes/password.
/// ICP-Brasil chain/revocation and SEFAZ authorization are separate operational checks.
/// </summary>
public static class A1CompanyCertificate
{
    private const string CnpjOid = "2.16.76.1.3.3";
    private const string LegalNameOid = "2.16.76.1.3.8";
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    public static A1CompanyIdentity Inspect(byte[] pkcs12, string password, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);
        ArgumentNullException.ThrowIfNull(password);
        if (pkcs12.Length is 0 or > 5_000_000)
            throw new InvalidDataException("Arquivo A1 vazio ou acima de 5 MB.");

        using var cert = new X509Certificate2(pkcs12, password, X509KeyStorageFlags.EphemeralKeySet);

        if (!cert.HasPrivateKey)
            throw new InvalidDataException("O certificado não contém chave privada.");
        var now = at ?? DateTimeOffset.UtcNow;
        var beginning = new DateTimeOffset(cert.NotBefore.ToUniversalTime());
        var expiration = new DateTimeOffset(cert.NotAfter.ToUniversalTime());
        if (now < beginning || now >= expiration)
            throw new InvalidDataException("Certificado fora da validade.");

        var attrs = ReadBrazilianOtherNames(cert);
        if (!attrs.TryGetValue(CnpjOid, out var rawCnpj) ||
            rawCnpj.Length != 14 || !rawCnpj.All(char.IsAsciiDigit) ||
            !ValidCnpj(rawCnpj))
            throw new InvalidDataException("CNPJ válido não encontrado no campo ICP-Brasil do certificado.");

        // DOC-ICP-05 establishes corporate name in Subject CN; some certificate
        // profiles also include its dedicated OID 2.16.76.1.3.8.
        var rawName = attrs.GetValueOrDefault(LegalNameOid);
        if (string.IsNullOrWhiteSpace(rawName))
            rawName = cert.GetNameInfo(X509NameType.SimpleName, false);
        var name = (rawName ?? "").Trim();
        if (name.EndsWith(":" + rawCnpj, StringComparison.Ordinal))
            name = name[..^(rawCnpj.Length + 1)].Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("Razão social não encontrada no certificado.");

        // ST/S (2.5.4.8) identifica UF do endereço do titular no perfil e-CNPJ.
        // NUNCA inferir a UF da empresa pelo RG/título do responsável no SAN.
        var state = ReadSubjectState(cert);
        return new A1CompanyIdentity(rawCnpj, name, state, beginning, expiration,
            cert.Issuer, cert.Thumbprint, true);
    }

    /// <summary>
    /// Lê exclusivamente o estado do titular (OID 2.5.4.8), sem inferir pelo CNPJ.
    /// </summary>
    public static string? GetSubjectState(X509Certificate2 certificate) => ReadSubjectState(certificate);

    private static string? ReadSubjectState(X509Certificate2 cert)
    {
        // Em X500Name formatado pelo Windows, o atributo 2.5.4.8 pode
        // aparecer como S ou ST; usamos o DER para evitar separadores
        // escapados em valores de outros atributos.
        try
        {
            var reader = new AsnReader(cert.SubjectName.RawData, AsnEncodingRules.DER);
            var name = reader.ReadSequence();
            while (name.HasData)
            {
                var set = name.ReadSetOf(skipSortOrderValidation: true);
                while (set.HasData)
                {
                    var attribute = set.ReadSequence();
                    var oid = attribute.ReadObjectIdentifier();
                    if (oid != "2.5.4.8") continue;
                    var tag = attribute.PeekTag();
                    string? raw = null;
                    if (tag.HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.UTF8String)))
                        raw = attribute.ReadCharacterString(UniversalTagNumber.UTF8String);
                    else if (tag.HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.PrintableString)))
                        raw = attribute.ReadCharacterString(UniversalTagNumber.PrintableString);
                    else if (tag.HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.BMPString)))
                        raw = attribute.ReadCharacterString(UniversalTagNumber.BMPString);
                    var value = raw?.Trim().ToUpperInvariant();
                    const string states = "|AC|AL|AP|AM|BA|CE|DF|ES|GO|MA|MT|MS|MG|PA|PB|PR|PE|PI|RJ|RN|RS|RO|RR|SC|SP|SE|TO|";
                    return value is { Length: 2 } && states.Contains("|" + value + "|", StringComparison.Ordinal)
                        ? value : null;
                }
            }
        }
        catch (AsnContentException) { /* Certificado sem Subject DN parseável. */ }
        return null;
    }

    private static Dictionary<string, string> ReadBrazilianOtherNames(X509Certificate2 cert)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var san = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == SubjectAlternativeNameOid);
        if (san is null) return result;
        try
        {
            var reader = new AsnReader(san.RawData, AsnEncodingRules.DER);
            var names = reader.ReadSequence();
            while (names.HasData)
            {
                var tag = names.PeekTag();
                if (!tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    names.ReadEncodedValue();
                    continue;
                }

                // GeneralName.otherName: [0] IMPLICIT OtherName
                var other = names.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                var oid = other.ReadObjectIdentifier();
                var value = other.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                if (!value.HasData) continue;

                string? decoded = null;
                if (value.PeekTag().HasSameClassAndValue(Asn1Tag.PrimitiveOctetString))
                    decoded = Encoding.Latin1.GetString(value.ReadOctetString());
                else if (value.PeekTag().HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.PrintableString)))
                    decoded = value.ReadCharacterString(UniversalTagNumber.PrintableString);
                else if (value.PeekTag().HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.UTF8String)))
                    decoded = value.ReadCharacterString(UniversalTagNumber.UTF8String);

                if (decoded is not null && (oid == CnpjOid || oid == LegalNameOid))
                    result[oid] = decoded.TrimEnd('\0').Trim();
            }
            names.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
        }
        catch (AsnContentException ex)
        {
            throw new InvalidDataException("Extensão SAN do certificado inválida.", ex);
        }
        return result;
    }

    private static bool ValidCnpj(string digits)
    {
        if (digits.Distinct().Count() == 1) return false;
        static int Digit(string s, int len)
        {
            var weight = len - 7;
            var sum = 0;
            for (var i = 0; i < len; i++)
            {
                sum += (s[i] - '0') * weight;
                weight = weight == 2 ? 9 : weight - 1;
            }
            var mod = sum % 11;
            return mod < 2 ? 0 : 11 - mod;
        }
        return digits[12] - '0' == Digit(digits, 12) &&
               digits[13] - '0' == Digit(digits, 13);
    }
}
