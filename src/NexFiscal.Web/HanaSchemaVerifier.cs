using System.Data.Common;

/// <summary>
/// Verifica colunas indispensáveis, além da simples existência de tabelas.
/// Nunca considera uma estrutura parcialmente incompatível como pronta.
/// </summary>
public static class HanaSchemaVerifier
{
    public static readonly IReadOnlyDictionary<string, string[]> RequiredColumns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["BRAI_NEX_SchemaVersion"] = ["Version", "Name", "AppliedAtUtc"],
            ["BRAI_NEX_Company"] = ["CompanyId", "Cnpj", "LegalName", "Environment", "Enabled", "CreatedAtUtc"],
            ["BRAI_NEX_CaptureCursor"] = ["CompanyId", "DocumentType", "LastNsu", "NextAllowedAtUtc", "Version", "UpdatedAtUtc"],
            ["BRAI_NEX_Envelope"] = ["CompanyId", "DocumentType", "Nsu", "SchemaName", "XmlSha256", "XmlBytes", "ReceivedAtUtc"],
            ["BRAI_NEX_CaptureLog"] = ["AttemptId", "CompanyId", "DocumentType", "RequestedNsu", "Status", "Result", "Message", "StartedAtUtc", "FinishedAtUtc"]
        };

    public static async Task<HanaSetupResult> VerifyAsync(DbConnection connection, CancellationToken ct)
    {
        var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME,COLUMN_NAME FROM SYS.TABLE_COLUMNS WHERE SCHEMA_NAME='BRAI_NEX'";
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                if (!RequiredColumns.ContainsKey(table)) continue;
                if (!found.TryGetValue(table, out var columns))
                    found.Add(table, columns = new HashSet<string>(StringComparer.Ordinal));
                columns.Add(reader.GetString(1));
            }
        }

        var validTables = found.Count;
        var missing = RequiredColumns
            .SelectMany(t => t.Value.Where(column => !found.TryGetValue(t.Key, out var columns) || !columns.Contains(column))
            .Select(column => t.Key + "." + column))
            .ToArray();
        var ok = validTables == RequiredColumns.Count && missing.Length == 0;
        var resultMessage = ok ? "Schema BRAI_NEX validado (tabelas e colunas)." :
            missing.Length > 0 ? "Estrutura incompleta: tabelas ou colunas ausentes. Instalação/atualização necessária." :
            "Estrutura incompleta.";
        return new HanaSetupResult(true, ok, validTables, resultMessage);
    }
}
