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
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, Port);
    kestrel.Limits.MaxRequestBodySize = 1024 * 1024; // um A1 em Base64 tem poucos KB
});

var dataDirectory = new NexFiscalDataDirectory(builder.Configuration["NEXFISCAL_DATA_DIR"]);
builder.Services.AddSingleton(dataDirectory);
var protection = builder.Services.AddDataProtection()
    .SetApplicationName("NexFiscal")
    .PersistKeysToFileSystem(new DirectoryInfo(dataDirectory.PathFor(".nexfiscal-keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<IHanaConnectionFactory, HanaConnectionFactory>();
builder.Services.AddSingleton<HanaCredentialStore>();
builder.Services.AddSingleton<HanaSetup>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CertificateVault>();
builder.Services.AddSingleton<ICompanyStore, HanaCompanyStore>();
builder.Services.AddSingleton<CompanyService>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    // Defesa em profundidade: só o próprio computador, e só pelo endereço local
    // (o segundo teste impede que um site externo alcance a API por DNS rebinding).
    var ip = context.Connection.RemoteIpAddress;
    var local = ip is not null && (IPAddress.IsLoopback(ip) ||
        ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4()));
    // Alterações só são aceitas quando partem da própria tela do NexFiscal, nunca de outro site aberto no navegador.
    var origin = context.Request.Headers.Origin.ToString();
    var foreignOrigin = origin.Length > 0 && !HttpMethods.IsGet(context.Request.Method) &&
        !(Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host is "127.0.0.1" or "localhost");
    if (!local || foreignOrigin || context.Request.Host.Host is not ("127.0.0.1" or "localhost"))
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

app.MapGet("/api/health", () => Results.Ok(new { product = "NexFiscal", version = "V1", stage = 2 }));

app.MapGet("/api/setup/hana/status", async (HanaSetup hana, CancellationToken ct) =>
    Results.Ok(await hana.StatusAsync(ct)));
app.MapPost("/api/setup/hana/test", (HanaSetupRequest? req, HanaSetup hana, CancellationToken ct) =>
    Run(() => hana.TestAsync(req, ct)));
app.MapPost("/api/setup/hana/save", (HanaSetupRequest? req, HanaSetup hana, CancellationToken ct) =>
    Run(() => hana.SaveAsync(req, ct)));

app.MapPost("/api/setup/hana/upgrade", (HanaSetup hana, CancellationToken ct) =>
    Run(() => hana.UpgradeAsync(ct)));

app.MapGet("/api/companies", (CompanyService companies, CancellationToken ct) =>
    Run(() => companies.ListAsync(ct)));
app.MapPost("/api/companies/certificates/inspect", (CertificateUpload? upload, CompanyService companies, CancellationToken ct) =>
    Run(() => companies.InspectAsync(upload, ct)));
app.MapPost("/api/companies", (CertificateUpload? upload, CompanyService companies, CancellationToken ct) =>
    Run(() => companies.RegisterAsync(upload, ct)));
app.MapPut("/api/companies/{id}/certificate", (string id, CertificateUpload? upload, CompanyService companies, CancellationToken ct) =>
    Run(() => companies.UpdateCertificateAsync(id, upload, ct)));
app.MapDelete("/api/companies/{id}", (string id, CompanyService companies, CancellationToken ct) =>
    Run(() => companies.RemoveAsync(id, ct)));

app.MapFallback(() => Results.NotFound());

app.Logger.LogInformation("Dados privados do NexFiscal em {Root}", dataDirectory.Root);
try { app.Run(); }
catch (IOException ex) when (ex.InnerException is AddressInUseException)
{
    // Neste ponto o host já foi descartado: os loggers (ex.: Log de Eventos do Windows)
    // não podem mais ser usados. Escrever direto no console.
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERRO: a porta {Port} já está em uso. Feche a outra instância do NexFiscal " +
        "(inclusive versões antigas ou uma execução anterior ainda aberta) e execute novamente.");
    Environment.ExitCode = 1;
}

async Task<IResult> Run<T>(Func<Task<T>> operation)
{
    try { return Results.Ok(await operation()); }
    catch (NexFiscalException ex)
    {
        return Results.Json(new { error = ex.Message, kind = ex.Kind, issues = ex.Issues }, statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogError("Falha inesperada: {Type}", ex.GetType().Name);
        return Results.Json(new { error = "Falha inesperada. Nada foi salvo.", kind = "unexpected", issues = Array.Empty<string>() }, statusCode: 500);
    }
}

public partial class Program;
