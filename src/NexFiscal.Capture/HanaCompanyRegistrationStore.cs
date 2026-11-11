using System.Data;
using System.Data.Common;

namespace NexFiscal.Capture;

/// <summary>
/// Salva cadastro e cursor no schema intermediário, sem ativar consultas.
/// A conta de aplicação precisa apenas de DML no schema BRAI_NEX.
/// </summary>
public sealed class HanaCompanyRegistrationStore(DbProviderFactory factory, string connectionString)
    : ICompanyRegistrationStore
{
    private const string S = "\"BRAI_NEX\".";

    public async Task<CompanyRegistration> RegisterPausedAsync(
        A1CompanyIdentity identity, int environment, CancellationToken ct
    {
        if (identity.Cnpj.Length != 14 || environment != 1)
            throw new ArgumentException("Dados de cadastro inválidos.");

        await using var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException("Provider HANA indisponível.");
        connection.ConnectionString = connectionString;
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            // Não sobrescrever um cadastro/cursor anterior. Isso evita perda
            // de NSU quando um A1 é substituído ou enviado novamente.
            await using (var query = Command(connection, tx,
                "SELECT \"CompanyId\" FROM " + S + "\"BRAI_NEX_Company\" WHERE \"Cnpj\"=? AND \"Environment\"=?",
                identity.Cnpj, environment))
            {
                var oldId = await query.ExecuteScalarAsync(ct);
                if (oldId is not null && oldId is not DBNull)
                    throw new InvalidOperationException("Empresa já cadastrada. Use o fluxo de renovação do certificado.");
            }

            var id = Guid.NewGuid().ToString("D");
            await using (var insert = Command(connection, tx,
                "INSERT INTO " + S + "\"BRAI_NEX_Company\" " +
                "(\"CompanyId\",\"Cnpj\",\"LegalName\",\"Environment\",\"Enabled\",\"CreatedAtUtc\") " +
                "VALUES (?,?,?,?,0,CURRENT_UTCTIMESTAMP)",
                id, identity.Cnpj, identity.LegalName, environment))
            {
                if (await insert.ExecuteNonQueryAsync(ct) != 1)
                    throw new DataException("Cadastro da empresa não foi gravado.");
            }

            // Não criar checkpoint fictício (NSU 0). A ausência de cursor
            // significa que a inicialização fiscal ainda não foi comprovada.
            // A empresa permanece Enabled=0; nenhuma consulta é autorizada.

            await tx.CommitAsync(ct);
            return new CompanyRegistration(
                id, identity.Cnpj, identity.LegalName, environment,
                identity.Thumbprint, identity.ValidUntil, false, "AGUARDANDO_INICIALIZACAO");
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbCommand Command(DbConnection connection, DbTransaction tx, string sql, params object[] values)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = "p" + i;
            param.Value = values[i];
            cmd.Parameters.Add(param);
        }
        return cmd;
    }
}
