using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NexFiscal.Web;

namespace NexFiscal.Tests;

/// <summary>Sobe o Program real em memória, com diretório de dados temporário e HANA simulado.</summary>
public class NexFiscalApp : WebApplicationFactory<Program>
{
    public readonly string Root = Directory.CreateTempSubdirectory("nexfiscal-http-").FullName;
    public readonly FakeHanaServer Hana = new();
    public readonly InMemoryCompanyStore Companies = new();
    public readonly InMemoryCaptureStore Capture = new();
    public readonly InMemoryManifestStore Manifest = new();
    public readonly FakeSefaz Sefaz = new();
    public readonly InMemoryNfseStore Nfse = new();
    public readonly FakeAdn Adn = new();
    public IPAddress? RemoteIp = IPAddress.Loopback;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("NEXFISCAL_DATA_DIR", Root);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IHanaConnectionFactory>(new FakeHanaFactory(Hana));
            services.AddSingleton<ICompanyStore>(Companies);
            services.AddSingleton<ICaptureStore>(Capture);
            services.AddSingleton<ISefazTransport>(Sefaz);
            services.AddSingleton<INfseStore>(Nfse);
            services.AddSingleton<IManifestStore>(Manifest);
            services.AddSingleton<IAdnTransport>(Adn);
            services.AddSingleton<TimeProvider>(new TestClock());
            services.AddSingleton<IStartupFilter>(new RemoteIpFilter(this));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    // O servidor de teste não tem socket; simula o endereço de origem da conexão.
    private sealed class RemoteIpFilter(NexFiscalApp owner) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                context.Connection.RemoteIpAddress = owner.RemoteIp;
                return following();
            });
            next(app);
        };
    }
}

public class HttpApiTests
{
    private static readonly object ValidBody = new
    {
        server = "hana.teste.local", port = 30015, username = FakeHanaServer.User, password = FakeHanaServer.Password
    };

    [Fact]
    public async Task Health_e_status_inicial()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();

        var health = await client.GetFromJsonAsync<JsonElement>("/api/health");
        var status = await client.GetFromJsonAsync<JsonElement>("/api/setup/hana/status");

        Assert.Equal("NexFiscal", health.GetProperty("product").GetString());
        Assert.Equal(6, health.GetProperty("stage").GetInt32());
        Assert.Equal("not_configured", status.GetProperty("state").GetString());
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.False(File.Exists(Path.Combine(app.Root, ".nexfiscal-hana.dat")));
    }

    [Fact]
    public async Task Fluxo_completo_testar_salvar_status_sem_devolver_senha()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();

        var test = await client.PostAsJsonAsync("/api/setup/hana/test", ValidBody);
        var testJson = await test.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.Equal("pending", testJson.GetProperty("schemaState").GetString());
        Assert.False(File.Exists(Path.Combine(app.Root, ".nexfiscal-hana.dat")));

        var save = await client.PostAsJsonAsync("/api/setup/hana/save", ValidBody);
        var saveText = await save.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Contains("\"saved\":true", saveText);

        var statusText = await client.GetStringAsync("/api/setup/hana/status");
        Assert.Contains("\"state\":\"ready\"", statusText);
        Assert.Contains("hana.teste.local", statusText);
        foreach (var body in new[] { saveText, statusText, await test.Content.ReadAsStringAsync() })
        {
            Assert.DoesNotContain("assword", body);
            Assert.DoesNotContain("S3nha", body);
        }
    }

    [Theory]
    [InlineData("{\"server\":\"a b\",\"port\":30015,\"username\":\"u\",\"password\":\"p\"}", "Servidor inválido")]
    [InlineData("{\"server\":\"host\",\"port\":0,\"username\":\"u\",\"password\":\"p\"}", "Porta inválida")]
    [InlineData("{\"server\":\"host\",\"port\":30015,\"username\":\"u\"}", "senha")]
    [InlineData("{}", "Servidor inválido")]
    public async Task Entrada_invalida_retorna_400_com_mensagem_do_campo(string body, string expected)
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();
        foreach (var route in new[] { "test", "save" })
        {
            var response = await client.PostAsync("/api/setup/hana/" + route, new StringContent(body, Encoding.UTF8, "application/json"));
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(expected, json.GetProperty("error").GetString());
        }
        Assert.Equal(0, app.Hana.OpenCount);
    }

    [Fact]
    public async Task Erros_do_hana_chegam_com_mensagem_em_portugues_e_sem_salvar()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();
        app.Hana.Online = false;

        var response = await client.PostAsJsonAsync("/api/setup/hana/save", ValidBody);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("connection_refused", json.GetProperty("kind").GetString());
        Assert.Contains("Conexão recusada", json.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(app.Root, ".nexfiscal-hana.dat")));
    }

    [Fact]
    public async Task Requisicao_de_maquina_remota_e_bloqueada()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();
        app.RemoteIp = IPAddress.Parse("192.168.0.50");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/setup/hana/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/setup/hana/save", ValidBody)).StatusCode);
        Assert.Equal(0, app.Hana.OpenCount);
    }

    [Fact]
    public async Task Host_externo_apontando_para_o_loopback_e_bloqueado()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/hana/save") { Content = JsonContent.Create(ValidBody) };
        request.Headers.Host = "site-malicioso.example";

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(0, app.Hana.OpenCount);
    }

    [Fact]
    public async Task Formulario_de_outro_site_sem_json_e_ignorado()
    {
        using var app = new NexFiscalApp();
        var response = await app.CreateClient().PostAsync("/api/setup/hana/save",
            new StringContent(JsonSerializer.Serialize(ValidBody), Encoding.UTF8, "text/plain"));

        // Só application/json alcança as rotas de configuração (um formulário de outro site não consegue enviá-lo).
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(0, app.Hana.OpenCount);
        Assert.False(File.Exists(Path.Combine(app.Root, ".nexfiscal-hana.dat")));
    }

    [Fact]
    public async Task Rota_desconhecida_retorna_404()
    {
        using var app = new NexFiscalApp();
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync("/api/inexistente")).StatusCode);
    }

    // ---------- Função 2 ----------

    private static object A1(byte[]? pfx = null, string? password = TestCertificates.Password) =>
        new { pfxBase64 = Convert.ToBase64String(pfx ?? TestCertificates.Create()), password };

    [Fact]
    public async Task Fluxo_de_empresa_inspecionar_cadastrar_listar_atualizar_remover()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();

        var inspect = await client.PostAsJsonAsync("/api/companies/certificates/inspect", A1());
        var preview = await inspect.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
        Assert.Equal("11.222.333/0001-81", preview.GetProperty("cnpjFormatted").GetString());
        Assert.Equal("EMPRESA TESTE LTDA", preview.GetProperty("legalName").GetString());
        Assert.EndsWith("Z", preview.GetProperty("notAfterUtc").GetString());
        Assert.Empty(app.Companies.Rows);

        var create = await client.PostAsJsonAsync("/api/companies", A1());
        var createText = await create.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = JsonDocument.Parse(createText).RootElement.GetProperty("company").GetProperty("companyId").GetString()!;

        var listText = await client.GetStringAsync("/api/companies");
        Assert.Contains("\"certificateStatus\":\"stored\"", listText);

        var update = await client.PutAsJsonAsync($"/api/companies/{id}/certificate", A1());
        var updateText = await update.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Contains("\"outcome\":\"updated\"", updateText);

        foreach (var body in new[] { createText, listText, updateText, await inspect.Content.ReadAsStringAsync() })
        {
            Assert.DoesNotContain("assword", body);
            Assert.DoesNotContain("pfx", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Senha#A1", body);
        }

        var remove = await client.DeleteAsync($"/api/companies/{id}");
        Assert.Contains("\"outcome\":\"removed\"", await remove.Content.ReadAsStringAsync());
        Assert.Equal("[]", await client.GetStringAsync("/api/companies"));
        Assert.False(Directory.Exists(Path.Combine(app.Root, ".nexfiscal-certs")) &&
                     Directory.GetFiles(Path.Combine(app.Root, ".nexfiscal-certs")).Length > 0);
    }

    [Fact]
    public async Task Erros_de_empresa_chegam_com_status_e_mensagem()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();

        var wrong = await client.PostAsJsonAsync("/api/companies", A1(password: "errada"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("invalid_password", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kind").GetString());

        await client.PostAsJsonAsync("/api/companies", A1());
        var duplicate = await client.PostAsJsonAsync("/api/companies", A1());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/companies/nao-existe")).StatusCode);

        app.Companies.Unavailable = true;
        var list = await client.GetAsync("/api/companies");
        Assert.Equal(HttpStatusCode.Conflict, list.StatusCode);
        Assert.Contains("SAP HANA", (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("https://site-malicioso.example", HttpStatusCode.Forbidden)]
    [InlineData("null", HttpStatusCode.Forbidden)]
    [InlineData("http://127.0.0.1:5187", HttpStatusCode.OK)]
    [InlineData("http://localhost:5187", HttpStatusCode.OK)]
    public async Task Alteracoes_vindas_de_outro_site_no_navegador_sao_bloqueadas(string origin, HttpStatusCode expected)
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/companies") { Content = JsonContent.Create(A1()) };
        request.Headers.Add("Origin", origin);

        Assert.Equal(expected, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, app.Companies.Rows.Count);
    }

    [Fact]
    public async Task Atualizar_estrutura_pela_api_usa_a_conexao_salva()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/setup/hana/upgrade", null)).StatusCode);

        await client.PostAsJsonAsync("/api/setup/hana/save", ValidBody);
        app.Hana.Tables.Remove("BRAI_NEX_CompanyCertificate");
        var status = await client.GetFromJsonAsync<JsonElement>("/api/setup/hana/status");
        Assert.True(status.GetProperty("canUpgrade").GetBoolean());

        var upgrade = await client.PostAsync("/api/setup/hana/upgrade", null);
        Assert.Equal(HttpStatusCode.OK, upgrade.StatusCode);
        Assert.Contains("BRAI_NEX_CompanyCertificate", await upgrade.Content.ReadAsStringAsync());
        Assert.Contains("\"state\":\"ready\"", await client.GetStringAsync("/api/setup/hana/status"));
    }

    // ---------- Função 3 ----------

    [Fact]
    public async Task Fluxo_de_captura_pela_api_cria_pedido_duravel_e_mostra_o_resultado_sem_expor_nsu()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();
        var folder = Directory.CreateTempSubdirectory("nexfiscal-api-xml-").FullName;
        try
        {
            var create = await client.PostAsJsonAsync("/api/companies", A1());
            var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("company").GetProperty("companyId").GetString()!;

            var before = await client.GetFromJsonAsync<JsonElement>($"/api/companies/{id}/nfe/capture");
            Assert.True(before.GetProperty("canCapture").GetBoolean());
            Assert.Equal(JsonValueKind.Null, before.GetProperty("job").ValueKind);

            var invalid = await client.PostAsJsonAsync($"/api/companies/{id}/nfe/captures", new { destinationDirectory = "relativa" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Empty(app.Capture.Jobs);

            app.Sefaz.Reply(Sefaz.Found(2, 2, Sefaz.Complete(1, 101), Sefaz.Summary(2, 102)));
            var start = await client.PostAsJsonAsync($"/api/companies/{id}/nfe/captures", new { destinationDirectory = folder });
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            await app.Services.GetRequiredService<CaptureWorker>().RunDueJobsAsync(CancellationToken.None);

            var afterText = await client.GetStringAsync($"/api/companies/{id}/nfe/capture");
            var after = JsonDocument.Parse(afterText).RootElement;
            var job = after.GetProperty("job");
            Assert.Equal("completed_with_pending", job.GetProperty("state").GetString());
            Assert.Equal((1, 1, 1), (job.GetProperty("newComplete").GetInt32(), job.GetProperty("ignored").GetInt32(), job.GetProperty("pending").GetInt32()));
            Assert.Equal(folder, after.GetProperty("destinationDirectory").GetString());
            Assert.Equal(1, after.GetProperty("pendingSummaries").GetInt32());
            Assert.NotEqual(JsonValueKind.Null, after.GetProperty("nextAllowedAtUtc").ValueKind);
            Assert.DoesNotContain("nsu", afterText, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(folder, Sefaz.Key(101) + ".xml")));

            var pending = await client.GetFromJsonAsync<JsonElement>($"/api/companies/{id}/nfe/pending");
            Assert.Equal(Sefaz.Key(102), pending[0].GetProperty("accessKey").GetString());
            Assert.True(pending[0].GetProperty("canManifest").GetBoolean());

            // Etapa 6: a ciência só sai por pedido explícito, com as notas marcadas na tela.
            Assert.Empty(app.Sefaz.EventCalls);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/companies/{id}/nfe/manifest", new { accessKeys = Array.Empty<string>() })).StatusCode);
            app.Sefaz.ReplyEvents();
            var manifest = await client.PostAsJsonAsync($"/api/companies/{id}/nfe/manifest", new { accessKeys = new[] { Sefaz.Key(102) } });
            var manifestText = await manifest.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
            Assert.Equal(1, JsonDocument.Parse(manifestText).RootElement.GetProperty("registered").GetInt32());
            Assert.DoesNotContain("Signature", manifestText);
            pending = await client.GetFromJsonAsync<JsonElement>($"/api/companies/{id}/nfe/pending");
            Assert.Equal("registered", pending[0].GetProperty("manifestState").GetString());
            Assert.False(pending[0].GetProperty("canManifest").GetBoolean());
            var foreign = new HttpRequestMessage(HttpMethod.Post, $"/api/companies/{id}/nfe/manifest") { Content = JsonContent.Create(new { accessKeys = new[] { Sefaz.Key(102) } }) };
            foreign.Headers.Add("Origin", "https://site-externo.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/companies/nao-existe/nfe/capture")).StatusCode);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // ---------- Função 4 ----------

    [Fact]
    public async Task Monitoramento_pela_api_fica_desligado_ate_ser_ativado_e_valida_a_entrada()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();
        var folder = Directory.CreateTempSubdirectory("nexfiscal-api-mon-").FullName;
        try
        {
            var create = await client.PostAsJsonAsync("/api/companies", A1());
            var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("company").GetProperty("companyId").GetString()!;
            var url = $"/api/companies/{id}/nfe/monitor";

            var initial = await client.GetFromJsonAsync<JsonElement>(url);
            Assert.False(initial.GetProperty("enabled").GetBoolean());
            Assert.Equal(60, initial.GetProperty("intervalMinutes").GetInt32());
            Assert.Equal(JsonValueKind.Null, initial.GetProperty("nextRunAtUtc").ValueKind);

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { enabled = true, intervalMinutes = 0, destinationDirectory = folder })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { enabled = true, intervalMinutes = 30, destinationDirectory = "relativa" })).StatusCode);
            Assert.Empty(app.Capture.Monitors);

            var save = await client.PutAsJsonAsync(url, new { enabled = true, intervalMinutes = 30, destinationDirectory = folder });
            var savedText = await save.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            Assert.Contains("\"enabled\":true", savedText);
            Assert.Contains("\"intervalMinutes\":30", savedText);
            Assert.DoesNotContain("nsu", savedText, StringComparison.OrdinalIgnoreCase);
            Assert.True(app.Capture.Monitors[id].Enabled);

            var off = await client.PutAsJsonAsync(url, new { enabled = false, intervalMinutes = 30 });
            Assert.Contains("\"enabled\":false", await off.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/companies/nao-existe/nfe/monitor")).StatusCode);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // ---------- NFS-e ----------

    [Fact]
    public async Task Nfse_pela_api_captura_monitora_e_nao_se_mistura_com_a_nfe()
    {
        using var app = new NexFiscalApp();
        var client = app.CreateClient();
        var folder = Directory.CreateTempSubdirectory("nexfiscal-api-nfse-").FullName;
        try
        {
            var create = await client.PostAsJsonAsync("/api/companies", A1());
            var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("company").GetProperty("companyId").GetString()!;

            var before = await client.GetFromJsonAsync<JsonElement>($"/api/companies/{id}/nfse/capture");
            Assert.True(before.GetProperty("canCapture").GetBoolean());
            Assert.Equal(JsonValueKind.Null, before.GetProperty("job").ValueKind);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/companies/{id}/nfse/captures", new { destinationDirectory = "relativa" })).StatusCode);

            app.Adn.Reply(Adn.Found(Adn.Note(1, 1))).Reply(Adn.None());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/companies/{id}/nfse/captures", new { destinationDirectory = folder })).StatusCode);
            await app.Services.GetRequiredService<CaptureWorker>().RunDueJobsAsync(CancellationToken.None);

            var text = await client.GetStringAsync($"/api/companies/{id}/nfse/capture");
            Assert.Contains("\"state\":\"completed\"", text);
            Assert.Contains("\"newComplete\":1", text);
            Assert.DoesNotContain("nsu", text, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(folder, Adn.Key(1) + ".xml")));
            // A tela de NF-e não enxerga o pedido de NFS-e.
            Assert.Equal(JsonValueKind.Null, (await client.GetFromJsonAsync<JsonElement>($"/api/companies/{id}/nfe/capture")).GetProperty("job").ValueKind);

            var monitor = await client.GetFromJsonAsync<JsonElement>($"/api/companies/{id}/nfse/monitor");
            Assert.False(monitor.GetProperty("enabled").GetBoolean());
            Assert.Equal(folder, monitor.GetProperty("destinationDirectory").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/companies/{id}/nfse/monitor", new { enabled = true, intervalMinutes = 0, destinationDirectory = folder })).StatusCode);
            var saved = await client.PutAsJsonAsync($"/api/companies/{id}/nfse/monitor", new { enabled = false, intervalMinutes = 45, destinationDirectory = folder });
            Assert.Contains("\"intervalMinutes\":45", await saved.Content.ReadAsStringAsync());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
