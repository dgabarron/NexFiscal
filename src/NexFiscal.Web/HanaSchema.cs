using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;

namespace NexFiscal.Web;

/// <summary>Retrato da estrutura BRAI_NEX visível para o usuário conectado.</summary>
public sealed record HanaSchemaReport(
    int TablesFound, IReadOnlyList<string> MissingTables, IReadOnlyList<string> Incompatibilities)
{
    public bool Ready => MissingTables.Count == 0 && Incompatibilities.Count == 0;

    /// <summary>ready | pending (só faltam tabelas, podem ser criadas) | incompatible (tabela existente divergente).</summary>
    public string State => Ready ? "ready" : Incompatibilities.Count > 0 ? "incompatible" : "pending";

    public IReadOnlyList<string> Issues =>
        [.. Incompatibilities, .. MissingTables.Select(table => "Tabela ausente: " + table)];
}

/// <summary>
/// Confere tabelas, colunas e tipos exigidos pelas migrações. Somente leitura do catálogo do HANA.
/// </summary>
public static class HanaSchemaVerifier
{
    public const string Schema = "BRAI_NEX";

    /// <summary>Tabela → (coluna, DATA_TYPE_NAME). Deve espelhar sql/migrations (há teste garantindo).</summary>
    public static readonly IReadOnlyDictionary<string, (string Column, string Type)[]> Required =
        new Dictionary<string, (string, string)[]>(StringComparer.Ordinal)
        {
            ["BRAI_NEX_SchemaVersion"] = [("Version", "INTEGER"), ("Name", "NVARCHAR"), ("AppliedAtUtc", "TIMESTAMP")],
            ["BRAI_NEX_Company"] = [("CompanyId", "NVARCHAR"), ("Cnpj", "NVARCHAR"), ("LegalName", "NVARCHAR"),
                ("Environment", "TINYINT"), ("Enabled", "TINYINT"), ("CreatedAtUtc", "TIMESTAMP")],
            ["BRAI_NEX_CaptureCursor"] = [("CompanyId", "NVARCHAR"), ("DocumentType", "NVARCHAR"), ("LastNsu", "DECIMAL"),
                ("NextAllowedAtUtc", "TIMESTAMP"), ("Version", "BIGINT"), ("UpdatedAtUtc", "TIMESTAMP")],
            ["BRAI_NEX_Envelope"] = [("CompanyId", "NVARCHAR"), ("DocumentType", "NVARCHAR"), ("Nsu", "DECIMAL"),
                ("SchemaName", "NVARCHAR"), ("XmlSha256", "NVARCHAR"), ("XmlBytes", "BLOB"), ("ReceivedAtUtc", "TIMESTAMP")],
            ["BRAI_NEX_CaptureLog"] = [("AttemptId", "NVARCHAR"), ("CompanyId", "NVARCHAR"), ("DocumentType", "NVARCHAR"),
                ("RequestedNsu", "DECIMAL"), ("Status", "INTEGER"), ("Result", "NVARCHAR"), ("Message", "NVARCHAR"),
                ("StartedAtUtc", "TIMESTAMP"), ("FinishedAtUtc", "TIMESTAMP")],
            ["BRAI_NEX_CompanyCertificate"] = [("CompanyId", "NVARCHAR"), ("Uf", "NVARCHAR"), ("Issuer", "NVARCHAR"),
                ("Thumbprint", "NVARCHAR"), ("NotBeforeUtc", "TIMESTAMP"), ("NotAfterUtc", "TIMESTAMP"),
                ("UpdatedAtUtc", "TIMESTAMP"), ("ArchivedAtUtc", "TIMESTAMP")]
        };

    public static async Task<HanaSchemaReport> VerifyAsync(DbConnection connection, CancellationToken ct)
    {
        var found = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT TABLE_NAME,COLUMN_NAME,DATA_TYPE_NAME FROM SYS.TABLE_COLUMNS WHERE SCHEMA_NAME='BRAI_NEX'";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                if (!Required.ContainsKey(table)) continue;
                if (!found.TryGetValue(table, out var columns))
                    found.Add(table, columns = new Dictionary<string, string>(StringComparer.Ordinal));
                columns[reader.GetString(1)] = reader.IsDBNull(2) ? "" : reader.GetString(2);
            }
        }

        var missingTables = new List<string>();
        var incompatibilities = new List<string>();
        foreach (var (table, required) in Required)
        {
            if (!found.TryGetValue(table, out var columns)) { missingTables.Add(table); continue; }
            foreach (var (column, type) in required)
            {
                if (!columns.TryGetValue(column, out var actual))
                    incompatibilities.Add($"Coluna ausente: {table}.{column}");
                else if (!string.Equals(actual, type, StringComparison.OrdinalIgnoreCase))
                    incompatibilities.Add($"Tipo incompatível: {table}.{column} (esperado {type}, encontrado {actual})");
            }
        }
        return new HanaSchemaReport(found.Count, missingTables, incompatibilities);
    }
}

/// <summary>
/// Cria somente o schema e as tabelas AUSENTES das migrações.
/// Emite apenas CREATE SCHEMA, CREATE COLUMN TABLE e INSERT de versão;
/// nunca DROP, TRUNCATE, ALTER, DELETE ou UPDATE. Tabelas existentes não são tocadas.
/// </summary>
public static class HanaSchemaInstaller
{
    /// <summary>Migrações na ordem de aplicação. Novas etapas só acrescentam itens aqui.</summary>
    public static readonly IReadOnlyList<(int Version, string Name, string Resource)> Migrations =
    [
        (1, "initial_nfe_capture", "NexFiscal.Web.sql.migrations.001_BRAI_NEX_initial.sql"),
        (2, "company_certificate", "NexFiscal.Web.sql.migrations.002_BRAI_NEX_company_certificate.sql")
    ];

    /// <summary>Tabela → comando CREATE COLUMN TABLE completo, na ordem das migrações (respeita chaves estrangeiras).</summary>
    public static IReadOnlyList<(string Table, string Sql)> CreateStatements { get; } = ParseMigrations();

    public static async Task<(HanaSchemaReport Report, IReadOnlyList<string> Created)> InstallAsync(
        DbConnection connection, CancellationToken ct)
    {
        var before = await HanaSchemaVerifier.VerifyAsync(connection, ct);
        // Tabela existente divergente: não criar nada e não tentar "consertar".
        if (before.Ready || before.Incompatibilities.Count > 0)
        {
            if (before.Ready) await EnsureVersionRowsAsync(connection, ct);
            return (before, []);
        }

        if (await ScalarAsync(connection, "SELECT COUNT(*) FROM SYS.SCHEMAS WHERE SCHEMA_NAME='BRAI_NEX'", ct) == 0)
            await ExecuteAsync(connection, "CREATE SCHEMA \"BRAI_NEX\"", ct);

        var created = new List<string>();
        foreach (var (table, sql) in CreateStatements)
        {
            if (!before.MissingTables.Contains(table)) continue;
            await ExecuteAsync(connection, sql, ct);
            created.Add(table);
        }

        var after = await HanaSchemaVerifier.VerifyAsync(connection, ct);
        if (after.Ready) await EnsureVersionRowsAsync(connection, ct);
        return (after, created);
    }

    private static async Task EnsureVersionRowsAsync(DbConnection connection, CancellationToken ct)
    {
        foreach (var (version, name, _) in Migrations)
        {
            if (await ScalarAsync(connection,
                    $"SELECT COUNT(*) FROM \"BRAI_NEX\".\"BRAI_NEX_SchemaVersion\" WHERE \"Version\"={version}", ct) == 0)
                await ExecuteAsync(connection,
                    "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_SchemaVersion\" (\"Version\",\"Name\",\"AppliedAtUtc\") " +
                    $"VALUES ({version},'{name}',CURRENT_UTCTIMESTAMP)", ct);
        }
    }

    private static IReadOnlyList<(string, string)> ParseMigrations()
    {
        var statements = new List<(string, string)>();
        foreach (var (_, _, resource) in Migrations)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Migração SQL integrada não encontrada: " + resource);
            using var reader = new StreamReader(stream);
            statements.AddRange(Regex.Matches(reader.ReadToEnd(),
                    "CREATE\\s+COLUMN\\s+TABLE\\s+\"BRAI_NEX\"\\.\"(?<table>BRAI_NEX_[A-Za-z]+)\"\\s*\\(.*?\\)\\s*(?=;)",
                    RegexOptions.Singleline)
                .Select(match => (match.Groups["table"].Value, match.Value.Trim())));
        }
        var expected = HanaSchemaVerifier.Required.Keys.ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(statements.Select(s => s.Item1)))
            throw new InvalidOperationException("As migrações não correspondem às tabelas exigidas pelo NexFiscal.");
        return statements;
    }

    private static async Task<int> ScalarAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
