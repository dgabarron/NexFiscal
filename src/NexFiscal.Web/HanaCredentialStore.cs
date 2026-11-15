using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace NexFiscal.Web;

public enum StoredConfigState { Missing, Readable, Unreadable }

/// <summary>
/// Arquivo cifrado .nexfiscal-hana.dat na raiz de dados privada.
/// Um arquivo que existe mas não pode ser lido é "ilegível", nunca "ausente",
/// e só é substituído por pedido explícito — e mesmo assim é preservado como backup.
/// </summary>
public sealed class HanaCredentialStore(
    IDataProtectionProvider provider, NexFiscalDataDirectory dataDirectory, ILogger<HanaCredentialStore> logger)
{
    private readonly IDataProtector _protector = provider.CreateProtector("NexFiscal.HanaSetup.v1");

    public string ConfigPath => dataDirectory.PathFor(".nexfiscal-hana.dat");

    public (StoredConfigState State, HanaCredentials? Credentials) Read()
    {
        if (!File.Exists(ConfigPath)) return (StoredConfigState.Missing, null);
        try
        {
            var json = _protector.Unprotect(File.ReadAllText(ConfigPath).Trim());
            var stored = JsonSerializer.Deserialize<HanaCredentials>(json);
            if (stored is null || string.IsNullOrWhiteSpace(stored.Server) || string.IsNullOrWhiteSpace(stored.Username) ||
                string.IsNullOrEmpty(stored.Password) || stored.Port is < 1 or > 65535)
                throw new InvalidDataException("Conteúdo incompleto.");
            return (StoredConfigState.Readable, stored);
        }
        catch (Exception ex)
        {
            // Qualquer falha ao ler um arquivo existente é "ilegível". Registrar só o tipo do erro.
            logger.LogError("Configuração HANA existente não pôde ser lida ({Type}). O arquivo foi mantido intacto.",
                ex.GetType().Name);
            return (StoredConfigState.Unreadable, null);
        }
    }

    /// <summary>Escrita atômica: arquivo temporário gravado em disco e renomeado sobre o definitivo.</summary>
    /// <returns>Caminho do backup do arquivo ilegível anterior, quando houve substituição autorizada.</returns>
    public async Task<string?> WriteAsync(HanaCredentials credentials, bool replaceUnreadable, CancellationToken ct)
    {
        var (state, _) = Read();
        if (state == StoredConfigState.Unreadable && !replaceUnreadable)
            throw new HanaSetupException("stored_config_unreadable",
                "Já existe uma configuração protegida que não pôde ser lida. Ela não será sobrescrita sem a sua confirmação explícita.", 409);

        var protectedText = _protector.Protect(JsonSerializer.Serialize(credentials));
        Directory.CreateDirectory(dataDirectory.Root);
        var temporary = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
            if (state == StoredConfigState.Unreadable)
            {
                backup = ConfigPath + ".ilegivel-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                         Guid.NewGuid().ToString("N")[..6] + ".bak";
                File.Move(ConfigPath, backup, overwrite: false);
            }
            File.Move(temporary, ConfigPath, overwrite: true);
            return backup;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
