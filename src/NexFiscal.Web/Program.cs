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
builder.Services.AddSingleton<CompanyCertificateVault>();
builder.Services.AddSingleton<NfeCaptureCoordinator>();
builder.Services.AddSingleton<NfeKeyDiagnostics>();
builder.Services.AddSingleton<NfeFirstDownload>();
builder.Services.AddSingleton<CteFirstDownload>();
builder.Services.AddSingleton<CteSefazCatchup>();
builder.Services.AddSingleton<CaptureScheduleSettings>();
builder.Services.AddSingleton<NfeSefazCatchup>();
builder.Services.AddHostedService<NfeMonitoringWorker>();
builder.Services.AddHostedService<CteMonitoringWorker>();
builder.Services.AddSingleton<NfeXmlArchive>();
builder.Services.AddSingleton<NfeIndividualXmlStore>();
builder.Services.AddSingleton<NfeHandoffPreparation>();
var app = builder.Build();
var hanaSetup = app.Services.GetRequiredService<HanaSetup>();
var certificateVault = app.Services.GetRequiredService<CompanyCertificateVault>();

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

app.MapPost("/api/setup/hana/provision", async (HanaSetupRequest request, CancellationToken ct) =>
{
    try { return Results.Ok(await hanaSetup.ProvisionAsync(request, ct)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Parâmetros de conexão inválidos." }); }
    catch (Exception) { return Results.Problem("Não foi possível preparar o schema BRAI_NEX. Confira permissões DDL ou a estrutura existente. Nenhum dado foi apagado.", statusCode: 503); }
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
        return Results.Problem("Configure a conexão SAP HANA antes de cadastrar.", statusCode: 503);

    var step = "validacao_certificado";
    var diagnosticId = Guid.NewGuid().ToString("N")[..8];
    try
    {
        var (pfx, password) = await ReadCertificateAsync(req, ct);
        var identity = A1CompanyCertificate.Inspect(pfx, password);

        step = "consulta_empresa_hana";
        var store = new HanaCompanyRegistrationStore(HanaSetup.Factory, hanaSetup.ConnectionString!);
        var existingId = await store.FindPausedCompanyIdAsync(identity.Cnpj, 1, ct);
        if (existingId is not null && certificateVault.IsStored(existingId))
            return Results.Conflict(new { error = "Empresa e certificado já cadastrados. Consulte a área de operação." });

        step = "gravacao_empresa_hana";
        var registered = existingId is null
            ? await new A1CompanyRegistration(store).RegisterAsync(pfx, password, ct)
            : new CompanyRegistration(existingId, identity.Cnpj, identity.LegalName, 1,
                identity.Thumbprint, identity.ValidUntil, false, "AGUARDANDO_INICIALIZACAO");

        step = "protecao_certificado";
        await certificateVault.SaveAsync(registered.CompanyId, registered.Cnpj, pfx, password, ct);

        return Results.Ok(new
        {
            registered.CompanyId, registered.Cnpj, registered.LegalName,
            registered.Environment, registered.Enabled, registered.CaptureStatus,
            certificateStored = true,
            message = "Empresa e A1 protegido cadastrados. A captura permanece pausada."
        });
    }
    catch (Exception ex) when (step == "validacao_certificado" &&
                               ex is InvalidDataException or ArgumentException or System.Security.Cryptography.CryptographicException)
    {
        return Results.BadRequest(new { error = "Certificado ou senha inválidos." });
    }
    catch (Exception ex)
    {
        // NUNCA registrar Exception.ToString ou Message: drivers podem incluir SQL e credenciais.
        var dbCode = ex is DbException db ? db.ErrorCode.ToString() : "n/a";
        app.Logger.LogError(
            "NexFiscal cadastro falhou. Diagnostico={DiagnosticId} Etapa={Step} Classe={Class} DbCode={DbCode}",
            diagnosticId, step, ex.GetType().Name, dbCode);
        var message = step switch
        {
            "consulta_empresa_hana" => "Não foi possível consultar o cadastro no HANA.",
            "gravacao_empresa_hana" => "Não foi possível gravar a empresa no HANA.",
            "protecao_certificado" => "Empresa cadastrada pausada, mas não foi possível proteger o A1. Não repita cadastros manualmente; tente novamente com a mesma empresa após verificar o serviço.",
            _ => "Cadastro não concluído."
        };
        return Results.Json(new { error = message, etapa = step, diagnostico = diagnosticId },
            statusCode: 500);
    }
}).DisableAntiforgery();

// Pré-voo somente leitura. Não chama SEFAZ, não cria NSU e não ativa empresas.
app.MapGet("/api/capture/status", async (NfeCaptureCoordinator coordinator, CancellationToken ct) =>
{
    if (!HasHana()) return Results.Problem("HANA não configurado.", statusCode: 503);
    try { return Results.Ok(await coordinator.StatusAsync(ct)); }
    catch (Exception ex)
    {
        app.Logger.LogError("Pré-voo NF-e falhou: {Type}", ex.GetType().Name);
        return Results.Problem("Não foi possível consultar o estado da captura no HANA.", statusCode: 503);
    }
});

// Consulta exclusivamente dados locais para identificar NF-e ausentes; não chama SEFAZ.
app.MapPost("/api/nfe/diagnostics/keys", async (
    NfeKeyDiagnostics.Request request, NfeKeyDiagnostics diagnostics, CancellationToken ct) =>
{
    try { return Results.Ok(await diagnostics.CheckAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Exception ex)
    {
        app.Logger.LogError("Diagnóstico de NF-e locais falhou: {Type}", ex.GetType().Name);
        return Results.Problem("Não foi possível verificar as NF-e no HANA e na pasta.", statusCode: 503);
    }
});

// Diagnóstico HTTPS sem distNSU: permitido mesmo antes do handoff.
app.MapPost("/api/capture/test-connectivity", async (CaptureTestRequest request, NfeCaptureCoordinator coordinator, CancellationToken ct) =>
{
    try { return Results.Ok(await coordinator.TestConnectivityAsync(request.CompanyId, ct)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Empresa inválida." }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Exception ex)
    {
        app.Logger.LogError("Teste HTTPS SEFAZ falhou: {Type}", ex.GetType().Name);
        return Results.Problem("Não foi possível concluir o teste HTTPS com a SEFAZ. Verifique firewall, TLS e certificado A1.", statusCode: 503);
    }
});

// Preparação transacional do checkpoint, com evidência do sistema anterior.
// Nunca habilita a empresa, nunca executa distNSU e nunca sobrescreve NSU existente.
app.MapPost("/api/capture/prepare-handoff", async (
    NfeHandoffPreparation.Request request, NfeHandoffPreparation handoff, CancellationToken ct) =>
{
    try { return Results.Ok(await handoff.PrepareAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Exception ex)
    {
        app.Logger.LogError("Preparação NSU falhou. Categoria={Category}", ex.GetType().Name);
        return Results.Problem("Não foi possível preparar o checkpoint no SAP HANA. Nenhuma consulta foi executada.", statusCode: 503);
    }
});

// Exporta o ZIP já recebido sem nova consulta fiscal e sem acesso ao HANA.
app.MapGet("/api/capture/archive/{companyId}/{archiveId}", (
    string companyId, string archiveId, string? kind, NfeXmlArchive archive) =>
{
    var file = archive.Find(companyId, archiveId);
    var type = kind == "CTe" ? "CTe" : "NFe";
    return file is null
        ? Results.NotFound()
        : Results.File(file, "application/zip", "NexFiscal_" + type + "_" + archiveId + ".zip");
});

// Consulta REAL à SEFAZ para a empresa cadastrada: percorre os lotes
// disponíveis, usando o cursor de distribuição vigente e o mesmo controle fiscal.
app.MapPost("/api/sefaz/nfe/catchup", async (
    NfeSefazCatchup.Request request, NfeSefazCatchup service, CancellationToken ct) =>
{
    try { return Results.Accepted(value: await service.StartAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DirectoryNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Falha ao iniciar distribuição SEFAZ em lotes");
        return Results.Problem("Não foi possível iniciar a captura SEFAZ.", statusCode: 503);
    }
});
app.MapGet("/api/sefaz/nfe/catchup/{companyId}", (
    string companyId, NfeSefazCatchup service) =>
{
    try
    {
        var status = service.Get(companyId);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Agenda por tipo: o Worker ativo é o de NF-e.
app.MapGet("/api/capture/schedules/{companyId}", async (
    string companyId, CaptureScheduleSettings settings, CancellationToken ct) =>
{
    try { return Results.Ok(await settings.GetAsync(companyId, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex)
    {
        app.Logger.LogError("Leitura de agenda falhou: {Type}", ex.GetType().Name);
        return Results.Problem("Falha ao carregar configuração de captura.", statusCode: 503);
    }
});

app.MapPost("/api/capture/schedules/{companyId}", async (
    string companyId, CaptureScheduleSettings.ScheduleInput[] schedules,
    CaptureScheduleSettings settings, CancellationToken ct) =>
{
    try { return Results.Ok(await settings.SaveAsync(companyId, schedules, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DirectoryNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex)
    {
        app.Logger.LogError("Gravação de agenda falhou: {Type}", ex.GetType().Name);
        return Results.Problem("Falha ao salvar configuração de captura.", statusCode: 503);
    }
});

// CT-e tem distribuição e cursor próprios, independentes do NSU de NF-e.
app.MapPost("/api/sefaz/cte/catchup", async (
    CteSefazCatchup.Request request, CteSefazCatchup service, CancellationToken ct) =>
{
    try { return Results.Accepted(value: await service.StartAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DirectoryNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Falha ao iniciar captura CT-e");
        return Results.Problem("Não foi possível iniciar a captura CT-e.", statusCode: 503);
    }
});
app.MapGet("/api/sefaz/cte/catchup/{companyId}", (
    string companyId, CteSefazCatchup service) =>
{
    try
    {
        var status = service.Get(companyId);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/cte/status/{companyId}", async (
    string companyId, CteFirstDownload service, CancellationToken ct) =>
{
    try { return Results.Ok(await service.StatusAsync(companyId, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Falha ao consultar cursor CT-e");
        return Results.Json(new { error = "Não foi possível ler o checkpoint CT-e." }, statusCode: 503);
    }
});

app.MapPost("/api/cte/download", async (
    CaptureDownloadRequest request, CteFirstDownload service,
    CaptureScheduleSettings settings, CancellationToken ct) =>
{
    try
    {
        var folder = await settings.RequireDestinationAsync(request.CompanyId, "CTE", ct);
        return Results.Ok(await service.ExecuteAsync(request.CompanyId, request.PreviousConsumerStopped, folder, ct));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DirectoryNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Exception ex)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        app.Logger.LogError(ex, "Falha de captura CT-e Diagnostico={Id}", id);
        return Results.Json(new { error = "A consulta ou a gravação CT-e falhou. Verifique os logs do servidor.", diagnostico = id }, statusCode: 503);
    }
});

// Primeira distribuição NF-e: um clique, sem digitar NSU, somente no portal local.
// Bloqueio de uma hora já reservado no HANA antes de acessar a SEFAZ.
app.MapPost("/api/capture/download", async (CaptureTestRequest request,
    NfeFirstDownload capture, CaptureScheduleSettings settings, CancellationToken ct) =>
{
    try
    {
        var folder = await settings.RequireDestinationAsync(request.CompanyId, "NFE", ct);
        return Results.Ok(await capture.ExecuteAsync(request.CompanyId, folder, ct));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DirectoryNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Exception ex)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        app.Logger.LogError(ex, "NF-e consulta falhou. Diagnostico={Id}", id);
        return Results.Json(new { error = "A consulta SOAP ou a gravação no HANA falhou. Verifique o log do servidor.", diagnostico = id }, statusCode: 503);
    }
});

// Consulta unitária somente para empresa explicitamente autorizada e com
// checkpoint válido. A rotina nunca cria nem importa NSU automaticamente.
app.MapPost("/api/capture/test-once", async (CaptureTestRequest request, NfeCaptureCoordinator coordinator, CancellationToken ct) =>
{
    try { return Results.Ok(await coordinator.TestOnceAsync(request.CompanyId, ct)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Empresa inválida." }); }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Consulta unitária NF-e falhou: {Type}", ex.GetType().Name);
        return Results.Problem("A consulta NF-e falhou. Verifique a conexão SEFAZ e os logs do servidor.", statusCode: 503);
    }
});

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

public sealed record CaptureTestRequest(string CompanyId);

public sealed record CaptureDownloadRequest(string CompanyId, bool PreviousConsumerStopped);

