using System.Security.Cryptography;
using NexFiscal.Capture;

/// <summary>
/// Grava cada docZip descompactado como um XML individual na pasta existente,
/// indicada pelo operador do servidor. Não cria diretórios arbitrários nem
/// substitui arquivos existentes com conteúdo diferente.
/// </summary>
public sealed class NfeIndividualXmlStore
{
    public static string ValidateDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Informe a pasta de destino dos XMLs.");
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Informe um caminho absoluto da pasta de destino.");
        var full = Path.GetFullPath(directory.Trim());
        var root = Path.GetPathRoot(full);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Selecione uma pasta específica, não a raiz da unidade.");
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("A pasta informada não existe no servidor NexFiscal.");
        return full;
    }

    public async Task<NfeIndividualSaveResult> SaveAsync(
        string directory, NfeDistributionResult response, CancellationToken ct, string documentType = "NFE")
    {
        var full = ValidateDirectory(directory);
        if (documentType is not ("NFE" or "CTE"))
            throw new ArgumentException("Tipo fiscal inválido.", nameof(documentType));
        if (response.Status != 138 || response.Documents.Count == 0)
            return new NfeIndividualSaveResult(full, 0, 0, [], 0);

        var created = 0;
        var existing = 0;
        var skipped = 0;
        var names = new List<string>();
        foreach (var document in response.Documents.OrderBy(d => d.Nsu))
        {
            if (!(documentType == "NFE" ? NfeCompleteDocument.IsProcNFe(document) : CteCompleteDocument.IsProcCTe(document)))
            {
                skipped++;
                continue;
            }
            if (document.Nsu is < 1 or > 999_999_999_999_999L ||
                document.XmlBytes.Length is < 1 or > 10_000_000)
                throw new InvalidDataException("XML recebido inválido para gravação individual.");

            // O NSU numérico é usado como nome, sem qualquer caminho de origem externa.
            var name = (documentType == "CTE" ? "CTe_" : "") + document.Nsu.ToString("D15") + ".xml";
            var destination = Path.Combine(full, name);
            var digest = SHA256.HashData(document.XmlBytes);
            if (!string.Equals(Convert.ToHexString(digest), document.Sha256,
                               StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Hash SHA-256 do XML recebido não confere.");

            if (File.Exists(destination))
            {
                await using var current = File.OpenRead(destination);
                var existingDigest = await SHA256.HashDataAsync(current, ct);
                if (!CryptographicOperations.FixedTimeEquals(digest, existingDigest))
                    throw new IOException("Arquivo XML com mesmo NSU já existe com conteúdo diferente: " + name);
                existing++;
                names.Add(name);
                continue;
            }

            var temporary = Path.Combine(full, "." + Guid.NewGuid().ToString("N") + ".xml.tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(document.XmlBytes, ct);
                    await stream.FlushAsync(ct);
                }
                // Nunca sobrescreve XML existente.
                File.Move(temporary, destination);
                created++;
                names.Add(name);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return new NfeIndividualSaveResult(full, created, existing, names.ToArray(), skipped);
    }
}

public sealed record NfeIndividualSaveResult(
    string Directory, int Created, int AlreadyPresent, string[] Filenames, int SkippedNonProcNFe);
