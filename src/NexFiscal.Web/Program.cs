using System.Data.Common;
using System.Net;
using NexFiscal.Capture;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["NexFiscal:ListenUrl"] ?? "http://127.0.0.1:5187");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 5_500_000);
var app = builder.Build();

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

app.MapGet("/api/health", () => Results.Ok(new { product = "NexFiscal", state = "configuration", hanaConfigured = HasHana() }));

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
        return Results.Problem("Configure NexFiscal:HanaConnectionString e NexFiscal:HanaFactoryType antes de cadastrar.", statusCode: 503);
    try
    {
        var (pfx, password) = await ReadCertificateAsync(req, ct);
        var factoryName = app.Configuration["NexFiscal:HanaFactoryType"]!;
        var factoryType = Type.GetType(factoryName, throwOnError: true)!;
        DbProviderFactory factory;
        var instance = factoryType.GetField("Instance")?.GetValue(null);
        if (instance is DbProviderFactory fieldFactory) factory = fieldFactory;
        else throw new InvalidOperationException("Factory HANA não encontrada.");
        var store = new HanaCompanyRegistrationStore(factory, app.Configuration["NexFiscal:HanaConnectionString"]!);
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

bool HasHana() => !string.IsNullOrWhiteSpace(app.Configuration["NexFiscal:HanaConnectionString"]) &&
                  !string.IsNullOrWhiteSpace(app.Configuration["NexFiscal:HanaFactoryType"]);

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
