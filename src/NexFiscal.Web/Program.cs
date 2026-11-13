using System.Net;
using Microsoft.AspNetCore.DataProtection;

var contentRoot = NexFiscalDataDirectory.ResolveContentRoot(AppContext.BaseDirectory);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot
});
builder.WebHost.UseUrls("http://127.0.0.1:5187");
var dataDirectory = new NexFiscalDataDirectory(
    Environment.GetEnvironmentVariable("NEXFISCAL_DATA_DIR"));
builder.Services.AddSingleton(dataDirectory);
var protection = builder.Services.AddDataProtection()
    .SetApplicationName("NexFiscal")
    .PersistKeysToFileSystem(new DirectoryInfo(dataDirectory.PathFor(".nexfiscal-keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<HanaSetup>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    var ip = context.Connection.RemoteIpAddress;
    if (ip is null || !(IPAddress.IsLoopback(ip) ||
        ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4())))
    {
        context.Response.StatusCode = 403;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { product = "NexFiscal", version = "V1", stage = 1 }));

app.MapGet("/api/setup/hana/status", async (HanaSetup hana, CancellationToken ct) =>
{
    return Results.Ok(await hana.StatusAsync(ct));
});
app.MapPost("/api/setup/hana/test", async (HanaSetupRequest req, HanaSetup hana, CancellationToken ct) =>
{
    try { return Results.Ok(await hana.TestAsync(req, ct)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Informe servidor, porta, usuário e senha válidos." }); }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Teste HANA falhou: {Type}", ex.GetType().Name);
        return Results.Json(new { error = "Falha de conexão SAP HANA. Verifique servidor, porta, credenciais e permissões." }, statusCode: 503);
    }
});
app.MapPost("/api/setup/hana/save", async (HanaSetupRequest req, HanaSetup hana, CancellationToken ct) =>
{
    try { return Results.Ok(await hana.SaveAsync(req, ct)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Informe servidor, porta, usuário e senha válidos." }); }
    catch (Exception ex)
    {
        app.Logger.LogError("Configuração HANA não concluída: {Type}", ex.GetType().Name);
        return Results.Json(new { error = "Não foi possível preparar as tabelas e salvar a conexão. Nenhuma tabela existente foi apagada. Verifique as permissões DDL e os logs." }, statusCode: 503);
    }
});

app.MapFallback(() => Results.NotFound());
app.Run();
