using System.Data.Common;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using NexFiscal.Capture;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["NexFiscal:ListenUrl"] ?? "http://127.0.0.1:5187");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 5_500_000);
var protection = builder.Services.AddDataProtection()
    .SetApplicationName("NexFiscal")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".nexfiscal-keys")));
if (OperatingSystem.IsWindows())
    protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<HanaSetup>();
var app = builder.Build();
var hanaSetup = app.Services.GetRequiredService<HanaSetup>();

// Secure bootstrap: this initial configuration portal must never be accessible
// from the network. Production multi-user authentication is not implemented.
app.Use(async (ctx, next) =>
{
    if (ctx.Connection.RemoteIpAddress is not { } ip ||
        !(IPAddress.IsLoopback(ip) || ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4())))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new
{
    product = "NexFiscal", state = "configuration",
    hanaConfigured = HasHana(), hanaSetupCompleted = hanaSetup.Configured
}));

app.MapPost("/api/setup/hana/test", async (HanaSetupRequest request, CancellationToken ct) =>
{
    try { return Results.Ok(await hanaSetup.TestAsync(request, ct)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Parâmetros de conexão inválidos." }); }
    catch (Exception) { return Results.Problem("Não foi possível conectar ao HANA. Confira servidor, porta, credenciais e conectividade.", statusCode: 503); }
});

app.MapPost("/api/setup/hana/save", async (HanaSetupRequest request, CancellationToken ct) =>
{
    try
    {
        var result = await hanaSetup.SaveAsync(request, ct);
        return result.SchemaReady ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Parâmetros de conexão inválidos." }); }
    catch (Exception) { return Results.Problem("Não foi possível validar e salvar a configuração HANA.", statusCode: 503); }
});

app.MapPost("/api/certificates/inspect", async (HttpRequest req, CancellationToken ct) =>
{
    try
    {
        var (pfx, password) = await ReadCertificateAsync(req, ct);
        var identity = A1CompanyCertificate.Inspect(pfx, password);
        return Results.Ok(new
        {
            identity.Cnpj, identity.LegalName, identity.State,
            identity.ValidFrom, identity.ValidUntil, identity.Issuer,
            identity.Thumbprint, identity.HasPrivateKey,
            chainVerified = false,
            message = "Dados extraídos. Cadeia ICP-Brasil, revogação e autorização SEFAZ ainda não verificadas."
        });
    }
    catch (Exception e) when (e is InvalidDataException or System.Security.Cryptography.CryptographicException or ArgumentException)
    {
        return Results.BadRequest(new { error = "Certificado inválido, senha incorreta ou formato não suportado." });
    }
}).DisableAntiforgery();

app.MapPost("/api/companies/register", async (HttpRequest req, CancellationToken ct) =>
{
    if (!HasHana())
        return Results.Problem("Configure a conexão SAP HANA na primeira etapa antes de cadastrar.", statusCode: 503);
    try
    {
        var (pfx, password) = await ReadCertificateAsync(req, ct);
        var store = new HanaCompanyRegistrationStore(HanaSetup.Factory, hanaSetup.ConnectionString!);
        var registered = await new A1CompanyRegistration(store).RegisterAsync(pfx, password, ct);
        return Results.Ok(new { registered.CompanyId, registered.Cnpj, registered.LegalName, registered.Environment, registered.Enabled, registered.CaptureStatus,
            message = "Empresa cadastrada. Sincronização aguardando inicialização segura; A1 ainda não é armazenado." });
    }
    catch (Exception e) when (e is InvalidDataException or ArgumentException or System.Security.Cryptography.CryptographicException)
    {
        return Results.BadRequest(new { error = "Não foi possível validar o certificado ou os parâmetros." });
    }
    catch (Exception)
    {
        // Never return secrets, connection strings or provider exception details.
        return Results.Problem("Cadastro não concluído. Consulte o diagnóstico local do HANA.", statusCode: 500);
    }
}).DisableAntiforgery();

app.MapFallbackToFile("index.html");
app.Run();

bool HasHana() => !string.IsNullOrWhiteSpace(hanaSetup.ConnectionString);

static async Task<(byte[] pfx, string password)> ReadCertificateAsync(HttpRequest req, CancellationToken ct)
{
    if (!req.HasFormContentType) throw new InvalidDataException("Envie multipart/form-data.");
    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("certificate");
    var password = form["password"].ToString();
    if (file is null || file.Length is <= 0 or > 5_000_000 ||
        !new[] { ".pfx", ".p12" }.Contains(Path.GetExtension(file.FileName).ToLowerInvariant()) ||
        string.IsNullOrEmpty(password))
        throw new InvalidDataException("Certificado/senha ausente ou arquivo inválido.");
    using var bytes = new MemoryStream();
    await file.CopyToAsync(bytes, ct);
    return (bytes.ToArray(), password);
}

public partial class Program { }
