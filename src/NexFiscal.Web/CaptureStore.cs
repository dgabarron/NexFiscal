using System.Data.Common;

namespace NexFiscal.Web;

/// <summary>Checkpoint oficial da distribuição por empresa (BRAI_NEX_CaptureCursor, DocumentType 'NFE').</summary>
public sealed record CaptureCursor(long LastNsu, DateTime NextAllowedAtUtc);

/// <summary>Envelope recebido da SEFAZ, guardado como evidência (inclusive resumos e eventos).</summary>
public sealed record EnvelopeRow(long Nsu, string Schema, string Sha256, byte[] Xml);

/// <summary>Linha do inventário de NF-e conhecidas da empresa.</summary>
public sealed record DocumentRow(
    string AccessKey, bool HasCompleteXml, long? CompleteNsu, string? CompleteSha256, long? SummaryNsu, int? Situation,
    string? IssuerId, string? IssuerName, DateTime? IssuedAtUtc, decimal? TotalValue);

/// <summary>Tudo o que um lote da SEFAZ altera no banco, gravado em uma única transação.</summary>
public sealed record LotPersistence(
    IReadOnlyList<EnvelopeRow> Envelopes, IReadOnlyList<DocumentRow> Documents, IReadOnlyList<string> CancelledKeys,
    IReadOnlyList<long> MissingNsus, long? LastNsu, DateTime? NextAllowedAtUtc);

public sealed record CaptureAttempt(long RequestedNsu, int? Status, string Result, string? Message, DateTime StartedAtUtc, DateTime FinishedAtUtc);

public sealed record CaptureJob(
    string JobId, string CompanyId, string Kind, string State, string DestinationDirectory, DateTime RequestedAtUtc,
    DateTime? StartedAtUtc = null, DateTime? FinishedAtUtc = null, DateTime? NextRunAtUtc = null, int Attempts = 0,
    int NewComplete = 0, int AlreadyPresent = 0, int Recovered = 0, int Ignored = 0, int Pending = 0, int Failures = 0,
    int? LastStatus = null, string? Message = null)
{
    public const string Queued = "queued", Running = "running", WaitingSefaz = "waiting_sefaz",
        Completed = "completed", CompletedWithPending = "completed_with_pending", Failed = "failed";

    public bool Active => State is Queued or Running or WaitingSefaz;
}

/// <summary>Persistência da captura. A implementação real é o HANA; os testes usam uma em memória.</summary>
public interface ICaptureStore
{
    Task<CaptureCursor> GetCursorAsync(string companyId, CancellationToken ct);
    /// <summary>Grava envelopes, inventário, lacunas e checkpoint na mesma transação. O NSU nunca retrocede.</summary>
    Task SaveLotAsync(string companyId, LotPersistence lot, DateTime nowUtc, CancellationToken ct);
    Task LogAttemptAsync(string companyId, CaptureAttempt attempt, CancellationToken ct);

    Task<IReadOnlyList<DocumentRow>> ListDocumentsAsync(string companyId, CancellationToken ct);
    Task<byte[]?> ReadEnvelopeAsync(string companyId, long nsu, CancellationToken ct);
    /// <summary>Envelopes procNFe já gravados (inclusive por versões antigas) que ainda não constam no inventário.</summary>
    Task<IReadOnlyList<EnvelopeRow>> ListUnindexedCompleteEnvelopesAsync(string companyId, long afterNsu, CancellationToken ct);

    Task<IReadOnlyList<long>> ListPendingRecoveryAsync(string companyId, int maxAttempts, int take, CancellationToken ct);
    Task<int> CountRecoveryAttemptsSinceAsync(string companyId, DateTime sinceUtc, CancellationToken ct);
    Task SaveRecoveryResultAsync(string companyId, long nsu, string state, int? status, DateTime nowUtc, CancellationToken ct);
    Task<int> CountOpenRecoveryAsync(string companyId, int maxAttempts, CancellationToken ct);

    Task InsertJobAsync(CaptureJob job, CancellationToken ct);
    Task UpdateJobAsync(CaptureJob job, CancellationToken ct);
    Task<IReadOnlyList<CaptureJob>> ListActiveJobsAsync(CancellationToken ct);
    Task<CaptureJob?> LatestJobAsync(string companyId, CancellationToken ct);

    Task<string?> GetDestinationAsync(string companyId, CancellationToken ct);
    Task SetDestinationAsync(string companyId, string directory, DateTime nowUtc, CancellationToken ct);
}

/// <summary>Todo o SQL da Função 3. Um teste confere cada identificador contra as migrações.</summary>
internal static class CaptureSql
{
    private const string B = "\"BRAI_NEX\".";

    public const string SelectCursor =
        "SELECT \"LastNsu\", \"NextAllowedAtUtc\" FROM " + B + "\"BRAI_NEX_CaptureCursor\" WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFE'";
    public const string CountCursor =
        "SELECT COUNT(*) FROM " + B + "\"BRAI_NEX_CaptureCursor\" WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFE'";
    public const string InsertCursor =
        "INSERT INTO " + B + "\"BRAI_NEX_CaptureCursor\" (\"CompanyId\", \"DocumentType\", \"LastNsu\", \"NextAllowedAtUtc\", \"Version\", \"UpdatedAtUtc\") " +
        "VALUES (?, 'NFE', ?, ?, 0, ?)";
    // "LastNsu" < ? garante que o checkpoint nunca retrocede.
    public const string AdvanceCursor =
        "UPDATE " + B + "\"BRAI_NEX_CaptureCursor\" SET \"LastNsu\" = ?, \"Version\" = \"Version\" + 1, \"UpdatedAtUtc\" = ? " +
        "WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFE' AND \"LastNsu\" < ?";
    public const string SetNextAllowed =
        "UPDATE " + B + "\"BRAI_NEX_CaptureCursor\" SET \"NextAllowedAtUtc\" = ?, \"Version\" = \"Version\" + 1, \"UpdatedAtUtc\" = ? " +
        "WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFE'";

    public const string CountEnvelope =
        "SELECT COUNT(*) FROM " + B + "\"BRAI_NEX_Envelope\" WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFE' AND \"Nsu\" = ?";
    public const string InsertEnvelope =
        "INSERT INTO " + B + "\"BRAI_NEX_Envelope\" (\"CompanyId\", \"DocumentType\", \"Nsu\", \"SchemaName\", \"XmlSha256\", \"XmlBytes\", \"ReceivedAtUtc\") " +
        "VALUES (?, 'NFE', ?, ?, ?, ?, ?)";
    public const string ReadEnvelope =
        "SELECT \"XmlBytes\" FROM " + B + "\"BRAI_NEX_Envelope\" WHERE \"CompanyId\" = ? AND \"DocumentType\" = 'NFE' AND \"Nsu\" = ?";
    public const string UnindexedCompleteEnvelopes =
        "SELECT TOP 50 e.\"Nsu\", e.\"SchemaName\", e.\"XmlSha256\", e.\"XmlBytes\" FROM " + B + "\"BRAI_NEX_Envelope\" e " +
        "WHERE e.\"CompanyId\" = ? AND e.\"DocumentType\" = 'NFE' AND e.\"Nsu\" > ? AND e.\"SchemaName\" LIKE 'procNFe%' " +
        "AND NOT EXISTS (SELECT 1 FROM " + B + "\"BRAI_NEX_NfeDocument\" d WHERE d.\"CompanyId\" = e.\"CompanyId\" AND d.\"CompleteNsu\" = e.\"Nsu\") " +
        "ORDER BY e.\"Nsu\"";

    public const string ListDocuments =
        "SELECT \"AccessKey\", \"HasCompleteXml\", \"CompleteNsu\", \"CompleteSha256\", \"SummaryNsu\", \"Situation\", " +
        "\"IssuerId\", \"IssuerName\", \"IssuedAtUtc\", \"TotalValue\" FROM " + B + "\"BRAI_NEX_NfeDocument\" WHERE \"CompanyId\" = ?";
    public const string SelectDocument =
        "SELECT \"HasCompleteXml\" FROM " + B + "\"BRAI_NEX_NfeDocument\" WHERE \"CompanyId\" = ? AND \"AccessKey\" = ?";
    public const string InsertDocument =
        "INSERT INTO " + B + "\"BRAI_NEX_NfeDocument\" (\"CompanyId\", \"AccessKey\", \"HasCompleteXml\", \"CompleteNsu\", \"CompleteSha256\", " +
        "\"SummaryNsu\", \"Situation\", \"IssuerId\", \"IssuerName\", \"IssuedAtUtc\", \"TotalValue\", \"FirstSeenAtUtc\", \"UpdatedAtUtc\") " +
        "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";
    public const string SetDocumentComplete =
        "UPDATE " + B + "\"BRAI_NEX_NfeDocument\" SET \"HasCompleteXml\" = 1, \"CompleteNsu\" = ?, \"CompleteSha256\" = ?, " +
        "\"IssuerId\" = ?, \"IssuerName\" = ?, \"IssuedAtUtc\" = ?, \"TotalValue\" = ?, \"UpdatedAtUtc\" = ? " +
        "WHERE \"CompanyId\" = ? AND \"AccessKey\" = ?";
    public const string SetDocumentSummary =
        "UPDATE " + B + "\"BRAI_NEX_NfeDocument\" SET \"SummaryNsu\" = ?, \"Situation\" = ?, \"UpdatedAtUtc\" = ? " +
        "WHERE \"CompanyId\" = ? AND \"AccessKey\" = ?";
    public const string SetDocumentCancelled =
        "UPDATE " + B + "\"BRAI_NEX_NfeDocument\" SET \"Situation\" = 3, \"UpdatedAtUtc\" = ? WHERE \"CompanyId\" = ? AND \"AccessKey\" = ?";

    public const string CountRecovery =
        "SELECT COUNT(*) FROM " + B + "\"BRAI_NEX_NfeRecovery\" WHERE \"CompanyId\" = ? AND \"Nsu\" = ?";
    public const string InsertRecovery =
        "INSERT INTO " + B + "\"BRAI_NEX_NfeRecovery\" (\"CompanyId\", \"Nsu\", \"State\", \"Attempts\", \"CreatedAtUtc\") VALUES (?, ?, 'pending', 0, ?)";
    public const string ListPendingRecovery =
        "SELECT TOP 20 \"Nsu\" FROM " + B + "\"BRAI_NEX_NfeRecovery\" WHERE \"CompanyId\" = ? AND \"State\" = 'pending' AND \"Attempts\" < ? ORDER BY \"Nsu\"";
    public const string CountOpenRecovery =
        "SELECT COUNT(*) FROM " + B + "\"BRAI_NEX_NfeRecovery\" WHERE \"CompanyId\" = ? AND \"State\" = 'pending' AND \"Attempts\" < ?";
    public const string CountRecoveryAttemptsSince =
        "SELECT COUNT(*) FROM " + B + "\"BRAI_NEX_NfeRecovery\" WHERE \"CompanyId\" = ? AND \"LastAttemptAtUtc\" >= ?";
    public const string SaveRecoveryResult =
        "UPDATE " + B + "\"BRAI_NEX_NfeRecovery\" SET \"State\" = ?, \"Attempts\" = \"Attempts\" + 1, \"LastStatus\" = ?, \"LastAttemptAtUtc\" = ? " +
        "WHERE \"CompanyId\" = ? AND \"Nsu\" = ?";

    public const string InsertLog =
        "INSERT INTO " + B + "\"BRAI_NEX_CaptureLog\" (\"AttemptId\", \"CompanyId\", \"DocumentType\", \"RequestedNsu\", \"Status\", \"Result\", " +
        "\"Message\", \"StartedAtUtc\", \"FinishedAtUtc\") VALUES (?, ?, 'NFE', ?, ?, ?, ?, ?, ?)";

    private const string JobColumns =
        "\"JobId\", \"CompanyId\", \"Kind\", \"State\", \"DestinationDirectory\", \"RequestedAtUtc\", \"StartedAtUtc\", \"FinishedAtUtc\", " +
        "\"NextRunAtUtc\", \"Attempts\", \"NewComplete\", \"AlreadyPresent\", \"Recovered\", \"Ignored\", \"Pending\", \"Failures\", \"LastStatus\", \"Message\"";
    public const string InsertJob =
        "INSERT INTO " + B + "\"BRAI_NEX_CaptureJob\" (" + JobColumns + ") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";
    public const string UpdateJob =
        "UPDATE " + B + "\"BRAI_NEX_CaptureJob\" SET \"State\" = ?, \"StartedAtUtc\" = ?, \"FinishedAtUtc\" = ?, \"NextRunAtUtc\" = ?, \"Attempts\" = ?, " +
        "\"NewComplete\" = ?, \"AlreadyPresent\" = ?, \"Recovered\" = ?, \"Ignored\" = ?, \"Pending\" = ?, \"Failures\" = ?, \"LastStatus\" = ?, \"Message\" = ? " +
        "WHERE \"JobId\" = ?";
    public const string ActiveJobs =
        "SELECT " + JobColumns + " FROM " + B + "\"BRAI_NEX_CaptureJob\" WHERE \"State\" IN ('queued', 'running', 'waiting_sefaz') ORDER BY \"RequestedAtUtc\"";
    public const string LatestJob =
        "SELECT TOP 1 " + JobColumns + " FROM " + B + "\"BRAI_NEX_CaptureJob\" WHERE \"CompanyId\" = ? ORDER BY \"RequestedAtUtc\" DESC";

    public const string SelectSettings =
        "SELECT \"DestinationDirectory\" FROM " + B + "\"BRAI_NEX_CaptureSettings\" WHERE \"CompanyId\" = ?";
    public const string InsertSettings =
        "INSERT INTO " + B + "\"BRAI_NEX_CaptureSettings\" (\"CompanyId\", \"DestinationDirectory\", \"UpdatedAtUtc\") VALUES (?, ?, ?)";
    public const string UpdateSettings =
        "UPDATE " + B + "\"BRAI_NEX_CaptureSettings\" SET \"DestinationDirectory\" = ?, \"UpdatedAtUtc\" = ? WHERE \"CompanyId\" = ?";
}

/// <summary>Captura no SAP HANA, usando a conexão salva na Função 1. Nunca apaga nem retrocede nada.</summary>
public sealed class HanaCaptureStore(
    HanaCredentialStore credentialStore, IHanaConnectionFactory connections, ILogger<HanaCaptureStore> logger)
    : ICaptureStore
{
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public Task<CaptureCursor> GetCursorAsync(string companyId, CancellationToken ct) =>
        WithConnectionAsync(db => ReadCursorAsync(db, null, companyId, ct), ct);

    private static async Task<CaptureCursor> ReadCursorAsync(DbConnection db, DbTransaction? tx, string companyId, CancellationToken ct)
    {
        await using var cmd = Command(db, tx, CaptureSql.SelectCursor, companyId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new CaptureCursor(Convert.ToInt64(reader.GetValue(0)), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc))
            : new CaptureCursor(0, Epoch);
    }

    public Task SaveLotAsync(string companyId, LotPersistence lot, DateTime nowUtc, CancellationToken ct) =>
        InTransactionAsync(async (db, tx) =>
        {
            foreach (var envelope in lot.Envelopes)
                if (await ScalarAsync(db, tx, ct, CaptureSql.CountEnvelope, companyId, (decimal)envelope.Nsu) == 0)
                    await ExecuteAsync(db, tx, ct, CaptureSql.InsertEnvelope, companyId, (decimal)envelope.Nsu, envelope.Schema,
                        envelope.Sha256, envelope.Xml, nowUtc);

            foreach (var document in lot.Documents)
            {
                bool exists, complete = false;
                await using (var cmd = Command(db, tx, CaptureSql.SelectDocument, companyId, document.AccessKey))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    exists = await reader.ReadAsync(ct);
                    if (exists) complete = Convert.ToInt32(reader.GetValue(0)) == 1;
                }
                if (!exists)
                    await ExecuteAsync(db, tx, ct, CaptureSql.InsertDocument, companyId, document.AccessKey,
                        document.HasCompleteXml ? 1 : 0, TypedNull.Dec(document.CompleteNsu), document.CompleteSha256,
                        TypedNull.Dec(document.SummaryNsu), TypedNull.Int(document.Situation), document.IssuerId, document.IssuerName,
                        TypedNull.Time(document.IssuedAtUtc), TypedNull.Money(document.TotalValue), nowUtc, nowUtc);
                else if (document.HasCompleteXml)
                    await ExecuteAsync(db, tx, ct, CaptureSql.SetDocumentComplete, TypedNull.Dec(document.CompleteNsu), document.CompleteSha256,
                        document.IssuerId, document.IssuerName, TypedNull.Time(document.IssuedAtUtc), TypedNull.Money(document.TotalValue), nowUtc, companyId, document.AccessKey);
                else if (!complete)
                    await ExecuteAsync(db, tx, ct, CaptureSql.SetDocumentSummary, TypedNull.Dec(document.SummaryNsu), TypedNull.Int(document.Situation),
                        nowUtc, companyId, document.AccessKey);
            }

            foreach (var key in lot.CancelledKeys)
                await ExecuteAsync(db, tx, ct, CaptureSql.SetDocumentCancelled, nowUtc, companyId, key);

            foreach (var nsu in lot.MissingNsus)
                if (await ScalarAsync(db, tx, ct, CaptureSql.CountRecovery, companyId, (decimal)nsu) == 0)
                    await ExecuteAsync(db, tx, ct, CaptureSql.InsertRecovery, companyId, (decimal)nsu, nowUtc);

            if (lot.LastNsu is null && lot.NextAllowedAtUtc is null) return true;
            if (await ScalarAsync(db, tx, ct, CaptureSql.CountCursor, companyId) == 0)
                await ExecuteAsync(db, tx, ct, CaptureSql.InsertCursor, companyId, 0m, Epoch, nowUtc);
            if (lot.LastNsu is { } last)
                await ExecuteAsync(db, tx, ct, CaptureSql.AdvanceCursor, (decimal)last, nowUtc, companyId, (decimal)last);
            if (lot.NextAllowedAtUtc is { } next)
                await ExecuteAsync(db, tx, ct, CaptureSql.SetNextAllowed, next, nowUtc, companyId);
            return true;
        }, ct);

    public Task LogAttemptAsync(string companyId, CaptureAttempt attempt, CancellationToken ct) =>
        WithConnectionAsync(async db =>
        {
            await ExecuteAsync(db, null, ct, CaptureSql.InsertLog, Guid.NewGuid().ToString(), companyId, (decimal)attempt.RequestedNsu,
                TypedNull.Int(attempt.Status), attempt.Result, Trim(attempt.Message), attempt.StartedAtUtc, attempt.FinishedAtUtc);
            return true;
        }, ct);

    public Task<IReadOnlyList<DocumentRow>> ListDocumentsAsync(string companyId, CancellationToken ct) =>
        WithConnectionAsync(async db =>
        {
            var list = new List<DocumentRow>();
            await using var cmd = Command(db, null, CaptureSql.ListDocuments, companyId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(new DocumentRow(reader.GetString(0), Convert.ToInt32(reader.GetValue(1)) == 1, Long(reader, 2), Text(reader, 3),
                    Long(reader, 4), reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)), Text(reader, 6), Text(reader, 7),
                    Utc(reader, 8), reader.IsDBNull(9) ? null : Convert.ToDecimal(reader.GetValue(9))));
            return (IReadOnlyList<DocumentRow>)list;
        }, ct);

    public Task<byte[]?> ReadEnvelopeAsync(string companyId, long nsu, CancellationToken ct) =>
        WithConnectionAsync(async db =>
        {
            await using var cmd = Command(db, null, CaptureSql.ReadEnvelope, companyId, (decimal)nsu);
            return await cmd.ExecuteScalarAsync(ct) as byte[];
        }, ct);

    public Task<IReadOnlyList<EnvelopeRow>> ListUnindexedCompleteEnvelopesAsync(string companyId, long afterNsu, CancellationToken ct) =>
        WithConnectionAsync(async db =>
        {
            var list = new List<EnvelopeRow>();
            await using var cmd = Command(db, null, CaptureSql.UnindexedCompleteEnvelopes, companyId, (decimal)afterNsu);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(new EnvelopeRow(Convert.ToInt64(reader.GetValue(0)), reader.GetString(1), reader.GetString(2), (byte[])reader.GetValue(3)));
            return (IReadOnlyList<EnvelopeRow>)list;
        }, ct);

    public Task<IReadOnlyList<long>> ListPendingRecoveryAsync(string companyId, int maxAttempts, int take, CancellationToken ct) =>
        WithConnectionAsync(async db =>
        {
            var list = new List<long>();
            await using var cmd = Command(db, null, CaptureSql.ListPendingRecovery, companyId, maxAttempts);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (list.Count < take && await reader.ReadAsync(ct)) list.Add(Convert.ToInt64(reader.GetValue(0)));
            return (IReadOnlyList<long>)list;
        }, ct);

    public Task<int> CountRecoveryAttemptsSinceAsync(string companyId, DateTime sinceUtc, CancellationToken ct) =>
        WithConnectionAsync(db => ScalarAsync(db, null, ct, CaptureSql.CountRecoveryAttemptsSince, companyId, sinceUtc), ct);

    public Task<int> CountOpenRecoveryAsync(string companyId, int maxAttempts, CancellationToken ct) =>
        WithConnectionAsync(db => ScalarAsync(db, null, ct, CaptureSql.CountOpenRecovery, companyId, maxAttempts), ct);

    public Task SaveRecoveryResultAsync(string companyId, long nsu, string state, int? status, DateTime nowUtc, CancellationToken ct) =>
        WithConnectionAsync(async db =>
        {
            await ExecuteAsync(db, null, ct, CaptureSql.SaveRecoveryResult, state, TypedNull.Int(status), nowUtc, companyId, (decimal)nsu);
            return true;
        }, ct);

    public Task InsertJobAsync(CaptureJob job, CancellationToken ct) => WithConnectionAsync(async db =>
    {
        await ExecuteAsync(db, null, ct, CaptureSql.InsertJob, job.JobId, job.CompanyId, job.Kind, job.State, job.DestinationDirectory,
            job.RequestedAtUtc, TypedNull.Time(job.StartedAtUtc), TypedNull.Time(job.FinishedAtUtc), TypedNull.Time(job.NextRunAtUtc), job.Attempts,
            job.NewComplete, job.AlreadyPresent, job.Recovered, job.Ignored, job.Pending, job.Failures, TypedNull.Int(job.LastStatus), Trim(job.Message));
        return true;
    }, ct);

    public Task UpdateJobAsync(CaptureJob job, CancellationToken ct) => WithConnectionAsync(async db =>
    {
        await ExecuteAsync(db, null, ct, CaptureSql.UpdateJob, job.State, TypedNull.Time(job.StartedAtUtc), TypedNull.Time(job.FinishedAtUtc), TypedNull.Time(job.NextRunAtUtc), job.Attempts,
            job.NewComplete, job.AlreadyPresent, job.Recovered, job.Ignored, job.Pending, job.Failures, TypedNull.Int(job.LastStatus), Trim(job.Message), job.JobId);
        return true;
    }, ct);

    public Task<IReadOnlyList<CaptureJob>> ListActiveJobsAsync(CancellationToken ct) =>
        WithConnectionAsync(db => ReadJobsAsync(db, ct, CaptureSql.ActiveJobs), ct);

    public Task<CaptureJob?> LatestJobAsync(string companyId, CancellationToken ct) =>
        WithConnectionAsync(async db => (await ReadJobsAsync(db, ct, CaptureSql.LatestJob, companyId)).FirstOrDefault(), ct);

    private static async Task<IReadOnlyList<CaptureJob>> ReadJobsAsync(DbConnection db, CancellationToken ct, string sql, params object?[] args)
    {
        var list = new List<CaptureJob>();
        await using var cmd = Command(db, null, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new CaptureJob(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                Utc(reader, 5)!.Value, Utc(reader, 6), Utc(reader, 7), Utc(reader, 8), Int(reader, 9), Int(reader, 10), Int(reader, 11),
                Int(reader, 12), Int(reader, 13), Int(reader, 14), Int(reader, 15),
                reader.IsDBNull(16) ? null : Convert.ToInt32(reader.GetValue(16)), Text(reader, 17)));
        return list;
    }

    public Task<string?> GetDestinationAsync(string companyId, CancellationToken ct) => WithConnectionAsync(async db =>
    {
        await using var cmd = Command(db, null, CaptureSql.SelectSettings, companyId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }, ct);

    public Task SetDestinationAsync(string companyId, string directory, DateTime nowUtc, CancellationToken ct) =>
        InTransactionAsync(async (db, tx) =>
        {
            await using var cmd = Command(db, tx, CaptureSql.SelectSettings, companyId);
            var current = await cmd.ExecuteScalarAsync(ct) as string;
            if (current is null) await ExecuteAsync(db, tx, ct, CaptureSql.InsertSettings, companyId, directory, nowUtc);
            else if (current != directory) await ExecuteAsync(db, tx, ct, CaptureSql.UpdateSettings, directory, nowUtc, companyId);
            return true;
        }, ct);

    // ---------- infraestrutura ----------

    private async Task<T> WithConnectionAsync<T>(Func<DbConnection, Task<T>> work, CancellationToken ct)
    {
        var (state, credentials) = credentialStore.Read();
        if (state != StoredConfigState.Readable)
            throw new NexFiscalException("hana_not_configured", "Configure e valide a conexão SAP HANA antes de capturar NF-e.", 409);
        try
        {
            await using var db = connections.Create(credentials!);
            await db.OpenAsync(ct);
            return await work(db);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or NexFiscalException))
        {
            var failure = HanaFailure.Translate(ex, credentials!, saving: false);
            logger.LogWarning("Operação de captura no HANA falhou: {Type} / {Kind} / código {Code}",
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

    private static string? Trim(string? message) => message is { Length: > 2000 } ? message[..2000] : message;
    private static string? Text(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static long? Long(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : Convert.ToInt64(reader.GetValue(index));
    private static int Int(DbDataReader reader, int index) => Convert.ToInt32(reader.GetValue(index));
    private static DateTime? Utc(DbDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTime.SpecifyKind(reader.GetDateTime(index), DateTimeKind.Utc);
}
