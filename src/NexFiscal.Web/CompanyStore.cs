using System.Data.Common;

namespace NexFiscal.Web;

/// <summary>Empresa como está no HANA (BRAI_NEX_Company + BRAI_NEX_CompanyCertificate).</summary>
public sealed record CompanyRecord(
    string CompanyId, string Cnpj, string LegalName, int Environment, DateTime CreatedAtUtc,
    string? Uf, string? Issuer, string? Thumbprint, DateTime? NotBeforeUtc, DateTime? NotAfterUtc,
    DateTime? CertificateUpdatedAtUtc, DateTime? ArchivedAtUtc, bool HasFiscalHistory = false);

/// <summary>Persistência das empresas. A implementação real é o HANA; os testes usam uma em memória.</summary>
public interface ICompanyStore
{
    Task<IReadOnlyList<CompanyRecord>> ListAsync(CancellationToken ct);
    Task InsertAsync(CompanyRecord company, CancellationToken ct);
    /// <summary>Grava os dados do certificado e reativa a empresa. Não altera CNPJ, razão social, NSU ou histórico.</summary>
    Task SetCertificateAsync(string companyId, CertificateInfo certificate, DateTime nowUtc, CancellationToken ct);
    /// <summary>Exclui o cadastro. Recusa (retorna false) se houver histórico fiscal.</summary>
    Task<bool> DeleteIfNoHistoryAsync(string companyId, CancellationToken ct);
    Task ArchiveAsync(string companyId, DateTime nowUtc, CancellationToken ct);
}

/// <summary>Todo o SQL da Função 2. Um teste confere cada identificador contra as migrações.</summary>
internal static class CompanySql
{
    public const string List =
        "SELECT c.\"CompanyId\", c.\"Cnpj\", c.\"LegalName\", c.\"Environment\", c.\"CreatedAtUtc\", " +
        "k.\"Uf\", k.\"Issuer\", k.\"Thumbprint\", k.\"NotBeforeUtc\", k.\"NotAfterUtc\", k.\"UpdatedAtUtc\", k.\"ArchivedAtUtc\" " +
        "FROM \"BRAI_NEX\".\"BRAI_NEX_Company\" c " +
        "LEFT JOIN \"BRAI_NEX\".\"BRAI_NEX_CompanyCertificate\" k ON k.\"CompanyId\" = c.\"CompanyId\" " +
        "ORDER BY c.\"LegalName\", c.\"Cnpj\"";

    // Empresas que já têm cursor, envelope ou tentativa de captura: nunca são excluídas, só arquivadas.
    public const string HistoryCompanies =
        "SELECT \"CompanyId\" FROM \"BRAI_NEX\".\"BRAI_NEX_CaptureCursor\" " +
        "UNION SELECT \"CompanyId\" FROM \"BRAI_NEX\".\"BRAI_NEX_Envelope\" " +
        "UNION SELECT \"CompanyId\" FROM \"BRAI_NEX\".\"BRAI_NEX_CaptureLog\"";

    public const string InsertCompany =
        "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_Company\" " +
        "(\"CompanyId\", \"Cnpj\", \"LegalName\", \"Environment\", \"Enabled\", \"CreatedAtUtc\") VALUES (?, ?, ?, ?, 1, ?)";

    public const string CountCertificate =
        "SELECT COUNT(*) FROM \"BRAI_NEX\".\"BRAI_NEX_CompanyCertificate\" WHERE \"CompanyId\" = ?";

    public const string InsertCertificate =
        "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_CompanyCertificate\" " +
        "(\"CompanyId\", \"Uf\", \"Issuer\", \"Thumbprint\", \"NotBeforeUtc\", \"NotAfterUtc\", \"UpdatedAtUtc\", \"ArchivedAtUtc\") " +
        "VALUES (?, ?, ?, ?, ?, ?, ?, NULL)";

    public const string UpdateCertificate =
        "UPDATE \"BRAI_NEX\".\"BRAI_NEX_CompanyCertificate\" SET \"Uf\" = ?, \"Issuer\" = ?, \"Thumbprint\" = ?, " +
        "\"NotBeforeUtc\" = ?, \"NotAfterUtc\" = ?, \"UpdatedAtUtc\" = ?, \"ArchivedAtUtc\" = NULL WHERE \"CompanyId\" = ?";

    public const string InsertArchivedCertificate =
        "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_CompanyCertificate\" (\"CompanyId\", \"UpdatedAtUtc\", \"ArchivedAtUtc\") VALUES (?, ?, ?)";

    public const string ArchiveCertificate =
        "UPDATE \"BRAI_NEX\".\"BRAI_NEX_CompanyCertificate\" SET \"ArchivedAtUtc\" = ?, \"UpdatedAtUtc\" = ? WHERE \"CompanyId\" = ?";

    public const string SetCompanyEnabled =
        "UPDATE \"BRAI_NEX\".\"BRAI_NEX_Company\" SET \"Enabled\" = ? WHERE \"CompanyId\" = ?";

    public const string CountCursor = "SELECT COUNT(*) FROM \"BRAI_NEX\".\"BRAI_NEX_CaptureCursor\" WHERE \"CompanyId\" = ?";
    public const string CountEnvelope = "SELECT COUNT(*) FROM \"BRAI_NEX\".\"BRAI_NEX_Envelope\" WHERE \"CompanyId\" = ?";
    public const string CountLog = "SELECT COUNT(*) FROM \"BRAI_NEX\".\"BRAI_NEX_CaptureLog\" WHERE \"CompanyId\" = ?";

    // Únicos DELETE do sistema: só o cadastro de empresa SEM histórico fiscal, por CompanyId, após confirmação.
    public const string DeleteCertificate = "DELETE FROM \"BRAI_NEX\".\"BRAI_NEX_CompanyCertificate\" WHERE \"CompanyId\" = ?";
    public const string DeleteCompany = "DELETE FROM \"BRAI_NEX\".\"BRAI_NEX_Company\" WHERE \"CompanyId\" = ?";
}

/// <summary>Empresas no SAP HANA, usando a conexão salva na Função 1.</summary>
public sealed class HanaCompanyStore(
    HanaCredentialStore credentialStore, IHanaConnectionFactory connections, ILogger<HanaCompanyStore> logger)
    : ICompanyStore
{
    public Task<IReadOnlyList<CompanyRecord>> ListAsync(CancellationToken ct) => WithConnectionAsync(async db =>
    {
        var withHistory = new HashSet<string>(StringComparer.Ordinal);
        await using (var history = Command(db, null, CompanySql.HistoryCompanies))
        await using (var rows = await history.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct)) withHistory.Add(rows.GetString(0));

        var list = new List<CompanyRecord>();
        await using var cmd = Command(db, null, CompanySql.List);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            list.Add(new CompanyRecord(
                id, reader.GetString(1), reader.GetString(2), Convert.ToInt32(reader.GetValue(3)),
                Utc(reader, 4)!.Value, Text(reader, 5), Text(reader, 6), Text(reader, 7),
                Utc(reader, 8), Utc(reader, 9), Utc(reader, 10), Utc(reader, 11), withHistory.Contains(id)));
        }
        return (IReadOnlyList<CompanyRecord>)list;
    }, ct);

    public Task InsertAsync(CompanyRecord company, CancellationToken ct) => InTransactionAsync(async (db, tx) =>
    {
        await ExecuteAsync(db, tx, ct, CompanySql.InsertCompany,
            company.CompanyId, company.Cnpj, company.LegalName, company.Environment, company.CreatedAtUtc);
        await ExecuteAsync(db, tx, ct, CompanySql.InsertCertificate,
            company.CompanyId, company.Uf, company.Issuer, company.Thumbprint,
            company.NotBeforeUtc, company.NotAfterUtc, company.CertificateUpdatedAtUtc ?? company.CreatedAtUtc);
        return true;
    }, ct);

    public Task SetCertificateAsync(string companyId, CertificateInfo certificate, DateTime nowUtc, CancellationToken ct) =>
        InTransactionAsync(async (db, tx) =>
        {
            if (await ScalarAsync(db, tx, ct, CompanySql.CountCertificate, companyId) == 0)
                await ExecuteAsync(db, tx, ct, CompanySql.InsertCertificate, companyId, certificate.Uf, certificate.Issuer,
                    certificate.Thumbprint, certificate.NotBeforeUtc, certificate.NotAfterUtc, nowUtc);
            else
                await ExecuteAsync(db, tx, ct, CompanySql.UpdateCertificate, certificate.Uf, certificate.Issuer,
                    certificate.Thumbprint, certificate.NotBeforeUtc, certificate.NotAfterUtc, nowUtc, companyId);
            await ExecuteAsync(db, tx, ct, CompanySql.SetCompanyEnabled, 1, companyId);
            return true;
        }, ct);

    public Task<bool> DeleteIfNoHistoryAsync(string companyId, CancellationToken ct) => InTransactionAsync(async (db, tx) =>
    {
        // Reconfere dentro da mesma transação da exclusão.
        if (await HasHistoryAsync(db, tx, companyId, ct)) return false;
        await ExecuteAsync(db, tx, ct, CompanySql.DeleteCertificate, companyId);
        await ExecuteAsync(db, tx, ct, CompanySql.DeleteCompany, companyId);
        return true;
    }, ct);

    public Task ArchiveAsync(string companyId, DateTime nowUtc, CancellationToken ct) => InTransactionAsync(async (db, tx) =>
    {
        if (await ScalarAsync(db, tx, ct, CompanySql.CountCertificate, companyId) == 0)
            await ExecuteAsync(db, tx, ct, CompanySql.InsertArchivedCertificate, companyId, nowUtc, nowUtc);
        else
            await ExecuteAsync(db, tx, ct, CompanySql.ArchiveCertificate, nowUtc, nowUtc, companyId);
        await ExecuteAsync(db, tx, ct, CompanySql.SetCompanyEnabled, 0, companyId);
        return true;
    }, ct);

    private static async Task<bool> HasHistoryAsync(DbConnection db, DbTransaction? tx, string companyId, CancellationToken ct) =>
        await ScalarAsync(db, tx, ct, CompanySql.CountCursor, companyId) > 0 ||
        await ScalarAsync(db, tx, ct, CompanySql.CountEnvelope, companyId) > 0 ||
        await ScalarAsync(db, tx, ct, CompanySql.CountLog, companyId) > 0;

    // ---------- infraestrutura ----------

    private async Task<T> WithConnectionAsync<T>(Func<DbConnection, Task<T>> work, CancellationToken ct)
    {
        var (state, credentials) = credentialStore.Read();
        if (state != StoredConfigState.Readable)
            throw new NexFiscalException("hana_not_configured",
                "Configure e valide a conexão SAP HANA antes de usar o cadastro de empresas.", 409);
        try
        {
            await using var db = connections.Create(credentials!);
            await db.OpenAsync(ct);
            return await work(db);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or NexFiscalException))
        {
            var failure = HanaFailure.Translate(ex, credentials!, saving: false);
            logger.LogWarning("Operação de empresas no HANA falhou: {Type} / {Kind} / código {Code}",
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

    internal static DbCommand Command(DbConnection db, DbTransaction? tx, string sql, params object?[] args)
    {
        if (sql.Count(c => c == '?') != args.Length)
            throw new InvalidOperationException("Número de parâmetros não corresponde ao SQL.");
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var arg in args)
        {
            var parameter = cmd.CreateParameter();
            parameter.Value = arg ?? DBNull.Value;
            cmd.Parameters.Add(parameter);
        }
        return cmd;
    }

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

    private static string? Text(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static DateTime? Utc(DbDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTime.SpecifyKind(reader.GetDateTime(index), DateTimeKind.Utc);
}
