using System.Data;
using System.Data.Common;

namespace NexFiscal.Capture;

/// <summary>
/// HANA adapter via ADO.NET provider factory. The host injects a configured,
/// supported SAP HANA DbProviderFactory and connection string. No SAP B1 schema access.
/// One instance/lease per CNPJ+environment+document type is required.
/// </summary>
public sealed class HanaNfeCaptureStore(DbProviderFactory factory, string connectionString) : INfeCaptureStore
{
    private const string S = "\"BRAI_NEX\".";

    public async Task<NfeCheckpoint> LoadAsync(string cnpj, int environment, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Make(conn, null,
            "SELECT C.\"Cnpj\", C.\"Environment\", N.\"LastNsu\", N.\"NextAllowedAtUtc\" " +
            "FROM " + S + "\"BRAI_NEX_CaptureCursor\" N " +
            "JOIN " + S + "\"BRAI_NEX_Company\" C ON C.\"CompanyId\"=N.\"CompanyId\" " +
            "WHERE C.\"Cnpj\"=? AND C.\"Environment\"=? AND N.\"DocumentType\"='NFE' AND C.\"Enabled\"=1",
            P("Cnpj", cnpj), P("Environment", environment));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Empresa NF-e habilitada ou cursor não encontrado.");
        return new NfeCheckpoint(reader.GetString(0), Convert.ToInt32(reader.GetValue(1)),
            Convert.ToInt64(reader.GetValue(2)), Utc(reader.GetDateTime(3)));
    }

    public async Task ReserveNextAttemptAsync(NfeCheckpoint expected, DateTimeOffset nextAllowedAt, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Make(conn, null,
            "UPDATE " + S + "\"BRAI_NEX_CaptureCursor\" SET \"NextAllowedAtUtc\"=?, \"Version\"=\"Version\"+1, \"UpdatedAtUtc\"=CURRENT_UTCTIMESTAMP " +
            "WHERE \"CompanyId\"=(SELECT \"CompanyId\" FROM " + S + "\"BRAI_NEX_Company\" WHERE \"Cnpj\"=? AND \"Environment\"=? AND \"Enabled\"=1) " +
            "AND \"DocumentType\"='NFE' AND \"LastNsu\"=? AND \"NextAllowedAtUtc\"=?",
            P("NextAllowedAt", nextAllowedAt.UtcDateTime), P("Cnpj", expected.Cnpj),
            P("Environment", expected.Environment), P("LastNsu", expected.LastNsu),
            P("ExpectedNextAllowed", expected.NextAllowedAt.UtcDateTime));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new DBConcurrencyException("Cursor modificado por outro processo.");
    }

    public async Task CommitAsync(NfeCheckpoint expected, NfeCheckpoint updated, IReadOnlyList<NfeEnvelope> documents, CancellationToken ct)
    {
        if (expected.Cnpj != updated.Cnpj || expected.Environment != updated.Environment ||
            updated.LastNsu < expected.LastNsu)
            throw new InvalidOperationException("Checkpoint incompatível.");
        await using var conn = await OpenAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);
        try
        {
            var companyId = await CompanyIdAsync(conn, transaction, expected.Cnpj, expected.Environment, ct);
            // Claim the reserved cursor first, preventing concurrent writers.
            await using (var update = Make(conn, transaction,
                "UPDATE " + S + "\"BRAI_NEX_CaptureCursor\" SET \"LastNsu\"=?, \"NextAllowedAtUtc\"=?, \"Version\"=\"Version\"+1, \"UpdatedAtUtc\"=CURRENT_UTCTIMESTAMP " +
                "WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE' AND \"LastNsu\"=? AND \"NextAllowedAtUtc\"=?",
                P("NewLastNsu", updated.LastNsu), P("NewNextAllowed", updated.NextAllowedAt.UtcDateTime),
                P("CompanyId", companyId), P("OldLastNsu", expected.LastNsu), P("ReservedNextAllowed", expected.NextAllowedAt.UtcDateTime)))
            {
                if (await update.ExecuteNonQueryAsync(ct) != 1)
                    throw new DBConcurrencyException("Reserva não pertence mais a esta execução.");
            }

            foreach (var envelope in documents)
            {
                // Retries of an already imported NSU are idempotent, but differing hashes must fail.
                await using var check = Make(conn, transaction,
                    "SELECT \"XmlSha256\" FROM " + S + "\"BRAI_NEX_Envelope\" " +
                    "WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE' AND \"Nsu\"=?",
                    P("CompanyId", companyId), P("Nsu", envelope.Nsu));
                var existing = await check.ExecuteScalarAsync(ct);
                if (existing is not null && existing is not DBNull)
                {
                    if (!String.Equals(Convert.ToString(existing), envelope.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("NSU já existe com hash XML diferente.");
                    continue;
                }
                await using var insert = Make(conn, transaction,
                    "INSERT INTO " + S + "\"BRAI_NEX_Envelope\" " +
                    "(\"CompanyId\",\"DocumentType\",\"Nsu\",\"SchemaName\",\"XmlSha256\",\"XmlBytes\",\"ReceivedAtUtc\") " +
                    "VALUES (?,'NFE',?,?,?,?,CURRENT_UTCTIMESTAMP)",
                    P("CompanyId", companyId), P("Nsu", envelope.Nsu), P("Schema", envelope.Schema),
                    P("Hash", envelope.Sha256), P("Xml", envelope.RawXml));
                if (await insert.ExecuteNonQueryAsync(ct) != 1)
                    throw new DataException("Falha ao gravar envelope.");
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<string> CompanyIdAsync(DbConnection conn, DbTransaction transaction, string cnpj, int environment, CancellationToken ct)
    {
        await using var cmd = Make(conn, transaction,
            "SELECT \"CompanyId\" FROM " + S + "\"BRAI_NEX_Company\" WHERE \"Cnpj\"=? AND \"Environment\"=? AND \"Enabled\"=1",
            P("Cnpj", cnpj), P("Environment", environment));
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))
            ?? throw new InvalidOperationException("Empresa não encontrada.");
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = factory.CreateConnection() ?? throw new InvalidOperationException("Provider HANA não criou conexão.");
        connection.ConnectionString = connectionString;
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static DbCommand Make(DbConnection conn, DbTransaction? tx, string sql, params (string name, object value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value;
            cmd.Parameters.Add(param);
        }
        return cmd;
    }

    private static (string name, object value) P(string name, object value) => (name, value);

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
