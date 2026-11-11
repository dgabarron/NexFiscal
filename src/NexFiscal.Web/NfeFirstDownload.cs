using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexFiscal.Capture;

/// <summary>
/// Uma consulta manual distNSU por clique, após confirmação de que o consumidor
/// anterior foi desligado. Inicia o cursor em 0 somente quando não há checkpoint.
/// Reserva 1h antes de acessar a SEFAZ para não repetir uma chamada ambígua.
/// Não é um agendador e não oferece NSU editável.
/// </summary>
public sealed class NfeFirstDownload(HanaSetup hana, CompanyCertificateVault vault, NfeXmlArchive archive, NfeIndividualXmlStore individualStore)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<object> ExecuteAsync(string companyId, bool previousConsumerStopped, string? destinationDirectory, CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out var guid))
            throw new ArgumentException("Empresa inválida.");
        if (!previousConsumerStopped)
            throw new InvalidOperationException("Confirme que o sistema anterior não consulta mais a SEFAZ para este CNPJ.");
        // Validar destino ANTES de reservar NSU ou fazer uma consulta fiscal.
        var validatedDestination = NfeIndividualXmlStore.ValidateDirectory(destinationDirectory);
        if (!await Gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Já existe uma consulta NF-e em andamento.");
        try
        {
            if (!vault.IsStored(companyId))
                throw new InvalidOperationException("Certificado A1 não encontrado.");
            var (pfx, password) = await vault.ReadAsync(companyId, ct);
            try
            {
                using var cert = new X509Certificate2(pfx, password,
                    OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
                if (!cert.HasPrivateKey || DateTime.UtcNow < cert.NotBefore.ToUniversalTime() ||
                    DateTime.UtcNow >= cert.NotAfter.ToUniversalTime())
                    throw new InvalidOperationException("A1 inválido ou fora da validade.");
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
                if (!chain.Build(cert))
                    throw new InvalidOperationException("Não foi possível validar a cadeia ICP-Brasil do A1.");

                var (cnpj, nsu) = await ReserveAsync(guid.ToString("D"), ct);
                var result = await new NfeDistributionClient().QueryAsync(cert, cnpj, nsu, 1, ct);
                if (result.Status is not (137 or 138 or 656))
                {
                    await RecordRejectionAsync(guid.ToString("D"), nsu, result, ct);
                    throw new InvalidOperationException(
                        "SEFAZ cStat " + result.Status + " — " +
                        (result.Reason.Length > 220 ? result.Reason[..220] : result.Reason) +
                        ". Nenhum XML foi gravado. Aguarde o intervalo de segurança antes de uma nova consulta.");
                }
                // O primeiro objetivo é entregar os arquivos recebidos.
                // Um erro de persistência HANA não pode apagar os XMLs já baixados.
                string? archiveId = null;
                string[] schemas = [];
                if (result.Status == 138)
                {
                    var saved = await archive.SaveAsync(guid.ToString("D"), result, ct);
                    archiveId = saved.Id;
                    schemas = saved.Schemas;
                }

                // Fluxo principal: um XML por arquivo na pasta X.
                // O ZIP de recuperação foi criado antes para não perder os
                // documentos caso a gravação da pasta selecionada falhe.
                var individual = result.Status == 138
                    ? await individualStore.SaveAsync(validatedDestination, result, ct)
                    : new NfeIndividualSaveResult(validatedDestination, 0, 0, []);

                var hanaSaved = false;
                string? persistenceWarning = null;
                try
                {
                    await new HanaNfeDistributionStore(HanaSetup.Factory,
                            hana.ConnectionString ?? throw new InvalidOperationException("HANA não configurado."))
                        .SaveBatchAsync(guid.ToString("D"), nsu, result, ct);
                    hanaSaved = true;
                }
                catch (Exception ex)
                {
                    // O ZIP já foi finalizado e pode ser baixado mesmo
                    // que o checkpoint não tenha avançado no HANA.
                    persistenceWarning = "XMLs disponíveis para download, mas gravação HANA não confirmada. " +
                        "Não reinicie o NSU nem repita a consulta antes de reconciliar o checkpoint.";
                    System.Diagnostics.Trace.TraceError(
                        "NexFiscal HANA após XML capturado: {0}", ex.GetType().Name);
                }
                return new
                {
                    status = result.Status,
                    reason = result.Reason,
                    downloaded = result.Documents.Count,
                    filesSaved = individual.Created,
                    filesAlreadyPresent = individual.AlreadyPresent,
                    destinationDirectory = validatedDestination,
                    filenames = individual.Filenames,
                    archiveUrl = archiveId is null ? null :
                        "/api/capture/archive/" + guid.ToString("D") + "/" + archiveId,
                    schemas,
                    hanaSaved,
                    persistenceWarning,
                    lastNsu = result.Status == 138 ? result.LastNsu : nsu,
                    maxNsu = result.MaxNsu,
                    nextAllowedUtc = result.Status is 137 or 656 || result.LastNsu == result.MaxNsu
                        ? DateTime.UtcNow.AddHours(1) : DateTime.UtcNow
                };
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        finally { Gate.Release(); }
    }

    // Registra cStat/xMotivo rejeitados para diagnóstico sem alterar o NSU.
    // A reserva de intervalo já foi persistida antes de enviar o SOAP.
    private async Task RecordRejectionAsync(
        string companyId, long requestedNsu, NfeDistributionResult response, CancellationToken ct)
    {
        var connectionString = hana.ConnectionString
            ?? throw new InvalidOperationException("HANA não configurado.");
        await using var conn = HanaSetup.Factory.CreateConnection()
            ?? throw new InvalidOperationException("Provider HANA indisponível.");
        conn.ConnectionString = connectionString;
        await conn.OpenAsync(ct);
        await using var cmd = Cmd(conn, null,
            "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_CaptureLog\" " +
            "(\"AttemptId\",\"CompanyId\",\"DocumentType\",\"RequestedNsu\",\"Status\",\"Result\",\"Message\",\"StartedAtUtc\",\"FinishedAtUtc\") " +
            "VALUES (?,?,'NFE',?,?,'REJEICAO_SEFAZ',?,CURRENT_UTCTIMESTAMP,CURRENT_UTCTIMESTAMP)",
            Guid.NewGuid().ToString("D"), companyId, requestedNsu, response.Status,
            response.Reason.Length > 1000 ? response.Reason[..1000] : response.Reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<(string Cnpj, long Nsu)> ReserveAsync(string companyId, CancellationToken ct)
    {
        var cs = hana.ConnectionString ?? throw new InvalidOperationException("HANA não configurado.");
        await using var conn = HanaSetup.Factory.CreateConnection()
            ?? throw new InvalidOperationException("Provider HANA indisponível.");
        conn.ConnectionString = cs;
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            string cnpj;
            await using (var cmd = Cmd(conn, tx,
                "SELECT \"Cnpj\",\"Environment\" FROM \"BRAI_NEX\".\"BRAI_NEX_Company\" WHERE \"CompanyId\"=? FOR UPDATE", companyId))
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw new InvalidOperationException("Empresa não cadastrada.");
                cnpj = reader.GetString(0);
                if (Convert.ToInt32(reader.GetValue(1)) != 1)
                    throw new InvalidOperationException("Empresa não está em produção.");
            }

            long nsu;
            DateTime? nextAllowed = null;
            var hasCursor = false;
            await using (var cmd = Cmd(conn, tx,
                "SELECT \"LastNsu\",\"NextAllowedAtUtc\" FROM \"BRAI_NEX\".\"BRAI_NEX_CaptureCursor\" WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE' FOR UPDATE", companyId))
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                hasCursor = await reader.ReadAsync(ct);
                nsu = hasCursor ? Convert.ToInt64(reader.GetValue(0)) : 0L;
                if (hasCursor && !reader.IsDBNull(1))
                    nextAllowed = Convert.ToDateTime(reader.GetValue(1));
            }
            if (nextAllowed > DateTime.UtcNow)
                throw new InvalidOperationException("Aguarde o intervalo obrigatório da SEFAZ antes da próxima consulta.");

            // Uma tentativa fica reservada por 1h ANTES de acessar a SEFAZ.
            // Se houver timeout ou resposta perdida, não repetimos imediatamente.
            if (!hasCursor)
            {
                await using var insert = Cmd(conn, tx,
                    "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_CaptureCursor\" " +
                    "(\"CompanyId\",\"DocumentType\",\"LastNsu\",\"NextAllowedAtUtc\",\"Version\",\"UpdatedAtUtc\") " +
                    "VALUES (?,'NFE',0,ADD_SECONDS(CURRENT_UTCTIMESTAMP,3600),0,CURRENT_UTCTIMESTAMP)", companyId);
                await insert.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var update = Cmd(conn, tx,
                    "UPDATE \"BRAI_NEX\".\"BRAI_NEX_CaptureCursor\" " +
                    "SET \"NextAllowedAtUtc\"=ADD_SECONDS(CURRENT_UTCTIMESTAMP,3600),\"Version\"=\"Version\"+1," +
                    "\"UpdatedAtUtc\"=CURRENT_UTCTIMESTAMP WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE'", companyId);
                await update.ExecuteNonQueryAsync(ct);
            }
            await using (var enable = Cmd(conn, tx,
                "UPDATE \"BRAI_NEX\".\"BRAI_NEX_Company\" SET \"Enabled\"=1 WHERE \"CompanyId\"=?", companyId))
                await enable.ExecuteNonQueryAsync(ct);

            await using (var log = Cmd(conn, tx,
                "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_CaptureLog\" " +
                "(\"AttemptId\",\"CompanyId\",\"DocumentType\",\"RequestedNsu\",\"Status\",\"Result\",\"Message\",\"StartedAtUtc\") " +
                "VALUES (?,?,'NFE',?,NULL,'CONSULTA_INICIADA','Consulta manual; consumidor anterior declarado desligado',CURRENT_UTCTIMESTAMP)",
                Guid.NewGuid().ToString("D"), companyId, nsu))
                await log.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
            return (cnpj, nsu);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbCommand Cmd(DbConnection conn, DbTransaction? tx, string sql, params object[] values)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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
