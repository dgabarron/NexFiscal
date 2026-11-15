using System.Data.Common;

namespace NexFiscal.Web;

/// <summary>Linha do inventário de NFS-e completas conhecidas da empresa.</summary>
public sealed record NfseDocumentRow(
    string AccessKey, long? Nsu, string Sha256, bool Cancelled, string? IssuerId, string? IssuerName, DateTime? IssuedAtUtc, decimal? TotalValue);

public sealed record NfseLot(
    IReadOnlyList<EnvelopeRow> Envelopes, IReadOnlyList<NfseDocumentRow> Documents, IReadOnlyList<string> CancelledKeys,
    long? LastNsu, DateTime? NextAllowedAtUtc);

/// <summary>Persistência da NFS-e. Os pedidos de captura usam a mesma tabela e a mesma fila da NF-e.</summary>
public interface INfseStore
{
    Task<CaptureCursor> GetCursorAsync(string companyId, CancellationToken ct);
    /// <summary>Envelopes, inventário e checkpoint na mesma transação. O NSU nunca retrocede.</summary>
    Task SaveLotAsync(string companyId, NfseLot lot, DateTime nowUtc, CancellationToken ct);
    Task LogAttemptAsync(string companyId, CaptureAttempt attempt, CancellationToken ct);
    Task<IReadOnlyList<NfseDocumentRow>> ListDocumentsAsync(string companyId, CancellationToken ct);
    Task<byte[]?> ReadEnvelopeAsync(string companyId, long nsu, CancellationToken ct);

    Task<MonitorRow?> GetMonitorAsync(string companyId, CancellationToken ct);
    Task SaveMonitorAsync(MonitorRow monitor, DateTime nowUtc, CancellationToken ct);
    Task SetMonitorScheduleAsync(string companyId, DateTime? nextRunAtUtc, DateTime? lastRunAtUtc, string? message, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<MonitorRow>> ListEnabledMonitorsAsync(CancellationToken ct);
}

/// <summary>Todo o SQL da NFS-e. Um teste confere cada identificador contra as migrações.</summary>
internal static class NfseSql
{
    private const string B = "\"BRAI_NEX\".";

    public const string SelectCursor =
        "SELECT \"LastNsu\", \"NextAllowedAtUtc\" FROM " + B + "\"BRAI_NEX_CaptureCursor\" WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFSE'";
    public const string CountCursor =
        "SELECT COUNT(*) FROM " + B + "\"BRAI_NEX_CaptureCursor\" WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFSE'";
    public const string InsertCursor =
        "INSERT INTO " + B + "\"BRAI_NEX_CaptureCursor\" (\"CompanyId\", \"DocumentType\", \"LastNsu\", \"NextAllowedAtUtc\", \"Version\", \"UpdatedAtUtc\") " +
        "VALUES (?, 'NFSE', ?, ?, 0, ?)";
    public const string AdvanceCursor =
        "UPDATE " + B + "\"BRAI_NEX_CaptureCursor\" SET \"LastNsu\" = ?, \"Version\" = \"Version\" + 1, \"UpdatedAtUtc\" = ? " +
        "WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFSE' AND \"LastNsu\" < ?";
    public const string SetNextAllowed =
        "UPDATE " + B + "\"BRAI_NEX_CaptureCursor\" SET \"NextAllowedAtUtc\" = ?, \"Version\" = \"Version\" + 1, \"UpdatedAtUtc\" = ? " +
        "WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFSE'";

    public const string CountEnvelope =
        "SELECT COUNT(*) FROM " + B + "\"BRAI_NEX_Envelope\" WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFSE' AND \"Nsu\" = ?";
    public const string InsertEnvelope =
        "INSERT INTO " + B + "\"BRAI_NEX_Envelope\" (\"CompanyId\", \"DocumentType\", \"Nsu\", \"SchemaName\", \"XmlSha256\", \"XmlBytes\", \"ReceivedAtUtc\") " +
        "VALUES (?, 'NFSE', ?, ?, ?, ?, ?)";
    public const string ReadEnvelope =
        "SELECT \"XmlBytes\" FROM " + B + "\"BRAI_NEX_Envelope\" WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFSE' AND \"Nsu\" = ?";
    public const string InsertLog =
        "INSERT INTO " + B + "\"BRAI_NEX_CaptureLog\" (\"AttemptId\", \"CompanyId\", \"DocumentType\", \"RequestedNsu\", \"Status\", \"Result\", " +
        "\"Message\", \"StartedAtUtc\", \"FinishedAtUtc\") VALUES (?, ?, 'NFSE', ?, ?, ?, ?, ?, ?)";

    public const string ListDocuments =
        "SELECT \"AccessKey\", \"Nsu\", \"Sha256\", \"Cancelled\", \"IssuerId\", \"IssuerName\", \"IssuedAtUtc\", \"TotalValue\" FROM " + B +
        "\"BRAI_NEX_NfseDocument\" WHERE \"CompanyId\" = ?";
    public const string CountDocument =
        "SELECT COUNT(*) FROM " + B + "\"BRAI_NEX_NfseDocument\" WHERE \"CompanyId\" = ? AND \"AccessKey\" = ?";
    public const string InsertDocument =
        "INSERT INTO " + B + "\"BRAI_NEX_NfseDocument\" (\"CompanyId\", \"AccessKey\", \"Nsu\", \"Sha256\", \"Cancelled\", \"IssuerId\", \"IssuerName\", " +
        "\"IssuedAtUtc\", \"TotalValue\", \"FirstSeenAtUtc\", \"UpdatedAtUtc\") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";
    public const string UpdateDocument =
        "UPDATE " + B + "\"BRAI_NEX_NfseDocument\" SET \"Nsu\" = ?, \"Sha256\" = ?, \"UpdatedAtUtc\" = ? WHERE \"CompanyId\" = ? AND \"AccessKey\" = ?";
    public const string SetDocumentCancelled =
        "UPDATE " + B + "\"BRAI_NEX_NfseDocument\" SET \"Cancelled\" = 1, \"UpdatedAtUtc\" = ? WHERE \"CompanyId\" = ? AND \"AccessKey\" = ?";

    private const string MonitorColumns =
        "\"CompanyId\", \"Enabled\", \"IntervalMinutes\", \"DestinationDirectory\", \"NextRunAtUtc\", \"LastRunAtUtc\", \"LastMessage\"";
    public const string SelectMonitor = "SELECT " + MonitorColumns + " FROM " + B + "\"BRAI_NEX_NfseMonitor\" WHERE \"CompanyId\" = ?";
    public const string EnabledMonitors = "SELECT " + MonitorColumns + " FROM " + B + "\"BRAI_NEX_NfseMonitor\" WHERE \"Enabled\" = 1 ORDER BY \"CompanyId\"";
    public const string CountMonitor = "SELECT COUNT(*) FROM " + B + "\"BRAI_NEX_NfseMonitor\" WHERE \"CompanyId\" = ?";
    public const string InsertMonitor =
        "INSERT INTO " + B + "\"BRAI_NEX_NfseMonitor\" (\"CompanyId\", \"Enabled\", \"IntervalMinutes\", \"DestinationDirectory\", " +
        "\"NextRunAtUtc\", \"LastRunAtUtc\", \"LastMessage\", \"UpdatedAtUtc\") VALUES (?, ?, ?, ?, ?, ?, ?, ?)";
    public const string UpdateMonitor =
        "UPDATE " + B + "\"BRAI_NEX_NfseMonitor\" SET \"Enabled\" = ?, \"IntervalMinutes\" = ?, \"DestinationDirectory\" = ?, " +
        "\"NextRunAtUtc\" = ?, \"LastRunAtUtc\" = ?, \"LastMessage\" = ?, \"UpdatedAtUtc\" = ? WHERE \"CompanyId\" = ?";
    public const string SetMonitorSchedule =
        "UPDATE " + B + "\"BRAI_NEX_NfseMonitor\" SET \"NextRunAtUtc\" = ?, \"LastRunAtUtc\" = ?, \"LastMessage\" = ?, \"UpdatedAtUtc\" = ? WHERE \"CompanyId\" = ?";
}

/// <summary>NFS-e no SAP HANA, usando a conexão salva na Função 1. Nunca apaga nem retrocede nada.</summary>
public sealed class HanaNfseStore(
    HanaCredentialStore credentialStore, IHanaConnectionFactory connections, ILogger<HanaNfseStore> logger)
    : INfseStore
{
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public Task<CaptureCursor> GetCursorAsync(string companyId, CancellationToken ct) => WithConnectionAsync(async db =>
    {
        await using var cmd = Command(db, null, NfseSql.SelectCursor, companyId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new CaptureCursor(Convert.ToInt64(reader.GetValue(0)), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc))
            : new CaptureCursor(0, Epoch);
    }, ct);

    public Task SaveLotAsync(string companyId, NfseLot lot, DateTime nowUtc, CancellationToken ct) => InTransactionAsync(async (db, tx) =>
    {
        foreach (var envelope in lot.Envelopes)
            if (await ScalarAsync(db, tx, ct, NfseSql.CountEnvelope, companyId, (decimal)envelope.Nsu) == 0)
                await ExecuteAsync(db, tx, ct, NfseSql.InsertEnvelope, companyId, (decimal)envelope.Nsu, envelope.Schema, envelope.Sha256, envelope.Xml, nowUtc);

        foreach (var document in lot.Documents)
            if (await ScalarAsync(db, tx, ct, NfseSql.CountDocument, companyId, document.AccessKey) == 0)
                await ExecuteAsync(db, tx, ct, NfseSql.InsertDocument, companyId, document.AccessKey, TypedNull.Dec(document.Nsu), document.Sha256,
                    document.Cancelled ? 1 : 0, document.IssuerId, document.IssuerName, TypedNull.Time(document.IssuedAtUtc),
                    TypedNull.Money(document.TotalValue), nowUtc, nowUtc);
            else
                await ExecuteAsync(db, tx, ct, NfseSql.UpdateDocument, TypedNull.Dec(document.Nsu), document.Sha256, nowUtc, companyId, document.AccessKey);

        foreach (var key in lot.CancelledKeys)
            await ExecuteAsync(db, tx, ct, NfseSql.SetDocumentCancelled, nowUtc, companyId, key);

        if (lot.LastNsu is null && lot.NextAllowedAtUtc is null) return true;
        if (await ScalarAsync(db, tx, ct, NfseSql.CountCursor, companyId) == 0)
            await ExecuteAsync(db, tx, ct, NfseSql.InsertCursor, companyId, 0m, Epoch, nowUtc);
        if (lot.LastNsu is { } last)
            await ExecuteAsync(db, tx, ct, NfseSql.AdvanceCursor, (decimal)last, nowUtc, companyId, (decimal)last);
        if (lot.NextAllowedAtUtc is { } next)
            await ExecuteAsync(db, tx, ct, NfseSql.SetNextAllowed, next, nowUtc, companyId);
        return true;
    }, ct);

    public Task LogAttemptAsync(string companyId, CaptureAttempt attempt, CancellationToken ct) => WithConnectionAsync(async db =>
    {
        await ExecuteAsync(db, null, ct, NfseSql.InsertLog, Guid.NewGuid().ToString(), companyId, (decimal)attempt.RequestedNsu,
            TypedNull.Int(attempt.Status), attempt.Result, Trim(attempt.Message, 2000), attempt.StartedAtUtc, attempt.FinishedAtUtc);
        return true;
    }, ct);

    public Task<IReadOnlyList<NfseDocumentRow>> ListDocumentsAsync(string companyId, CancellationToken ct) => WithConnectionAsync(async db =>
    {
        var list = new List<NfseDocumentRow>();
        await using var cmd = Command(db, null, NfseSql.ListDocuments, companyId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new NfseDocumentRow(reader.GetString(0), reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1)), reader.GetString(2),
                Convert.ToInt32(reader.GetValue(3)) == 1, Text(reader, 4), Text(reader, 5), Utc(reader, 6),
                reader.IsDBNull(7) ? null : Convert.ToDecimal(reader.GetValue(7))));
        return (IReadOnlyList<NfseDocumentRow>)list;
    }, ct);

    public Task<byte[]?> ReadEnvelopeAsync(string companyId, long nsu, CancellationToken ct) => WithConnectionAsync(async db =>
    {
        await using var cmd = Command(db, null, NfseSql.ReadEnvelope, companyId, (decimal)nsu);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }, ct);

    public Task<MonitorRow?> GetMonitorAsync(string companyId, CancellationToken ct) =>
        WithConnectionAsync(async db => (await ReadMonitorsAsync(db, ct, NfseSql.SelectMonitor, companyId)).FirstOrDefault(), ct);

    public Task<IReadOnlyList<MonitorRow>> ListEnabledMonitorsAsync(CancellationToken ct) =>
        WithConnectionAsync(db => ReadMonitorsAsync(db, ct, NfseSql.EnabledMonitors), ct);

    public Task SaveMonitorAsync(MonitorRow monitor, DateTime nowUtc, CancellationToken ct) => InTransactionAsync(async (db, tx) =>
    {
        var message = Trim(monitor.LastMessage, 500);
        if (await ScalarAsync(db, tx, ct, NfseSql.CountMonitor, monitor.CompanyId) == 0)
            await ExecuteAsync(db, tx, ct, NfseSql.InsertMonitor, monitor.CompanyId, monitor.Enabled ? 1 : 0, monitor.IntervalMinutes,
                monitor.DestinationDirectory, TypedNull.Time(monitor.NextRunAtUtc), TypedNull.Time(monitor.LastRunAtUtc), message, nowUtc);
        else
            await ExecuteAsync(db, tx, ct, NfseSql.UpdateMonitor, monitor.Enabled ? 1 : 0, monitor.IntervalMinutes,
                monitor.DestinationDirectory, TypedNull.Time(monitor.NextRunAtUtc), TypedNull.Time(monitor.LastRunAtUtc), message, nowUtc, monitor.CompanyId);
        return true;
    }, ct);

    public Task SetMonitorScheduleAsync(string companyId, DateTime? nextRunAtUtc, DateTime? lastRunAtUtc, string? message, DateTime nowUtc, CancellationToken ct) =>
        WithConnectionAsync(async db =>
        {
            await ExecuteAsync(db, null, ct, NfseSql.SetMonitorSchedule, TypedNull.Time(nextRunAtUtc), TypedNull.Time(lastRunAtUtc),
                Trim(message, 500), nowUtc, companyId);
            return true;
        }, ct);

    private static async Task<IReadOnlyList<MonitorRow>> ReadMonitorsAsync(DbConnection db, CancellationToken ct, string sql, params object?[] args)
    {
        var list = new List<MonitorRow>();
        await using var cmd = Command(db, null, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new MonitorRow(reader.GetString(0), Convert.ToInt32(reader.GetValue(1)) == 1, Convert.ToInt32(reader.GetValue(2)),
                reader.GetString(3), Utc(reader, 4), Utc(reader, 5), Text(reader, 6)));
        return list;
    }

    private async Task<T> WithConnectionAsync<T>(Func<DbConnection, Task<T>> work, CancellationToken ct)
    {
        var (state, credentials) = credentialStore.Read();
        if (state != StoredConfigState.Readable)
            throw new NexFiscalException("hana_not_configured", "Configure e valide a conexão SAP HANA antes de capturar NFS-e.", 409);
        try
        {
            await using var db = connections.Create(credentials!);
            await db.OpenAsync(ct);
            return await work(db);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or NexFiscalException))
        {
            var failure = HanaFailure.Translate(ex, credentials!, saving: false);
            logger.LogWarning("Operação de NFS-e no HANA falhou: {Type} / {Kind} / código {Code}",
                ex.GetType().Name, failure.Kind, HanaFailure.NativeCode(ex));
            throw failure;
        }
    }

    private Task<T> InTransactionAsync<T>(Func<DbConnection, DbTransaction, Task<T>> work, CancellationToken ct) =>
        WithConnectionAsync(async db =>
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            try
            {
                var result = await work(db, tx);
                await tx.CommitAsync(ct);
                return result;
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None); } catch { /* conexão já perdida */ }
                throw;
            }
        }, ct);

    private static DbCommand Command(DbConnection db, DbTransaction? tx, string sql, params object?[] args) =>
        HanaCompanyStore.Command(db, tx, sql, args);

    private static async Task ExecuteAsync(DbConnection db, DbTransaction? tx, CancellationToken ct, string sql, params object?[] args)
    {
        await using var cmd = Command(db, tx, sql, args);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ScalarAsync(DbConnection db, DbTransaction? tx, CancellationToken ct, string sql, params object?[] args)
    {
        await using var cmd = Command(db, tx, sql, args);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static string? Trim(string? text, int max) => text is not null && text.Length > max ? text[..max] : text;
    private static string? Text(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTime? Utc(DbDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTime.SpecifyKind(reader.GetDateTime(index), DateTimeKind.Utc);
}
