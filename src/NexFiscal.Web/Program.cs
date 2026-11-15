using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using NexFiscal.Web;

const int Port = 5187;

var contentRoot = NexFiscalDataDirectory.ResolveContentRoot(AppContext.BaseDirectory);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = Path.Combine(contentRoot, "wwwroot")
});
// Escuta fixa em loopback: não pode ser alterada por --urls nem ASPNETCORE_URLS.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, Port));

var dataDirectory = new NexFiscalDataDirectory(builder.Configuration["NEXFISCAL_DATA_DIR"]);
builder.Services.AddSingleton(dataDirectory);
var protection = builder.Services.AddDataProtection()
    .SetApplicationName("NexFiscal")
    .PersistKeysToFileSystem(new DirectoryInfo(dataDirectory.PathFor(".nexfiscal-keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<IHanaConnectionFactory, HanaConnectionFactory>();
builder.Services.AddSingleton<HanaCredentialStore>();
builder.Services.AddSingleton<HanaSetup>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    // Defesa em profundidade: só o próprio computador, e só pelo endereço local
    // (o segundo teste impede que um site externo alcance a API por DNS rebinding).
    var ip = context.Connection.RemoteIpAddress;
    var local = ip is not null && (IPAddress.IsLoopback(ip) ||
        ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4()));
    if (!local || context.Request.Host.Host is not ("127.0.0.1" or "localhost"))
    {
        context.Response.StatusCode = 403;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});
var staticRoot = Path.Combine(contentRoot, "wwwroot");
if (Directory.Exists(staticRoot))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(staticRoot) });
else app.Logger.LogWarning("Frontend não compilado. Execute npm run build em frontend.");

// Rota explícita: a página funciona mesmo quando o wwwroot é gerado pelo Vite
// depois do dotnet build, sem depender do manifesto estático.
app.MapGet("/", () =>
{
    var file = Path.Combine(staticRoot, "index.html");
    return File.Exists(file)
        ? Results.File(file, "text/html; charset=utf-8")
        : Results.Problem("Frontend não compilado. Execute npm run build em frontend.", statusCode: 503);
});

app.MapGet("/api/health", () => Results.Ok(new { product = "NexFiscal", version = "V1", stage = 1 }));

app.MapGet("/api/setup/hana/status", async (HanaSetup hana, CancellationToken ct) =>
    Results.Ok(await hana.StatusAsync(ct)));
app.MapPost("/api/setup/hana/test", (HanaSetupRequest? req, HanaSetup hana, CancellationToken ct) =>
    Run(() => hana.TestAsync(req, ct)));
app.MapPost("/api/setup/hana/save", (HanaSetupRequest? req, HanaSetup hana, CancellationToken ct) =>
    Run(() => hana.SaveAsync(req, ct)));

app.MapFallback(() => Results.NotFound());

app.Logger.LogInformation("Dados privados do NexFiscal em {Root}", dataDirectory.Root);
try { app.Run(); }
catch (IOException ex) when (ex.InnerException is AddressInUseException)
{
    app.Logger.LogCritical(
        "A porta {Port} já está em uso. Feche a outra instância do NexFiscal (inclusive versões antigas) e execute novamente.", Port);
    Environment.ExitCode = 1;
}

async Task<IResult> Run(Func<Task<HanaSetupResult>> operation)
{
    try { return Results.Ok(await operation()); }
    catch (HanaSetupException ex)
    {
        return Results.Json(new { error = ex.Message, kind = ex.Kind, issues = ex.Issues }, statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogError("Falha inesperada na configuração HANA: {Type}", ex.GetType().Name);
        return Results.Json(new { error = "Falha inesperada. A configuração não foi salva.", kind = "unexpected", issues = Array.Empty<string>() }, statusCode: 500);
    }
}

public partial class Program;
