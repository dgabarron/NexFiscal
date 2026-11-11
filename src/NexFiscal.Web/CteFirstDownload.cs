using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NexFiscal.Capture;

/// <summary>
/// Primeiro fluxo CT-e: mesmo certificado A1 e pasta X da NF-e, mas SOAP e
/// checkpoint independentes. Estado durável local não requer SAP HANA.
/// </summary>
public sealed class CteFirstDownload(
    CompanyCertificateVault vault, IWebHostEnvironment host,
    NfeXmlArchive archive, NfeIndividualXmlStore store)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string Root => Path.Combine(host.ContentRootPath, ".nexfiscal-cte");

    public sealed record Cursor(
        long LastNsu, DateTime NextAllowedAtUtc,
        DateTime? LastAttemptUtc = null, int? LastStatus = null, string? LastReason = null);

    public async Task<object> StatusAsync(string companyId, CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out var id))
            throw new ArgumentException("Empresa inválida.");
        var cursor = await ReadAsync(id, ct);
        return new
        {
            companyId = id.ToString("D"),
            lastNsu = cursor.LastNsu.ToString("D15"),
            nextAllowedUtc = cursor.NextAllowedAtUtc,
            lastAttemptUtc = cursor.LastAttemptUtc,
            lastStatus = cursor.LastStatus,
            lastReason = cursor.LastReason,
            certificateStored = vault.IsStored(id.ToString("D"))
        };
    }

    public async Task<object> ExecuteAsync(
        string companyId, bool previousConsumerStopped, string? destinationDirectory,
        CancellationToken ct)
    {
        if (!Guid.TryParse(companyId, out var id))
            throw new ArgumentException("Empresa inválida.");
        if (!previousConsumerStopped)
            throw new InvalidOperationException("Confirme que outro consumidor CT-e não está ativo para este CNPJ.");
        var folder = NfeIndividualXmlStore.ValidateDirectory(destinationDirectory);
        if (!await Gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Já existe consulta CT-e em andamento.");
        try
        {
            var company = id.ToString("D");
            if (!vault.IsStored(company))
                throw new InvalidOperationException("Certificado A1 não encontrado.");
            var (pfx, password) = await vault.ReadAsync(company, ct);
            try
            {
                using var certificate = new X509Certificate2(pfx, password,
                    OperatingSystem.IsWindows()
                        ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
                if (!certificate.HasPrivateKey || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
                    DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
                    throw new InvalidOperationException("Certificado A1 inválido ou vencido.");

                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
                if (!chain.Build(certificate))
                    throw new InvalidOperationException("Não foi possível validar a cadeia ICP-Brasil do A1.");
                // Identidade fiscal vinculada ao A1, sem solicitar CNPJ novamente.
                var identity = A1CompanyCertificate.Inspect(pfx, password);
                var uf = A1CompanyCertificate.GetSubjectState(certificate);
                if (uf is null)
                    throw new InvalidOperationException("O A1 não informa UF no Subject; CT-e exige cUFAutor. Não enviar valores presumidos.");

                var cursor = await ReadAsync(id, ct);
                if (cursor.NextAllowedAtUtc > DateTime.UtcNow)
                    throw new InvalidOperationException("CT-e aguardando intervalo permitido até " +
                        cursor.NextAllowedAtUtc.ToString("dd/MM/yyyy HH:mm") + " UTC.");

                // Reserva antes do POST: após timeout não disparar consulta idêntica.
                var reserved = cursor with
                {
                    NextAllowedAtUtc = DateTime.UtcNow.AddHours(1),
                    LastAttemptUtc = DateTime.UtcNow, LastStatus = null,
                    LastReason = "Consulta CT-e iniciada"
                };
                await WriteAsync(id, reserved, ct);

                var response = await new CteDistributionClient().QueryAsync(
                    certificate, identity.Cnpj, uf, cursor.LastNsu, 1, ct);

                if (response.Status is not (137 or 138 or 656))
                {
                    await WriteAsync(id, reserved with
                    {
                        LastStatus = response.Status,
                        LastReason = response.Reason.Length > 255 ? response.Reason[..255] : response.Reason
                    }, ct);
                    return new
                    {
                        status = response.Status, reason = response.Reason,
                        downloaded = 0, filesSaved = 0, filesAlreadyPresent = 0,
                        skippedNonProcCTe = 0, saveSucceeded = false,
                        message = "SEFAZ rejeitou a consulta CT-e. Nenhum XML salvo.",
                        nextAllowedUtc = reserved.NextAllowedAtUtc
                    };
                }

                string? archiveId = null;
                if (response.Status == 138)
                {
                    // Armazena o lote bruto antes de filtrar procCTe; permite
                    // recuperação se houver erro de disco entre arquivos.
                    var savedArchive = await archive.SaveAsync(company, response, ct);
                    archiveId = savedArchive.Id;
                }

                NfeIndividualSaveResult individual;
                try
                {
                    individual = await store.SaveAsync(folder, response, ct, "CTE");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await WriteAsync(id, reserved with
                    {
                        LastStatus = response.Status,
                        LastReason = "CT-e recebido, mas cópia na pasta X não concluída"
                    }, ct);
                    return new
                    {
                        status = response.Status, reason = response.Reason,
                        downloaded = response.Documents.Count,
                        filesSaved = 0, filesAlreadyPresent = 0,
                        skippedNonProcCTe = 0, saveSucceeded = false,
                        message = "Falha ao salvar CT-e na pasta X. Use o ZIP de recuperação; não repita a consulta até reconciliar os arquivos.",
                        archiveUrl = archiveId is null ? null :
                            "/api/capture/archive/" + company + "/" + archiveId + "?kind=CTe",
                        nextAllowedUtc = reserved.NextAllowedAtUtc
                    };
                }

                // Avança o NSU apenas depois de preservar os documentos recebidos.
                var next = response.Status == 138 ? response.LastNsu :
                    response.Status == 137 && response.LastNsu >= cursor.LastNsu
                        ? response.LastNsu : cursor.LastNsu;
                if (next < cursor.LastNsu ||
                    (response.Status == 138 && (next <= cursor.LastNsu || next > response.MaxNsu)))
                    throw new InvalidDataException("CT-e retornou NSU inconsistente; checkpoint preservado.");

                var wait = response.Status is 137 or 656 ||
                    (response.Status == 138 && response.LastNsu == response.MaxNsu);
                var final = reserved with
                {
                    LastNsu = next,
                    NextAllowedAtUtc = wait ? DateTime.UtcNow.AddHours(1) : DateTime.UtcNow,
                    LastStatus = response.Status,
                    LastReason = response.Reason.Length > 255 ? response.Reason[..255] : response.Reason
                };
                await WriteAsync(id, final, ct);

                return new
                {
                    status = response.Status, reason = response.Reason,
                    downloaded = response.Documents.Count,
                    filesSaved = individual.Created,
                    filesAlreadyPresent = individual.AlreadyPresent,
                    skippedNonProcCTe = individual.SkippedNonProcNFe,
                    saveSucceeded = true,
                    destinationDirectory = folder,
                    archiveUrl = archiveId is null ? null :
                        "/api/capture/archive/" + company + "/" + archiveId,
                    lastNsu = next.ToString("D15"),
                    maxNsu = response.MaxNsu.ToString("D15"),
                    nextAllowedUtc = final.NextAllowedAtUtc
                };
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        finally { Gate.Release(); }
    }

    private string StatePath(Guid companyId) =>
        Path.Combine(Root, companyId.ToString("D") + ".json");

    private async Task<Cursor> ReadAsync(Guid id, CancellationToken ct)
    {
        var path = StatePath(id);
        if (!File.Exists(path))
            return new Cursor(0, DateTime.MinValue);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<Cursor>(file, cancellationToken: ct)
            ?? throw new InvalidDataException("Checkpoint CT-e vazio.");
    }

    private async Task WriteAsync(Guid id, Cursor value, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var path = StatePath(id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, value, cancellationToken: ct);
                await file.FlushAsync(ct);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
