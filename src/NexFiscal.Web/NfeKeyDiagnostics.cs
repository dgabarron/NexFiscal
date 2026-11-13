using System.Data.Common;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// Diagnóstico somente leitura de NF-e conhecidas: compara pasta de XMLs e envelopes
/// recebidos no HANA. Nunca chama a SEFAZ nem altera checkpoint/certificado.
/// </summary>
public sealed class NfeKeyDiagnostics(HanaSetup hana, CaptureScheduleSettings schedules)
{
    public sealed record Request(string CompanyId, string[] Keys);
    public sealed record Item(string Key, string Status, string Description,
        bool CompleteInHana, bool SummaryInHana, bool FilePresent,
        bool? FileMatchesHana, string[] Schemas);
    public sealed record Result(int Checked, int EnvelopesInspected, bool ScanComplete,
        string FolderStatus, Item[] Documents);

    public async Task<Result> CheckAsync(Request request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.CompanyId, out var id))
            throw new ArgumentException("Empresa inválida.");
        var keys = request.Keys?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (keys.Length is < 1 or > 50 || keys.Any(k => !IsKeyValid(k)))
            throw new ArgumentException("Informe de 1 a 50 chaves de acesso NF-e válidas, com 44 dígitos.");

        var directory = (await schedules.GetAsync(id.ToString("D"), ct))
            .FirstOrDefault(s => s.DocumentType == "NFE")?.DestinationDirectory;
        var folderReady = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory);
        var folderStatus = folderReady ? "Pasta configurada e acessível"
            : "Pasta de XMLs não configurada ou indisponível";
        var detected = keys.ToDictionary(k => k, k => new Found(k), StringComparer.Ordinal);

        // Consulta arquivos por nome fiscal; não procura arbitrariamente fora da pasta configurada.
        if (folderReady)
            foreach (var match in detected.Values)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(directory!, match.Key + ".xml");
                if (!File.Exists(path)) continue;
                match.FilePresent = true;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, ct);
                    match.FileValid = bytes.Length <= 10_000_000 &&
                        DocumentKeys(bytes).Contains(match.Key) &&
                        IsCompleteNfe(bytes);
                    match.FileHash = Convert.ToHexString(SHA256.HashData(bytes));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
                {
                    match.FileValid = false;
                }
            }

        var connectionString = hana.ConnectionString
            ?? throw new InvalidOperationException("Conexão HANA não configurada.");
        await using var connection = HanaSetup.Factory.CreateConnection()
            ?? throw new InvalidOperationException("Provider HANA não disponível.");
        connection.ConnectionString = connectionString;
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "SchemaName", "XmlBytes", "XmlSha256"
            FROM "BRAI_NEX"."BRAI_NEX_Envelope"
            WHERE "CompanyId"=? AND "DocumentType"='NFE'
            """;
        var companyParam = command.CreateParameter();
        companyParam.Value = id.ToString("D");
        command.Parameters.Add(companyParam);

        int inspected = 0;
        bool scanComplete = true;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (++inspected > 100_000)
            {
                scanComplete = false;
                break;
            }
            var schema = reader.GetString(0);
            // O provider HANA retorna o BLOB armazenado como byte[].
            if (reader.GetValue(1) is not byte[] xml || xml.Length is 0 or > 10_000_000)
                continue;
            HashSet<string> xmlKeys;
            try { xmlKeys = DocumentKeys(xml); }
            catch (XmlException) { continue; }
            foreach (var key in xmlKeys)
            {
                if (!detected.TryGetValue(key, out var found)) continue;
                found.Schemas.Add(schema);
                if (schema.StartsWith("procNFe", StringComparison.OrdinalIgnoreCase) &&
                    IsCompleteNfe(xml))
                {
                    found.CompleteInHana = true;
                    var hash = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    if (found.FileHash is not null)
                        found.FileMatchesHana = string.Equals(found.FileHash, hash,
                            StringComparison.OrdinalIgnoreCase);
                }
                if (schema.StartsWith("resNFe", StringComparison.OrdinalIgnoreCase))
                    found.SummaryInHana = true;
            }
        }

        var documents = detected.Values.Select(found =>
        {
            string status, message;
            if (found.FilePresent && !found.FileValid)
            {
                status = "INVALID_FILE";
                message = "Há um arquivo com essa chave, mas não foi confirmado como procNFe válido.";
            }
            else if (found.CompleteInHana && found.FilePresent && found.FileMatchesHana == false)
            {
                status = "FILE_DIVERGENT";
                message = "O arquivo da pasta difere do XML completo recebido no HANA. Não sobrescrever.";
            }
            else if (found.CompleteInHana && found.FileValid)
            {
                status = "COMPLETE";
                message = "XML completo encontrado no HANA e na pasta de destino.";
            }
            else if (found.CompleteInHana && !found.FilePresent)
            {
                status = "MISSING_FILE";
                message = "XML completo recebido no HANA, mas ausente da pasta. Requer reconciliação local.";
            }
            else if (found.FileValid)
            {
                status = "FILE_ONLY";
                message = "XML completo encontrado na pasta, sem registro completo correspondente no HANA.";
            }
            else if (found.SummaryInHana)
            {
                status = "SUMMARY_ONLY";
                message = "Somente resumo da NF-e localizado no HANA; XML completo não identificado.";
            }
            else if (!scanComplete || !folderReady)
            {
                status = "INCONCLUSIVE";
                message = "Verificação incompleta. Confira acesso à pasta e volume de registros.";
            }
            else if (found.Schemas.Count > 0)
            {
                status = "EVENT_ONLY";
                message = "Somente evento ou outro documento relacionado foi recebido.";
            }
            else
            {
                status = "NOT_FOUND";
                message = "Chave não identificada nos registros recebidos nem na pasta configurada.";
            }
            return new Item(found.Key, status, message, found.CompleteInHana,
                found.SummaryInHana, found.FilePresent, found.FileMatchesHana,
                found.Schemas.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }).ToArray();
        return new Result(documents.Length, inspected, scanComplete, folderStatus, documents);
    }

    private static bool IsKeyValid(string? value)
    {
        if (value is null || value.Length != 44 || !value.All(char.IsAsciiDigit) ||
            value.AsSpan(20, 2).ToString() != "55") return false;
        int sum = 0, weight = 2;
        for (int i = 42; i >= 0; i--)
        {
            sum += (value[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }
        var digit = (11 - sum % 11) % 11;
        return (digit >= 10 ? 0 : digit) == value[43] - '0';
    }

    private static bool IsCompleteNfe(byte[] xml)
    {
        using var input = new MemoryStream(xml, writable: false);
        using var reader = XmlReader.Create(input, SafeSettings());
        return XDocument.Load(reader).Root?.Name.LocalName == "nfeProc";
    }

    private static HashSet<string> DocumentKeys(byte[] xml)
    {
        using var input = new MemoryStream(xml, writable: false);
        using var reader = XmlReader.Create(input, SafeSettings());
        var root = XDocument.Load(reader).Root;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (root is null) return keys;
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName is "chNFe" &&
                IsKeyValid(element.Value.Trim()))
                keys.Add(element.Value.Trim());
            if (element.Name.LocalName == "infNFe")
            {
                var id = (string?)element.Attribute("Id");
                if (id?.StartsWith("NFe", StringComparison.Ordinal) == true &&
                    IsKeyValid(id[3..]))
                    keys.Add(id[3..]);
            }
        }
        return keys;
    }

    private static XmlReaderSettings SafeSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 10_000_000
    };

    private sealed class Found(string key)
    {
        public string Key { get; } = key;
        public bool FilePresent { get; set; }
        public bool FileValid { get; set; }
        public string? FileHash { get; set; }
        public bool? FileMatchesHana { get; set; }
        public bool CompleteInHana { get; set; }
        public bool SummaryInHana { get; set; }
        public HashSet<string> Schemas { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
