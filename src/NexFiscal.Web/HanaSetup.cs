using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Sap.Data.Hana;

public sealed record HanaSetupRequest(string Server, int Port, string Username, string Password);
public sealed record HanaSetupResult(bool Connected, bool SchemaReady, int TablesFound, string Message);

/// <summary>
/// O serviço de configuração é local (loopback) e armazena a conexão cifrada
/// pelo ASP.NET Core Data Protection; nunca persiste senha em texto puro.
/// </summary>
public sealed class HanaSetup(IDataProtectionProvider provider, IWebHostEnvironment host, IConfiguration configuration)
{
    private const string FileName = ".nexfiscal-hana.dat";
    private readonly IDataProtector _protector = provider.CreateProtector("NexFiscal.HanaSetup.v1");
    private static readonly string[] RequiredTables =
    [
        "BRAI_NEX_SchemaVersion", "BRAI_NEX_Company", "BRAI_NEX_CaptureCursor",
        "BRAI_NEX_Envelope", "BRAI_NEX_CaptureLog"
    ];

    private string ConfigPath => Path.Combine(host.ContentRootPath, FileName);

    public bool Configured => Load() is not null;

    public HanaSetupRequest? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<HanaSetupRequest>(
                _protector.Unprotect(File.ReadAllText(ConfigPath)));
        }
        catch
        {
            // Não tratar configuração ilegível como válida.
            return null;
        }
    }

    public string? ConnectionString
    {
        get
        {
            var entry = Load();
            if (entry is not null) return Build(entry);
            return configuration["NexFiscal:HanaConnectionString"];
        }
    }

    public static DbProviderFactory Factory => HanaFactory.Instance;

    private static string Build(HanaSetupRequest request)
    {
        var builder = new DbConnectionStringBuilder
        {
            ["Server"] = request.Server.Trim() + ":" + request.Port,
            ["UID"] = request.Username.Trim(),
            ["PWD"] = request.Password,
            ["Pooling"] = "False"
        };
        return builder.ConnectionString;
    }

    public async Task<HanaSetupResult> TestAsync(HanaSetupRequest request, CancellationToken ct)
    {
        Validate(request);

        // Não logar ou devolver exceções de conexão contendo credenciais.
        await using var connection = new HanaConnection(Build(request));
        await connection.OpenAsync(ct);
        return await HanaSchemaVerifier.VerifyAsync(connection, ct);
    }

    private static void Validate(HanaSetupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Server) || request.Server.Length > 255 ||
            request.Server.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) ||
            request.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 1024)
            throw new ArgumentException("Informe endereço, porta, usuário e senha válidos.");

    }

    public async Task<HanaSetupResult> ProvisionAsync(HanaSetupRequest request, CancellationToken ct)
    {
        // O comando é acionado explicitamente durante a instalação, após
        // o teste da conexão. Nunca migrar automaticamente na inicialização.
        Validate(request);
        await using var connection = new HanaConnection(Build(request));
        await connection.OpenAsync(ct);
        return await new HanaSchemaInstaller().InstallAsync(connection, ct);
    }

    public async Task<HanaSetupResult> SaveAsync(HanaSetupRequest request, CancellationToken ct)
    {
        var result = await TestAsync(request, ct);
        if (!result.SchemaReady) return result;

        var protectedPayload = _protector.Protect(JsonSerializer.Serialize(request));
        var tmp = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, protectedPayload, ct);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        return result;
    }
}
