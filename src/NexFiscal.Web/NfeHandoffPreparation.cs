using System.Data;
using System.Data.Common;
using System.Globalization;

/// <summary>
/// Registra o último NSU comprovado do consumidor anterior sem disparar SEFAZ
/// nem habilitar a empresa. O checkpoint é criado uma única vez, com auditoria.
/// A liberação da primeira consulta exige uma etapa de autorização separada.
/// </summary>
public sealed class NfeHandoffPreparation(HanaSetup hana, CompanyCertificateVault vault)
{
    public sealed record Request(
        string CompanyId, string LastNsu, string SourceSystem,
        string LastSuccessfulQueryUtc, string ConfirmCnpj,
        bool PreviousConsumerStopped, bool CheckpointVerified);

    public async Task<object> PrepareAsync(Request input, CancellationToken ct)
    {
        if (input is null || !Guid.TryParse(input.CompanyId, out var id))
            throw new ArgumentException("Empresa inválida.");
        if (input.LastNsu is null || input.LastNsu.Length != 15 ||
            !input.LastNsu.All(char.IsAsciiDigit) ||
            !long.TryParse(input.LastNsu, NumberStyles.None, CultureInfo.InvariantCulture, out var nsu) ||
            nsu is <= 0 or > 999_999_999_999_999L)
            throw new ArgumentException("Informe o último NSU comprovado com exatamente 15 dígitos; NSU zero não é aceito.");
        if (string.IsNullOrWhiteSpace(input.SourceSystem) || input.SourceSystem.Trim().Length is < 3 or > 120)
            throw new ArgumentException("Identifique o sistema anterior.");
        if (!DateTimeOffset.TryParse(input.LastSuccessfulQueryUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var lastQuery) ||
            lastQuery > DateTimeOffset.UtcNow || lastQuery < DateTimeOffset.UtcNow.AddDays(-90))
            throw new ArgumentException("Informe a data da última consulta confirmada, dentro dos últimos 90 dias.");
        if (!input.PreviousConsumerStopped || !input.CheckpointVerified)
            throw new ArgumentException("É necessário confirmar a parada do consumidor anterior e a conferência do NSU.");
        if (!vault.IsStored(id.ToString("D")))
            throw new InvalidOperationException("O certificado A1 protegido não foi localizado.");

        var cs = hana.ConnectionString ?? throw new InvalidOperationException("SAP HANA não configurado.");
        await using var connection = HanaSetup.Factory.CreateConnection()
            ?? throw new InvalidOperationException("Provider SAP HANA indisponível.");
        connection.ConnectionString = cs;
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // O bloqueio impede duas preparações concorrentes e confirma o CNPJ
            // no servidor antes de criar o checkpoint.
            await using (var company = Cmd(connection, tx,
                "SELECT \"Cnpj\",\"Environment\",\"Enabled\" FROM \"BRAI_NEX\".\"BRAI_NEX_Company\" " +
                "WHERE \"CompanyId\"=? FOR UPDATE", id.ToString("D")))
            {
                await using var reader = await company.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw new ArgumentException("Empresa não cadastrada.");
                var cnpj = reader.GetString(0);
                var environment = Convert.ToInt32(reader.GetValue(1));
                var enabled = Convert.ToInt32(reader.GetValue(2)) != 0;
                if (input.ConfirmCnpj != cnpj)
                    throw new ArgumentException("O CNPJ confirmado não corresponde ao cadastro.");
                if (environment != 1 || enabled)
                    throw new InvalidOperationException("Somente empresas de produção pausadas podem preparar o checkpoint.");
            }

            // Não sobrescreve cursor existente, nem para correções, evitando retrocesso NSU.
            await using (var existing = Cmd(connection, tx,
                "SELECT COUNT(*) FROM \"BRAI_NEX\".\"BRAI_NEX_CaptureCursor\" " +
                "WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE'", id.ToString("D")))
            {
                if (Convert.ToInt32(await existing.ExecuteScalarAsync(ct)) != 0)
                    throw new InvalidOperationException("Checkpoint já preparado. Não será sobrescrito.");
            }

            // Período conservador até a primeira tentativa autorizada.
            // O valor nunca liga automaticamente a captura.
            await using (var insert = Cmd(connection, tx,
                "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_CaptureCursor\" " +
                "(\"CompanyId\",\"DocumentType\",\"LastNsu\",\"NextAllowedAtUtc\",\"Version\",\"UpdatedAtUtc\") " +
                "VALUES (?,'NFE',?,CURRENT_UTCTIMESTAMP,0,CURRENT_UTCTIMESTAMP)",
                id.ToString("D"), nsu))
            {
                if (await insert.ExecuteNonQueryAsync(ct) != 1)
                    throw new DataException("Não foi possível preparar o checkpoint.");
            }

            // Auditoria sem nomes de operadores, dados do certificado ou segredos.
            var message = "Sistema anterior: " + input.SourceSystem.Trim() +
                "; última consulta UTC: " + lastQuery.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) +
                "; parada do consumidor e origem do NSU declaradas.";
            await using (var audit = Cmd(connection, tx,
                "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_CaptureLog\" " +
                "(\"AttemptId\",\"CompanyId\",\"DocumentType\",\"RequestedNsu\",\"Status\",\"Result\",\"Message\",\"StartedAtUtc\",\"FinishedAtUtc\") " +
                "VALUES (?,?,'NFE',?,NULL,'HANDOFF_PREPARED',?,CURRENT_UTCTIMESTAMP,CURRENT_UTCTIMESTAMP)",
                Guid.NewGuid().ToString("D"), id.ToString("D"), nsu, message))
            {
                if (await audit.ExecuteNonQueryAsync(ct) != 1)
                    throw new DataException("Não foi possível registrar a auditoria do checkpoint.");
            }

            await tx.CommitAsync(ct);
            return new
            {
                prepared = true, captureEnabled = false,
                lastNsu = input.LastNsu,
                message = "Checkpoint registrado no HANA com auditoria. Empresa permanece pausada; nenhuma consulta SEFAZ foi realizada."
            };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbCommand Cmd(DbConnection connection, DbTransaction transaction, string sql, params object[] values)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var value in values)
        {
            var p = cmd.CreateParameter();
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}
