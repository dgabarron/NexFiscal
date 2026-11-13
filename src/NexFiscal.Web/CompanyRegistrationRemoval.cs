using System.Data;
using System.Data.Common;
using System.Text.Json;

/// <summary>Remove apenas cadastro inicial sem dados fiscais; nunca apaga checkpoint ou XML.</summary>
public sealed class CompanyRegistrationRemoval(
    HanaSetup hana, CompanyCertificateVault vault, CaptureScheduleSettings schedules,
    CteFirstDownload cte, NfeSefazCatchup nfeJobs, CteSefazCatchup cteJobs,
    ILogger<CompanyRegistrationRemoval> logger)
{
    public sealed record Request(string CnpjConfirmation);

    public async Task RemoveAsync(string companyId, Request request, CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out var id))
            throw new ArgumentException("Empresa inválida.");
        var key = id.ToString("D");
        if (nfeJobs.Get(key)?.Status is "running" or "scheduled" ||
            cteJobs.Get(key)?.Status is "running" or "scheduled")
            throw new InvalidOperationException("Há uma captura em andamento ou agendada. Aguarde sua conclusão.");

        var configured = await schedules.GetAsync(key, ct);
        if (configured.Any(x => (x.DocumentType is "NFE" or "CTE") && x.Enabled))
            throw new InvalidOperationException("Desative e salve o monitoramento NF-e e CT-e antes de remover a empresa.");
        var cteState = await cte.StatusAsync(key, ct);
        using var cteJson = JsonDocument.Parse(JsonSerializer.Serialize(cteState));
        var cteRoot = cteJson.RootElement;
        if ((cteRoot.TryGetProperty("lastAttemptUtc", out var lastAttempt) && lastAttempt.ValueKind != JsonValueKind.Null) ||
            (cteRoot.TryGetProperty("lastNsu", out var lastNsu) && lastNsu.GetString() != "000000000000000"))
            throw new InvalidOperationException("Esta empresa possui histórico CT-e. A remoção está bloqueada para preservar o checkpoint fiscal.");

        var cs = hana.ConnectionString ?? throw new InvalidOperationException("HANA não configurado.");
        await using var connection = HanaSetup.Factory.CreateConnection()
            ?? throw new InvalidOperationException("Provider HANA não disponível.");
        connection.ConnectionString = cs;
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await using var find = Command(connection, tx,
                "SELECT \"Cnpj\" FROM \"BRAI_NEX\".\"BRAI_NEX_Company\" WHERE \"CompanyId\"=? FOR UPDATE", key);
            var cnpj = Convert.ToString(await find.ExecuteScalarAsync(ct));
            if (cnpj is null) throw new ArgumentException("Empresa não encontrada.");
            if (request.CnpjConfirmation != cnpj)
                throw new ArgumentException("Confirme exatamente o CNPJ da empresa para removê-la.");

            foreach (var table in new[] { "BRAI_NEX_CaptureCursor", "BRAI_NEX_Envelope", "BRAI_NEX_CaptureLog" })
            {
                await using var count = Command(connection, tx,
                    "SELECT COUNT(*) FROM \"BRAI_NEX\".\"" + table + "\" WHERE \"CompanyId\"=?", key);
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) > 0)
                    throw new InvalidOperationException(
                        "Empresa possui histórico fiscal no HANA. A remoção é bloqueada para preservar XMLs, logs e checkpoint.");
            }

            await using var delete = Command(connection, tx,
                "DELETE FROM \"BRAI_NEX\".\"BRAI_NEX_Company\" WHERE \"CompanyId\"=?", key);
            if (await delete.ExecuteNonQueryAsync(ct) != 1)
                throw new DBConcurrencyException("Cadastro alterado; atualize os dados.");
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        // Limpeza apenas dos arquivos privados de configuração e certificado deste ID.
        // Não toca em pastas de XML nem em checkpoint que possua histórico.
        try
        {
            vault.DeleteUnused(key);
            schedules.DeleteUnused(key);
        }
        catch (IOException ex)
        {
            logger.LogWarning("Cadastro removido, mas limpeza local pendente: {Type}", ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Cadastro removido, mas limpeza local pendente: {Type}", ex.GetType().Name);
        }
    }

    private static DbCommand Command(DbConnection connection, DbTransaction tx, string sql, object parameter)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.Value = parameter;
        cmd.Parameters.Add(p);
        return cmd;
    }
}
