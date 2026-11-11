using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;

/// <summary>
/// Provisiona apenas objetos ausentes da migração inicial.
/// Não faz DROP, TRUNCATE, nem altera tabelas existentes.
/// A chamada é explícita no assistente de instalação e requer privilégios DDL.
/// </summary>
public sealed class HanaSchemaInstaller
{
    private static readonly SemaphoreSlim InstallationLock = new(1, 1);
    private const string Schema = "BRAI_NEX";
    private const string MigrationResource = "NexFiscal.Web.sql.migrations.001_BRAI_NEX_initial.sql";

    public async Task<HanaSetupResult> InstallAsync(DbConnection connection, CancellationToken ct)
    {
        await InstallationLock.WaitAsync(ct);
        try
        {
            var sql = await ReadSqlAsync(ct);
            var commands = Regex.Split(sql, @";\s*(?:\r?\n|$)")
                .Select(command => string.Join("\n", command.Split('\n')
                    .Where(line => !line.TrimStart().StartsWith("--"))).Trim())
                .Where(command => command.Length > 0)
                .ToList();

            if (!await SchemaExistsAsync(connection, ct))
                await ExecuteAsync(connection, "CREATE SCHEMA \"BRAI_NEX\"", ct);

            foreach (var statement in commands)
            {
                var match = Regex.Match(statement,
                    "^CREATE\\s+COLUMN\\s+TABLE\\s+\"BRAI_NEX\"\\.\"(?<table>BRAI_NEX_[A-Za-z]+)\"",
                    RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                var table = match.Groups["table"].Value;
                if (!await TableExistsAsync(connection, table, ct))
                    await ExecuteAsync(connection, statement, ct);
            }

            // A tabela de versão já pode existir em instalações anteriores.
            // Não inserir o mesmo registro duas vezes.
            await using (var version = connection.CreateCommand())
            {
                version.CommandText = "SELECT COUNT(*) FROM \"BRAI_NEX\".\"BRAI_NEX_SchemaVersion\" WHERE \"Version\"=1";
                if (Convert.ToInt32(await version.ExecuteScalarAsync(ct)) == 0)
                    await ExecuteAsync(connection,
                        "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_SchemaVersion\" (\"Version\",\"Name\",\"AppliedAtUtc\") " +
                        "VALUES (1,'initial_nfe_capture',CURRENT_UTCTIMESTAMP)", ct);
            }

            // Após criar objetos ausentes, validar também todas as colunas.
            // Tabelas preexistentes incompatíveis nunca são sobrescritas.
            return await HanaSchemaVerifier.VerifyAsync(connection, ct);
        }
        finally { InstallationLock.Release(); }
    }

    private static async Task<string> ReadSqlAsync(CancellationToken ct)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(MigrationResource)
            ?? throw new InvalidOperationException("Migração SQL integrada não encontrada.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task<bool> SchemaExistsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SYS.SCHEMAS WHERE SCHEMA_NAME='BRAI_NEX'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, string name, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SYS.TABLES WHERE SCHEMA_NAME='BRAI_NEX' AND TABLE_NAME=?";
        var p = cmd.CreateParameter();
        p.ParameterName = "name";
        p.Value = name;
        cmd.Parameters.Add(p);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<int> CountTablesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SYS.TABLES WHERE SCHEMA_NAME='BRAI_NEX' " +
            "AND TABLE_NAME IN ('BRAI_NEX_SchemaVersion','BRAI_NEX_Company','BRAI_NEX_CaptureCursor','BRAI_NEX_Envelope','BRAI_NEX_CaptureLog')";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
