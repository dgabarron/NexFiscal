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
public sealed class NexFiscalApp : WebApplicationFactory<Program>
{
    public readonly string Root = Directory.CreateTempSubdirectory("nexfiscal-http-").FullName;
    public readonly FakeHanaServer Hana = new();
    public IPAddress? RemoteIp = IPAddress.Loopback;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("NEXFISCAL_DATA_DIR", Root);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IHanaConnectionFactory>(new FakeHanaFactory(Hana));
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
        Assert.Equal(1, health.GetProperty("stage").GetInt32());
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
}
