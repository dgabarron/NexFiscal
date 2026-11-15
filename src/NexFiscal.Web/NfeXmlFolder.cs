using System.Security.Cryptography;

namespace NexFiscal.Web;

public enum XmlFileState { Missing, Valid, Different }

/// <summary>
/// Pasta de destino dos XMLs completos: um arquivo por NF-e, nomeado pela chave de acesso.
/// Nunca sobrescreve um arquivo válido e nunca apaga: conteúdo divergente é preservado com outro nome.
/// </summary>
public static class NfeXmlFolder
{
    /// <summary>Valida o caminho e confirma que é possível gravar nele. Cria a pasta final se ela não existir.</summary>
    public static string Prepare(string? directory)
    {
        var path = directory?.Trim() ?? "";
        if (path.Length is 0 or > 400 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(path))
            throw new NexFiscalException("invalid_folder",
                "Informe o caminho completo da pasta de destino, por exemplo C:\\NexFiscal\\XML.", 400);
        path = Path.GetFullPath(path);
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".nexfiscal-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new NexFiscalException("folder_not_writable",
                $"Não foi possível gravar na pasta {path}. Verifique se ela existe e se o usuário do Windows que executa o NexFiscal tem permissão de escrita.", 409);
        }
        return path;
    }

    public static string PathFor(string directory, string accessKey)
    {
        // NF-e: 44 posições; NFS-e nacional: 50 dígitos. Nada além disso vira nome de arquivo.
        if (!NfeProtocol.IsAccessKey(accessKey) && !NfseProtocol.IsAccessKey(accessKey))
            throw new ArgumentException("Chave de acesso inválida.", nameof(accessKey));
        return Path.Combine(directory, accessKey + ".xml");
    }

    public static XmlFileState Check(string directory, string accessKey, string expectedSha256)
    {
        var path = PathFor(directory, accessKey);
        if (!File.Exists(path)) return XmlFileState.Missing;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? XmlFileState.Valid : XmlFileState.Different;
    }

    /// <summary>
    /// Grava o XML completo de forma atômica (temporário + renomear).
    /// Arquivo igual é mantido; arquivo divergente ou corrompido é renomeado para evidência antes da gravação.
    /// </summary>
    /// <returns>true se um arquivo foi gravado; false se o arquivo válido já existia.</returns>
    public static bool Write(string directory, string accessKey, byte[] xml, string sha256)
    {
        var path = PathFor(directory, accessKey);
        var state = Check(directory, accessKey, sha256);
        if (state == XmlFileState.Valid) return false;

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(xml);
                file.Flush(flushToDisk: true);
            }
            if (state == XmlFileState.Different)
                File.Move(path, path + ".invalido-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                                Guid.NewGuid().ToString("N")[..6], overwrite: false);
            File.Move(temporary, path, overwrite: false);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
