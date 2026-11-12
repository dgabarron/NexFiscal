using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using NexFiscal.Capture;

/// <summary>
/// Retrospectiva NF-e sem NSU digitado. Reconstrói candidatos a partir de ZIPs
/// recebidos anteriormente. Apenas consNSU pontual, nunca reinicia distNSU.
/// </summary>
public sealed class NfeHistoricalRecovery(
    IWebHostEnvironment host, CompanyCertificateVault vault,
    CaptureScheduleSettings settings, NfeXmlArchive archive, NfeIndividualXmlStore writer)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string StateRoot => Path.Combine(host.ContentRootPath, ".nexfiscal-recovery");
    private string ArchiveRoot => Path.Combine(host.ContentRootPath, ".nexfiscal-downloads");

    public sealed record Candidate(string Nsu, string Schema, DateTimeOffset? DocumentDate,
        bool Complete, string Source, bool XmlPresent);
    public sealed record Preview(DateTimeOffset From, DateTimeOffset To, int Candidates,
        int WithoutDate, int AlreadyComplete, IReadOnlyList<Candidate> Documents,
        string Warning);
    private sealed record Attempts(DateTimeOffset? BlockedUntil, List<DateTimeOffset> Timestamps, Dictionary<long, DateTimeOffset>? RecentNsus = null);

    public async Task<Preview> PreviewAsync(string companyId, DateTimeOffset from, CancellationToken ct)
    {
        var id = Company(companyId);
        if (from > DateTimeOffset.UtcNow || from < DateTimeOffset.UtcNow.AddDays(-90))
            throw new ArgumentException("A data deve estar dentro dos últimos 90 dias.");
        var folder = await settings.RequireDestinationAsync(id, "NFE", ct);
        var now = DateTimeOffset.UtcNow;
        var dir = Path.Combine(ArchiveRoot, id);
        var found = new Dictionary<long, Candidate>();
        var noDate = 0;
        if (Directory.Exists(dir))
        {
            foreach (var zipPath in Directory.EnumerateFiles(dir, "*.zip").OrderByDescending(File.GetCreationTimeUtc).Take(500))
            {
                ct.ThrowIfCancellationRequested();
                using var zip = ZipFile.OpenRead(zipPath);
                var manifest = zip.GetEntry("manifest.json");
                if (manifest is null || manifest.Length > 500_000) continue;
                using var manifestStream = manifest.Open();
                using var json = await JsonDocument.ParseAsync(manifestStream,
                    new JsonDocumentOptions { MaxDepth = 20 }, ct);
                if (!json.RootElement.TryGetProperty("documents", out var docs) || docs.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in docs.EnumerateArray())
                {
                    if (!entry.TryGetProperty("nsu", out var nsuValue) ||
                        !long.TryParse(nsuValue.GetString(), out var nsu) || nsu <= 0 ||
                        !entry.TryGetProperty("schema", out var schemaValue)) continue;
                    var schema = schemaValue.GetString() ?? "";
                    if (schema.Length > 200 || !schema.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase) ||
                        !(schema.StartsWith("resNFe_v", StringComparison.OrdinalIgnoreCase) ||
                          schema.StartsWith("procNFe_v", StringComparison.OrdinalIgnoreCase))) continue;
                    var xmlEntry = zip.GetEntry(nsu.ToString("D15", CultureInfo.InvariantCulture) + ".xml");
                    if (xmlEntry is null || xmlEntry.Length is < 1 or > 10_000_000) continue;
                    await using var input = xmlEntry.Open();
                    using var copy = new MemoryStream();
                    await input.CopyToAsync(copy, ct);
                    var bytes = copy.ToArray();
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (!entry.TryGetProperty("sha256", out var sha) ||
                        !string.Equals(hash, sha.GetString(), StringComparison.OrdinalIgnoreCase)) continue;
                    var dt = ExtractDate(bytes);
                    if (dt is null) { noDate++; continue; }
                    if (dt < from || dt > now.AddDays(1)) continue;
                    var complete = schema.StartsWith("procNFe_v", StringComparison.OrdinalIgnoreCase);
                    var present = File.Exists(Path.Combine(folder, nsu.ToString("D15") + ".xml"));
                    var item = new Candidate(nsu.ToString("D15"), schema, dt, complete, "Lote SEFAZ armazenado", present);
                    // Preferir registros completos quando houver versões repetidas do mesmo NSU.
                    if (!found.TryGetValue(nsu, out var prev) || (!prev.Complete && complete))
                        found[nsu] = item;
                }
            }
        }
        var list = found.Values.OrderBy(x => x.DocumentDate).ThenBy(x => x.Nsu).ToArray();
        return new Preview(from, now, list.Length, noDate,
            list.Count(x => x.Complete || x.XmlPresent), list,
            "Pesquisa local dos lotes disponíveis, não varredura por data na SEFAZ. " +
            "A janela de 90 dias não garante cobertura completa; NSUs não armazenados não são descobertos.");
    }

    /// <summary>Uma única tentativa pontual por clique, sem fila agressiva.</summary>
    public async Task<object> RecoverOneAsync(string companyId, DateTimeOffset from,
        bool previousConsumerStopped, CancellationToken ct)
    {
        if (!previousConsumerStopped)
            throw new InvalidOperationException("Confirme que outro consumidor fiscal não está em operação para o CNPJ.");
        var company = Company(companyId);
        if (!await Gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Outra recuperação de NF-e está em andamento.");
        try
        {
            var preview = await PreviewAsync(company, from, ct);
            var statePath = Path.Combine(StateRoot, company + ".json");
            Directory.CreateDirectory(StateRoot);
            var attempts = await ReadAttemptsAsync(statePath, ct);
            var now = DateTimeOffset.UtcNow;
            var oldAttempts = attempts.RecentNsus ?? new Dictionary<long, DateTimeOffset>();
            var candidate = preview.Documents.FirstOrDefault(d => !d.Complete && !d.XmlPresent &&
                (!oldAttempts.TryGetValue(long.Parse(d.Nsu, CultureInfo.InvariantCulture), out var lastTry) ||
                 lastTry < now.AddHours(-1)));
            if (candidate is null)
                return new { status = 0, attempted = false,
                    message = "Nenhum NSU de resumo pendente identificado nos lotes armazenados dentro da data-limite.",
                    availableCandidates = preview.Candidates, lastNsuUnchanged = true };

            var folder = await settings.RequireDestinationAsync(company, "NFE", ct);
            var nsu = long.Parse(candidate.Nsu, CultureInfo.InvariantCulture);
            if (attempts.BlockedUntil is { } until && until > now)
                throw new InvalidOperationException("Reconsulta indisponível até " + until.ToString("dd/MM HH:mm 'UTC'") + ".");
            // Reservar cota com margem para ações feitas por outros programas.
            var recent = attempts.Timestamps.Where(t => t > now.AddHours(-1)).ToList();
            if (recent.Count >= 19)
                throw new InvalidOperationException("Limite local de segurança atingido (19 consultas pontuais/hora).");
            recent.Add(now);
            var tried = oldAttempts.Where(x => x.Value > now.AddDays(-1))
                .ToDictionary(x => x.Key, x => x.Value);
            tried[nsu] = now;
            await WriteAttemptsAsync(statePath, new Attempts(null, recent, tried), ct);

            if (!vault.IsStored(company))
                throw new InvalidOperationException("Certificado A1 não encontrado.");
            var (pfx, password) = await vault.ReadAsync(company, ct);
            try
            {
                using var cert = new X509Certificate2(pfx, password,
                    OperatingSystem.IsWindows()
                        ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
                if (!chain.Build(cert))
                    throw new InvalidOperationException("Não foi possível validar a cadeia do certificado A1.");
                var identity = A1CompanyCertificate.Inspect(pfx, password);
                var response = await new NfeDistributionClient()
                    .QuerySpecificNsuAsync(cert, identity.Cnpj, nsu, 1, ct);
                if (response.Status == 656)
                    await WriteAttemptsAsync(statePath, new Attempts(DateTimeOffset.UtcNow.AddHours(1), recent, tried), ct);
                if (response.Status != 138)
                    return new { status = response.Status, reason = response.Reason,
                        attempted = true, nsu = candidate.Nsu, filesSaved = 0,
                        lastNsuUnchanged = true, message = "Documento não disponibilizado nesta reconsulta." };

                // Arquivar antes de alterar arquivos da pasta do usuário.
                var backedUp = await archive.SaveAsync(company, response, ct);
                var saved = await writer.SaveAsync(folder, response, ct);
                return new
                {
                    status = response.Status, reason = response.Reason,
                    attempted = true, nsu = candidate.Nsu,
                    filesSaved = saved.Created, alreadyPresent = saved.AlreadyPresent,
                    skippedNonProcNFe = saved.SkippedNonProcNFe,
                    archiveUrl = "/api/capture/archive/" + company + "/" + backedUp.Id,
                    lastNsuUnchanged = true,
                    message = saved.Created > 0 ? "procNFe recuperado na pasta configurada." :
                        "O NSU consultado não retornou novo XML completo."
                };
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        finally { Gate.Release(); }
    }

    private static string Company(string companyId) => Guid.TryParse(companyId, out var id)
        ? id.ToString("D") : throw new ArgumentException("Empresa inválida.");

    public static DateTimeOffset? ExtractDate(byte[] xml)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 10_000_000
            });
            var root = XDocument.Load(reader).Root;
            if (root is null) return null;
            // Resumos resNFe têm dhEmi; procNFe possui ide/dhEmi.
            var raw = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "dhEmi")?.Value
                ?? root.Descendants().FirstOrDefault(e => e.Name.LocalName == "dEmi")?.Value;
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date)) return date;
        }
        catch (XmlException) { }
        return null;
    }

    private static async Task<Attempts> ReadAttemptsAsync(string file, CancellationToken ct)
    {
        if (!File.Exists(file)) return new Attempts(null, []);
        await using var input = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<Attempts>(input, cancellationToken: ct)
            ?? throw new InvalidDataException("Controle de consultas pontuais inválido.");
    }

    private static async Task WriteAttemptsAsync(string file, Attempts state, CancellationToken ct)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, state, cancellationToken: ct);
                await output.FlushAsync(ct);
            }
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
