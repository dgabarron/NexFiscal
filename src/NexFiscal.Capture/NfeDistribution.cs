using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NexFiscal.Capture;

public sealed record NfeEnvelope(long Nsu, string Schema, string Xml, string Sha256);
public sealed record NfeBatch(int Status, long LastNsu, long MaxNsu, IReadOnlyList<NfeEnvelope> Documents, string? Reason);
public sealed record NfeCheckpoint(string Cnpj, int Environment, long LastNsu, DateTimeOffset NextAllowedAt);

public static class NfeDistribution
{
    public const long MaxNsu = 999_999_999_999_999;

    public static NfeBatch Parse(string responseXml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50_000_000 };
        using var input = new StringReader(responseXml);
        using var reader = XmlReader.Create(input, settings);
        var xml = XDocument.Load(reader);
        var result = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "retDistDFeInt")
            ?? (xml.Root?.Name.LocalName == "retDistDFeInt" ? xml.Root : null)
            ?? throw new InvalidDataException("Resposta sem retDistDFeInt.");

        var status = RequiredInt(result, "cStat");
        var lastNsu = RequiredNsu(result, "ultNSU");
        var maxNsu = RequiredNsu(result, "maxNSU");
        if (lastNsu > maxNsu) throw new InvalidDataException("ultNSU maior que maxNSU.");

        var reason = result.Elements().FirstOrDefault(e => e.Name.LocalName == "xMotivo")?.Value;
        // A solicitação rejeitada não produz lote aproveitável.
        if (status is not (137 or 138))
            return new NfeBatch(status, lastNsu, maxNsu, Array.Empty<NfeEnvelope>(), reason);

        var documents = new List<NfeEnvelope>();
        foreach (var element in result.Descendants().Where(e => e.Name.LocalName == "docZip"))
        {
            var nsuText = (string?)element.Attribute("NSU") ?? throw new InvalidDataException("docZip sem NSU.");
            if (!long.TryParse(nsuText, out long nsu) || nsu < 0 || nsu > MaxNsu)
                throw new InvalidDataException("NSU do documento inválido.");
            var schema = (string?)element.Attribute("schema") ?? throw new InvalidDataException("docZip sem schema.");
            if (schema.Length is 0 or > 200 || schema.Contains('/') || schema.Contains('\\'))
                throw new InvalidDataException("Schema inválido.");
            byte[] compressed;
            try { compressed = Convert.FromBase64String(element.Value.Trim()); }
            catch (FormatException ex) { throw new InvalidDataException("docZip não é Base64.", ex); }
            if (compressed.Length > 15_000_000) throw new InvalidDataException("docZip comprimido excede o limite.");

            using var stream = new MemoryStream(compressed);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int bytes;
            while ((bytes = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + bytes > 20_000_000) throw new InvalidDataException("XML descompactado excede o limite.");
                output.Write(buffer, 0, bytes);
            }
            var raw = output.ToArray();
            var content = new UTF8Encoding(false, true).GetString(raw);
            ValidateXml(content);
            var hash = Convert.ToHexString(SHA256.HashData(raw));
            documents.Add(new NfeEnvelope(nsu, schema, content, hash));
        }

        if (documents.GroupBy(d => d.Nsu).Any(g => g.Count() > 1))
            throw new InvalidDataException("Lote com NSU duplicado.");
        if (documents.Any(d => d.Nsu > lastNsu))
            throw new InvalidDataException("Lote possui documento acima do ultNSU.");
        return new NfeBatch(status, lastNsu, maxNsu, documents, reason);
    }

    public static DateTimeOffset NextAllowed(DateTimeOffset now, NfeBatch batch) =>
        now.Add(batch.Status is 137 or 656 or 429 || batch.LastNsu >= batch.MaxNsu
            ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(3));

    public static NfeCheckpoint Advance(NfeCheckpoint previous, NfeBatch batch, DateTimeOffset now)
    {
        if (batch.Status is not (137 or 138))
            throw new InvalidOperationException("Não avançar NSU em resposta rejeitada.");
        if (batch.LastNsu < previous.LastNsu)
            throw new InvalidDataException("A resposta retrocedeu o NSU.");
        return previous with { LastNsu = batch.LastNsu, NextAllowedAt = NextAllowed(now, batch) };
    }

    private static int RequiredInt(XElement parent, string name) =>
        int.TryParse(parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value, out var v)
            ? v : throw new InvalidDataException(name + " inválido.");

    private static long RequiredNsu(XElement parent, string name)
    {
        var value = parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        if (!long.TryParse(value, out var nsu) || nsu < 0 || nsu > MaxNsu)
            throw new InvalidDataException(name + " inválido.");
        return nsu;
    }

    private static void ValidateXml(string value)
    {
        var config = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 };
        using var input = new StringReader(value);
        using var reader = XmlReader.Create(input, config);
        while (reader.Read()) { }
    }
}
