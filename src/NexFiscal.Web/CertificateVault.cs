using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace NexFiscal.Web;

public enum VaultState { Missing, Unreadable, Readable }

/// <summary>Conteúdo cifrado do cofre: o PFX original e sua senha.</summary>
public sealed record VaultEntry(string PfxBase64, string Password)
{
    public override string ToString() => "VaultEntry(conteúdo oculto)";
}

/// <summary>Resultado de uma gravação, usado para desfazê-la se o registro no HANA falhar.</summary>
public sealed record VaultWrite(string Path, string? Backup);

/// <summary>
/// Cofre dos certificados A1: um arquivo cifrado por empresa em .nexfiscal-certs, na raiz de dados privada
/// (fora do wwwroot). Mesmas chaves DPAPI da configuração HANA.
/// Um arquivo existente nunca é sobrescrito: a versão anterior vira backup.
/// </summary>
public sealed class CertificateVault(
    IDataProtectionProvider provider, NexFiscalDataDirectory dataDirectory, ILogger<CertificateVault> logger)
{
    private readonly IDataProtector _protector = provider.CreateProtector("NexFiscal.CompanyCertificate.v1");

    public string Directory => dataDirectory.PathFor(".nexfiscal-certs");

    public string PathFor(string companyId)
    {
        if (companyId.Length is 0 or > 36 || companyId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new NexFiscalException("invalid_company", "Identificador de empresa inválido.", 400);
        return Path.Combine(Directory, companyId + ".a1.dat");
    }

    public (VaultState State, VaultEntry? Entry) Read(string companyId)
    {
        var path = PathFor(companyId);
        if (!File.Exists(path)) return (VaultState.Missing, null);
        try
        {
            var entry = JsonSerializer.Deserialize<VaultEntry>(_protector.Unprotect(File.ReadAllText(path).Trim()));
            if (entry is null || string.IsNullOrEmpty(entry.PfxBase64) || entry.Password is null)
                throw new InvalidDataException("Conteúdo incompleto.");
            return (VaultState.Readable, entry);
        }
        catch (Exception ex)
        {
            // Arquivo presente mas não descriptografável: "ilegível", nunca "ausente".
            logger.LogError("Certificado guardado da empresa {CompanyId} não pôde ser lido ({Type}). O arquivo foi mantido intacto.",
                companyId, ex.GetType().Name);
            return (VaultState.Unreadable, null);
        }
    }

    /// <summary>Grava de forma atômica. Se já havia arquivo, ele é renomeado para backup antes.</summary>
    public async Task<VaultWrite> WriteAsync(string companyId, byte[] pfx, string password, CancellationToken ct)
    {
        var path = PathFor(companyId);
        System.IO.Directory.CreateDirectory(Directory);
        var protectedText = _protector.Protect(JsonSerializer.Serialize(new VaultEntry(Convert.ToBase64String(pfx), password)));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string? backup = null;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await file.WriteAsync(Encoding.UTF8.GetBytes(protectedText), ct);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                backup = path + ".anterior-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                         Guid.NewGuid().ToString("N")[..6] + ".bak";
                File.Move(path, backup, overwrite: false);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch
            {
                if (backup is not null && !File.Exists(path)) File.Move(backup, path);
                throw;
            }
            return new VaultWrite(path, backup);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Desfaz uma gravação: remove o arquivo novo e devolve o anterior ao lugar.</summary>
    public void Rollback(VaultWrite write)
    {
        if (File.Exists(write.Path)) File.Delete(write.Path);
        if (write.Backup is not null && File.Exists(write.Backup)) File.Move(write.Backup, write.Path);
    }

    /// <summary>Remoção definitiva do material privado de uma empresa SEM histórico fiscal, após confirmação.</summary>
    public void Delete(string companyId)
    {
        var path = PathFor(companyId);
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (var file in System.IO.Directory.GetFiles(Directory, Path.GetFileName(path) + "*"))
            File.Delete(file);
    }
}
