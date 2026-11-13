using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Sap.Data.Hana;

public sealed record HanaSetupRequest(string Server, int Port, string Username, string Password);
public sealed record HanaSetupResult(bool Connected, bool SchemaReady, int TablesFound, string Message);
public sealed record HanaStatus(bool Configured, bool Connected, bool SchemaReady, int TablesFound, string Message);

/// <summary>Somente a etapa HANA da V1. Nunca armazena credenciais sem testar e validar o schema.</summary>
public sealed class HanaSetup(IDataProtectionProvider provider, NexFiscalDataDirectory dataDirectory)
{
    private readonly IDataProtector _protector = provider.CreateProtector("NexFiscal.HanaSetup.v1");
    private string ConfigPath => dataDirectory.PathFor(".nexfiscal-hana.dat");

    public bool HasSavedFile => File.Exists(ConfigPath);

    public static void Validate(HanaSetupRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.Server) || request.Server.Length > 255 ||
            request.Server.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) ||
            request.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 1024)
            throw new ArgumentException("Informe servidor, porta, usuário e senha válidos.");
    }

    public HanaSetupRequest? Load()
    {
        if (!HasSavedFile) return null;
        // Falha de descriptografia é erro, nunca confundir com ausência da configuração.
        return JsonSerializer.Deserialize<HanaSetupRequest>(
            _protector.Unprotect(File.ReadAllText(ConfigPath)))
            ?? throw new InvalidDataException("Configuração HANA inválida.");
    }

    private static string Build(HanaSetupRequest request)
    {
        Validate(request);
        var builder = new DbConnectionStringBuilder
        {
            ["Server"] = request.Server.Trim() + ":" + request.Port,
            ["UID"] = request.Username.Trim(),
            ["PWD"] = request.Password,
            ["Pooling"] = "False"
        };
        return builder.ConnectionString;
    }

    private static HanaConnection OpenConnection(HanaSetupRequest request) => new(Build(request));

    public async Task<HanaSetupResult> TestAsync(HanaSetupRequest request, CancellationToken ct)
    {
        await using var db = OpenConnection(request);
        await db.OpenAsync(ct);
        // Conexão OK mesmo se o schema ainda precisa ser criado.
        return await HanaSchemaVerifier.VerifyAsync(db, ct);
    }

    public async Task<HanaSetupResult> SaveAsync(HanaSetupRequest request, CancellationToken ct)
    {
        await using var db = OpenConnection(request);
        await db.OpenAsync(ct);
        var result = await new HanaSchemaInstaller().InstallAsync(db, ct);
        if (!result.SchemaReady)
            throw new InvalidOperationException("A estrutura do HANA não está pronta; configuração não salva.");

        var protectedText = _protector.Protect(JsonSerializer.Serialize(request));
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var temporary = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, protectedText, ct);
            File.Move(temporary, ConfigPath, overwrite: true);
            return result;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<HanaStatus> StatusAsync(CancellationToken ct)
    {
        if (!HasSavedFile)
            return new(false, false, false, 0, "Conexão HANA ainda não configurada.");

        HanaSetupRequest settings;
        try { settings = Load() ?? throw new InvalidDataException("Configuração ausente."); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
            or JsonException or InvalidDataException or IOException)
        {
            return new(false, false, false, 0,
                "Configuração protegida inacessível. Verifique as chaves e o usuário Windows original; não sobrescreva os arquivos.");
        }

        try
        {
            var result = await TestAsync(settings, ct);
            return new(true, true, result.SchemaReady, result.TablesFound,
                result.SchemaReady ? "Conexão SAP HANA ativa e estrutura validada." : result.Message);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException
            or System.Net.Sockets.SocketException or ArgumentException)
        {
            return new(true, false, false, 0,
                "Configuração salva, mas não foi possível conectar ao HANA. Confira a disponibilidade do banco.");
        }
    }
}
