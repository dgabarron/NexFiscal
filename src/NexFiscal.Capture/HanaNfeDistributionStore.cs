using System.Data;
using System.Data.Common;

namespace NexFiscal.Capture;

/// <summary>
/// Persiste uma resposta de distribuição num único commit HANA.
/// Requer cursor NF-e previamente inicializado e autorizado.
/// NÃO cria NSU zero, não inicializa empresas e não chama a SEFAZ.
/// </summary>
public sealed class HanaNfeDistributionStore(DbProviderFactory factory, string connectionString)
{
    private const string Prefix = "\"BRAI_NEX\".";

    public async Task<int> SaveBatchAsync(
        string companyId, long requestedNsu, NfeDistributionResult result,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(companyId, out _) || requestedNsu is < 0 or > 999_999_999_999_999L)
            throw new ArgumentException("Empresa ou NSU inválidos.");
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status is not (137 or 138 or 656))
            throw new InvalidOperationException("Status não reconhecido: nenhum checkpoint será alterado.");
        if (result.LastNsu < requestedNsu && result.Status == 138)
            throw new InvalidDataException("A SEFAZ retornou NSU inferior ao solicitado.");

        await using var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException("Provider HANA indisponível.");
        connection.ConnectionString = connectionString;
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            // FOR UPDATE serializa operações simultâneas sobre o mesmo checkpoint.
            // Sem cursor anterior = sem migração/posse comprovada do consumidor.
            await using var cursor = Command(connection, tx,
                "SELECT \"LastNsu\",\"NextAllowedAtUtc\" FROM " + Prefix +
                "\"BRAI_NEX_CaptureCursor\" WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE' FOR UPDATE",
                companyId);
            await using var reader = await cursor.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Checkpoint NF-e não inicializado: captura bloqueada.");
            var storedNsu = Convert.ToInt64(reader.GetValue(0));
            var nextAllowed = Convert.ToDateTime(reader.GetValue(1));
            await reader.DisposeAsync();

            if (storedNsu != requestedNsu)
                throw new DBConcurrencyException("NSU alterado por outro processo: lote descartado.");
            if (nextAllowed > DateTime.UtcNow)
                throw new InvalidOperationException("Aguardando intervalo autorizado para consulta.");

            var added = 0;
            if (result.Status == 138)
            {
                if (result.Documents.Count == 0 || result.Documents.Any(d => d.Nsu <= requestedNsu || d.Nsu > result.LastNsu))
                    throw new InvalidDataException("Lote SEFAZ inválido ou fora da janela NSU.");

                foreach (var doc in result.Documents)
                {
                    await using var lookup = Command(connection, tx,
                        "SELECT \"XmlSha256\" FROM " + Prefix + "\"BRAI_NEX_Envelope\" " +
                        "WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE' AND \"Nsu\"=?",
                        companyId, doc.Nsu);
                    var existing = await lookup.ExecuteScalarAsync(cancellationToken);
                    if (existing is not null && existing is not DBNull)
                    {
                        if (!string.Equals(Convert.ToString(existing), doc.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("NSU já armazenado com XML divergente.");
                        continue;
                    }
                    await using var insert = Command(connection, tx,
                        "INSERT INTO " + Prefix + "\"BRAI_NEX_Envelope\" " +
                        "(\"CompanyId\",\"DocumentType\",\"Nsu\",\"SchemaName\",\"XmlSha256\",\"XmlBytes\",\"ReceivedAtUtc\") " +
                        "VALUES (?,'NFE',?,?,?,?,CURRENT_UTCTIMESTAMP)",
                        companyId, doc.Nsu, doc.Schema, doc.Sha256, doc.XmlBytes);
                    if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
                        throw new DataException("Falha ao gravar documento NF-e.");
                    added++;
                }
            }

            // 137/656 mantêm NSU e aplicam cooldown. No 138, avança somente
            // depois de gravar todos os XMLs, na mesma transação.
            var lastNsu = result.Status == 138 ? result.LastNsu : storedNsu;
            var cooldown = result.Status is 137 or 656
                ? DateTime.UtcNow.AddHours(1)
                : DateTime.UtcNow;
            await using var update = Command(connection, tx,
                "UPDATE " + Prefix + "\"BRAI_NEX_CaptureCursor\" SET " +
                "\"LastNsu\"=?,\"NextAllowedAtUtc\"=?,\"Version\"=\"Version\"+1,\"UpdatedAtUtc\"=CURRENT_UTCTIMESTAMP " +
                "WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE' AND \"LastNsu\"=?",
                lastNsu, cooldown, companyId, requestedNsu);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new DBConcurrencyException("Checkpoint mudou durante a gravação.");
            await tx.CommitAsync(cancellationToken);
            return added;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbCommand Command(DbConnection connection, DbTransaction tx, string sql, params object[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var value in values)
        {
            var parameter = command.CreateParameter();
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
