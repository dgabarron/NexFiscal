using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexFiscal.Capture;

/// <summary>
/// Recuperação pontual, sob demanda, de lacunas NSU identificadas no HANA.
/// Usa consNSU sem tocar no LastNsu do monitoramento. NÃO reinicia distNSU
/// nem promete recuperar documentos fora da janela de disponibilidade SEFAZ.
/// </summary>
public sealed class NfeRetroactiveRecovery(
    HanaSetup hana, CompanyCertificateVault vault,
    CaptureScheduleSettings schedules, NfeXmlArchive archive,
    NfeIndividualXmlStore files)
{
    public sealed record RecoveryResult(string State, long? RequestedNsu, int? SefazStatus,
        int XmlCreated, int AlreadyPresent, string Message);

    public async Task<RecoveryResult> RunOnceAsync(string companyId, CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out var guid))
            throw new ArgumentException("Empresa inválida.");
        var id = guid.ToString("D");
        var destination = await schedules.RequireDestinationAsync(id, "NFE", ct);
        // O gate é compartilhado com monitoramento/captura manual no processo.
        if (!await NfeFirstDownload.Gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Outra consulta NF-e já está em execução.");
        try
        {
            // Descoberta local: apenas lacunas entre NSUs efetivamente recebidos
            // para esta mesma empresa; NSUs ausentes podem não existir na SEFAZ.
            var candidate = await ReserveCandidateAsync(id, ct);
            if (candidate is null)
                return new("no-known-gaps", null, null, 0, 0,
                    "Não há lacunas de NSU identificadas nos documentos locais. " +
                    "Isso não comprova que todo o histórico da empresa está disponível ou capturado.");
            var (pfx, password) = await vault.ReadAsync(id, ct);
            try
            {
                using var certificate = new X509Certificate2(pfx, password,
                    OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
                if (!certificate.HasPrivateKey ||
                    DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
                    DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
                    throw new InvalidOperationException("Certificado A1 inválido.");
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
                if (!chain.Build(certificate))
                    throw new InvalidOperationException("Falha ao validar cadeia ICP-Brasil do A1.");

                var result = await new NfeDistributionClient()
                    .QuerySpecificNsuAsync(certificate, candidate.Value.Cnpj,
                        candidate.Value.Nsu, 1, ct);

                if (result.Status is not (137 or 138 or 656))
                {
                    await LogResultAsync(id, candidate.Value.Nsu, result.Status, "RETRO_REJEICAO", ct);
                    return new("rejected", candidate.Value.Nsu, result.Status, 0, 0,
                        "SEFAZ rejeitou a consulta específica. A próxima tentativa deve respeitar o intervalo reservado.");
                }
                if (result.Status == 656)
                {
                    await LogResultAsync(id, candidate.Value.Nsu, result.Status, "RETRO_BLOQUEADO", ct);
                    return new("paused", candidate.Value.Nsu, result.Status, 0, 0,
                        "SEFAZ 656: aguarde pelo menos uma hora antes de novas consultas.");
                }
                if (result.Status == 137)
                {
                    await LogResultAsync(id, candidate.Value.Nsu, result.Status, "RETRO_INDISPONIVEL", ct);
                    return new("not-available", candidate.Value.Nsu, result.Status, 0, 0,
                        "A SEFAZ não disponibilizou documento para este NSU.");
                }

                // Preserva primeiro o lote bruto, depois XML individual; em caso
                // de falha não declara sucesso nem modifica o cursor sequencial.
                await archive.SaveAsync(id, result, ct);
                var saved = await files.SaveAsync(destination, result, ct);
                await StoreEnvelopeAsync(id, result, ct);
                await LogResultAsync(id, candidate.Value.Nsu, result.Status, "RETRO_GRAVADO", ct);
                return new("completed", candidate.Value.Nsu, result.Status, saved.Created,
                    saved.AlreadyPresent,
                    "Consulta específica concluída. Checkpoint do monitoramento preservado. " +
                    "Resumos/eventos ficam registrados, mas não são apresentados como procNFe.");
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        finally { NfeFirstDownload.Gate.Release(); }
    }

    private async Task<(string Cnpj, long Nsu)?> ReserveCandidateAsync(string id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            string cnpj;
            await using (var company = Cmd(conn, tx,
                "SELECT \"Cnpj\",\"Environment\" FROM \"BRAI_NEX\".\"BRAI_NEX_Company\" WHERE \"CompanyId\"=? FOR UPDATE", id))
            await using (var reader = await company.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Empresa não cadastrada.");
                cnpj = reader.GetString(0);
                if (Convert.ToInt32(reader.GetValue(1)) != 1)
                    throw new InvalidOperationException("Empresa fora de produção.");
            }
            await using var cursor = Cmd(conn, tx,
                "SELECT \"NextAllowedAtUtc\" FROM \"BRAI_NEX\".\"BRAI_NEX_CaptureCursor\" " +
                "WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE' FOR UPDATE", id);
            var next = await cursor.ExecuteScalarAsync(ct);
            if (next is null or DBNull)
                throw new InvalidOperationException("Checkpoint NF-e não inicializado.");
            if (Convert.ToDateTime(next) > DateTime.UtcNow)
                throw new InvalidOperationException("Aguarde a liberação da janela de consulta SEFAZ.");

            var known = new List<long>();
            await using (var query = Cmd(conn, tx,
                "SELECT \"Nsu\" FROM \"BRAI_NEX\".\"BRAI_NEX_Envelope\" " +
                "WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE' ORDER BY \"Nsu\"", id))
            await using (var reader = await query.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    known.Add(Convert.ToInt64(reader.GetValue(0)));

            var attempted = new HashSet<long>();
            await using (var query = Cmd(conn, tx,
                "SELECT \"RequestedNsu\" FROM \"BRAI_NEX\".\"BRAI_NEX_CaptureLog\" " +
                "WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE' AND \"Result\" LIKE 'RETRO_%'", id))
            await using (var reader = await query.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    attempted.Add(Convert.ToInt64(reader.GetValue(0)));

            long? missing = null;
            // Nunca gera consultas para NSUs anteriores ao primeiro envelope conhecido
            // nem tenta sondar ilimitadamente a sequência.
            for (var i = 1; i < known.Count && missing is null; i++)
            {
                for (var nsu = known[i - 1] + 1;
                     nsu < known[i] && nsu - known[i - 1] <= 20000; nsu++)
                    if (!attempted.Contains(nsu)) { missing = nsu; break; }
            }
            if (missing is null)
            {
                await tx.CommitAsync(ct);
                return null;
            }

            // Reserva janela ANTES de enviar SOAP, sem modificar LastNsu.
            await using (var reserve = Cmd(conn, tx,
                "UPDATE \"BRAI_NEX\".\"BRAI_NEX_CaptureCursor\" " +
                "SET \"NextAllowedAtUtc\"=ADD_SECONDS(CURRENT_UTCTIMESTAMP,3600)," +
                "\"Version\"=\"Version\"+1,\"UpdatedAtUtc\"=CURRENT_UTCTIMESTAMP " +
                "WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE'", id))
                await reserve.ExecuteNonQueryAsync(ct);
            await using (var log = Cmd(conn, tx,
                "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_CaptureLog\" " +
                "(\"AttemptId\",\"CompanyId\",\"DocumentType\",\"RequestedNsu\",\"Status\",\"Result\",\"Message\",\"StartedAtUtc\") " +
                "VALUES (?,?,'NFE',?,NULL,'RETRO_INICIADO','Consulta pontual consNSU',CURRENT_UTCTIMESTAMP)",
                Guid.NewGuid().ToString("D"), id, missing.Value))
                await log.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return (cnpj, missing.Value);
        }
        catch { await tx.RollbackAsync(CancellationToken.None); throw; }
    }

    private async Task StoreEnvelopeAsync(string id, NfeDistributionResult result, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            foreach (var doc in result.Documents)
            {
                await using var lookup = Cmd(conn, tx,
                    "SELECT \"XmlSha256\" FROM \"BRAI_NEX\".\"BRAI_NEX_Envelope\" " +
                    "WHERE \"CompanyId\"=? AND \"DocumentType\"='NFE' AND \"Nsu\"=?", id, doc.Nsu);
                var hash = await lookup.ExecuteScalarAsync(ct);
                if (hash is not null and not DBNull)
                {
                    if (!string.Equals(Convert.ToString(hash), doc.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("NSU recuperado possui hash diferente do já armazenado.");
                    continue;
                }
                await using var insert = Cmd(conn, tx,
                    "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_Envelope\" " +
                    "(\"CompanyId\",\"DocumentType\",\"Nsu\",\"SchemaName\",\"XmlSha256\",\"XmlBytes\",\"ReceivedAtUtc\") " +
                    "VALUES (?,'NFE',?,?,?,?,CURRENT_UTCTIMESTAMP)",
                    id, doc.Nsu, doc.Schema, doc.Sha256, doc.XmlBytes);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(CancellationToken.None); throw; }
    }

    private async Task LogResultAsync(string id, long nsu, int status, string outcome, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var log = Cmd(conn, null,
            "INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_CaptureLog\" " +
            "(\"AttemptId\",\"CompanyId\",\"DocumentType\",\"RequestedNsu\",\"Status\",\"Result\",\"Message\",\"StartedAtUtc\",\"FinishedAtUtc\") " +
            "VALUES (?,?,'NFE',?,?,?,'Recuperação pontual',CURRENT_UTCTIMESTAMP,CURRENT_UTCTIMESTAMP)",
            Guid.NewGuid().ToString("D"), id, nsu, status, outcome);
        await log.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = HanaSetup.Factory.CreateConnection()
            ?? throw new InvalidOperationException("Provider HANA indisponível.");
        conn.ConnectionString = hana.ConnectionString
            ?? throw new InvalidOperationException("HANA não configurado.");
        await conn.OpenAsync(ct);
        return conn;
    }

    private static DbCommand Cmd(DbConnection connection, DbTransaction? tx, string sql, params object[] args)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var value in args)
        {
            var p = cmd.CreateParameter();
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}
