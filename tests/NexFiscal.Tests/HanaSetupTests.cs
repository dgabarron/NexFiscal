using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NexFiscal.Web;

namespace NexFiscal.Tests;

/// <summary>Um diretório de dados temporário + HANA simulado. Cada chamada a Start() equivale a reiniciar o backend.</summary>
public sealed class TestInstallation : IDisposable
{
    public readonly string Root = Directory.CreateTempSubdirectory("nexfiscal-test-").FullName;
    public readonly FakeHanaServer Hana = new();
    public readonly ListLoggerProvider Logs = new();
    public string KeysDirectory => Path.Combine(Root, ".nexfiscal-keys");
    public string ConfigFile => Path.Combine(Root, ".nexfiscal-hana.dat");

    public HanaSetup Start()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(KeysDirectory), b => b.SetApplicationName("NexFiscal"));
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        var store = new HanaCredentialStore(provider, new NexFiscalDataDirectory(Root), factory.CreateLogger<HanaCredentialStore>());
        return new HanaSetup(store, new FakeHanaFactory(Hana), factory.CreateLogger<HanaSetup>());
    }

    public static HanaSetupRequest Request(string? password = null, bool replace = false) =>
        new("hana.teste.local", 30015, FakeHanaServer.User, password ?? FakeHanaServer.Password, replace);

    public void LoseKeys() => Directory.Delete(KeysDirectory, recursive: true);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public sealed class ListLoggerProvider : ILoggerProvider, ILogger
{
    public readonly List<string> Lines = [];
    public ILogger CreateLogger(string categoryName) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add(formatter(state, exception) + " " + exception);
    }
    public void Dispose() { }
}

public class HanaSetupTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ---------- Entrada inválida ----------

    [Theory]
    [InlineData("", 30015, "u", "p")]
    [InlineData("host com espaco", 30015, "u", "p")]
    [InlineData("host:30015", 30015, "u", "p")]
    [InlineData("host;x=1", 30015, "u", "p")]
    [InlineData("host", 0, "u", "p")]
    [InlineData("host", 65536, "u", "p")]
    [InlineData("host", 30015, " ", "p")]
    [InlineData("host", 30015, "u", "")]
    [InlineData(null, 30015, null, null)]
    public async Task Entrada_invalida_e_recusada_sem_abrir_conexao(string? server, int port, string? user, string? password)
    {
        using var install = new TestInstallation();
        var setup = install.Start();
        var request = new HanaSetupRequest(server, port, user, password);

        var test = await Assert.ThrowsAsync<NexFiscalException>(() => setup.TestAsync(request, Ct));
        var save = await Assert.ThrowsAsync<NexFiscalException>(() => setup.SaveAsync(request, Ct));

        Assert.Equal((400, "invalid_input"), (test.StatusCode, test.Kind));
        Assert.Equal((400, "invalid_input"), (save.StatusCode, save.Kind));
        Assert.Equal(0, install.Hana.OpenCount);
        Assert.False(File.Exists(install.ConfigFile));
    }

    [Fact]
    public async Task Corpo_ausente_e_recusado()
    {
        using var install = new TestInstallation();
        var error = await Assert.ThrowsAsync<NexFiscalException>(() => install.Start().SaveAsync(null, Ct));
        Assert.Equal(400, error.StatusCode);
    }

    // ---------- Testar conexão (H02, H03, H04) ----------

    [Fact]
    public async Task Testar_com_schema_inexistente_confirma_acesso_e_nao_cria_nem_salva_nada()
    {
        using var install = new TestInstallation();
        var result = await install.Start().TestAsync(TestInstallation.Request(), Ct);

        Assert.True(result.Connected);
        Assert.False(result.SchemaReady);
        Assert.Equal("pending", result.SchemaState);
        Assert.False(result.Saved);
        Assert.Contains("estrutura pendente", result.Message);
        Assert.Equal(6, result.Issues.Count);
        Assert.Empty(install.Hana.Writes);
        Assert.False(install.Hana.SchemaExists);
        Assert.False(File.Exists(install.ConfigFile));
    }

    [Fact]
    public async Task Testar_com_estrutura_completa_informa_validada_sem_escrever()
    {
        using var install = new TestInstallation();
        install.Hana.AddAllCompatibleTables();
        var result = await install.Start().TestAsync(TestInstallation.Request(), Ct);

        Assert.True(result.SchemaReady);
        Assert.Equal(6, result.TablesFound);
        Assert.Empty(install.Hana.Writes);
        Assert.False(File.Exists(install.ConfigFile));
    }

    [Fact]
    public async Task Senha_incorreta_gera_erro_controlado_sem_expor_a_senha()
    {
        using var install = new TestInstallation();
        var setup = install.Start();
        var request = TestInstallation.Request(password: "SenhaErrada!123");

        var test = await Assert.ThrowsAsync<NexFiscalException>(() => setup.TestAsync(request, Ct));
        var save = await Assert.ThrowsAsync<NexFiscalException>(() => setup.SaveAsync(request, Ct));

        Assert.Equal("invalid_credentials", test.Kind);
        Assert.Equal("invalid_credentials", save.Kind);
        Assert.DoesNotContain("SenhaErrada!123", test.Message + save.Message);
        Assert.DoesNotContain(install.Logs.Lines, line => line.Contains("SenhaErrada!123"));
        Assert.False(File.Exists(install.ConfigFile));
    }

    [Fact]
    public async Task Servidor_indisponivel_gera_mensagem_objetiva_e_nao_salva()
    {
        using var install = new TestInstallation();
        install.Hana.Online = false;
        var setup = install.Start();

        var test = await Assert.ThrowsAsync<NexFiscalException>(() => setup.TestAsync(TestInstallation.Request(), Ct));
        var save = await Assert.ThrowsAsync<NexFiscalException>(() => setup.SaveAsync(TestInstallation.Request(), Ct));

        Assert.Equal((503, "connection_refused"), (test.StatusCode, test.Kind));
        Assert.Contains("hana.teste.local:30015", test.Message);
        Assert.Equal("connection_refused", save.Kind);
        Assert.False(File.Exists(install.ConfigFile));
    }

    // ---------- Salvar (H05–H08) ----------

    [Fact]
    public async Task Salvar_em_banco_vazio_cria_schema_e_todas_as_tabelas_valida_e_grava_cifrado()
    {
        using var install = new TestInstallation();
        var result = await install.Start().SaveAsync(TestInstallation.Request(), Ct);

        Assert.True(result.Saved);
        Assert.True(result.SchemaReady);
        Assert.Equal(6, result.CreatedTables.Count);
        Assert.Equal(HanaSchemaVerifier.Required.Keys.Order(), install.Hana.Tables.Keys.Order());
        Assert.Equal([1, 2], install.Hana.Versions);
        Assert.Equal(9, install.Hana.Writes.Count()); // 1 schema + 6 tabelas + 2 linhas de versão

        var stored = File.ReadAllText(install.ConfigFile);
        Assert.DoesNotContain(FakeHanaServer.Password, stored);
        Assert.DoesNotContain(FakeHanaServer.User, stored);
        Assert.DoesNotContain("hana.teste.local", stored);
        Assert.Empty(Directory.GetFiles(install.Root, "*.tmp"));
        Assert.DoesNotContain(install.Logs.Lines, line => line.Contains(FakeHanaServer.Password));
    }

    [Fact]
    public async Task Salvar_duas_vezes_e_idempotente()
    {
        using var install = new TestInstallation();
        var setup = install.Start();
        await setup.SaveAsync(TestInstallation.Request(), Ct);
        var writesAfterFirst = install.Hana.Writes.Count();

        var second = await setup.SaveAsync(TestInstallation.Request(), Ct);

        Assert.True(second.Saved);
        Assert.Empty(second.CreatedTables);
        Assert.Equal(writesAfterFirst, install.Hana.Writes.Count());
        Assert.Equal(2, install.Hana.VersionRows);
    }

    [Fact]
    public async Task Salvar_com_tabelas_existentes_cria_somente_as_ausentes_e_nao_toca_nas_demais()
    {
        using var install = new TestInstallation();
        install.Hana.SchemaExists = true;
        install.Hana.Versions.Add(1);
        install.Hana.AddCompatibleTable("BRAI_NEX_SchemaVersion");
        install.Hana.AddCompatibleTable("BRAI_NEX_Company");
        install.Hana.Tables["BRAI_NEX_Company"].Add(("ColunaExtraLegada", "NVARCHAR")); // coluna a mais é tolerada
        var companyBefore = install.Hana.Tables["BRAI_NEX_Company"].ToList();

        var result = await install.Start().SaveAsync(TestInstallation.Request(), Ct);

        Assert.True(result.Saved);
        Assert.Equal(["BRAI_NEX_CaptureCursor", "BRAI_NEX_Envelope", "BRAI_NEX_CaptureLog", "BRAI_NEX_CompanyCertificate"], result.CreatedTables);
        Assert.Equal(companyBefore, install.Hana.Tables["BRAI_NEX_Company"]);
        Assert.Equal([1, 2], install.Hana.Versions); // a versão 1 já existia e não foi repetida
        Assert.Equal(4, install.Hana.Writes.Count(sql => sql.StartsWith("CREATE COLUMN TABLE")));
        Assert.Equal(5, install.Hana.Writes.Count());
    }

    [Theory]
    [InlineData(false)] // coluna ausente
    [InlineData(true)]  // tipo divergente
    public async Task Tabela_existente_incompativel_bloqueia_sem_criar_alterar_ou_salvar(bool wrongType)
    {
        using var install = new TestInstallation();
        install.Hana.SchemaExists = true;
        install.Hana.AddCompatibleTable("BRAI_NEX_Company");
        var columns = install.Hana.Tables["BRAI_NEX_Company"];
        var index = columns.FindIndex(c => c.Column == "Cnpj");
        if (wrongType) columns[index] = ("Cnpj", "INTEGER"); else columns.RemoveAt(index);
        var setup = install.Start();

        var test = await setup.TestAsync(TestInstallation.Request(), Ct);
        var save = await Assert.ThrowsAsync<NexFiscalException>(() => setup.SaveAsync(TestInstallation.Request(), Ct));

        Assert.Equal("incompatible", test.SchemaState);
        Assert.Equal((409, "schema_incompatible"), (save.StatusCode, save.Kind));
        Assert.Contains(save.Issues, issue => issue.Contains("BRAI_NEX_Company.Cnpj"));
        Assert.Empty(install.Hana.Writes); // nem as tabelas ausentes são criadas
        Assert.Single(install.Hana.Tables);
        Assert.False(File.Exists(install.ConfigFile));
    }

    [Fact]
    public async Task Sem_privilegio_para_criar_schema_nao_salva_e_explica()
    {
        using var install = new TestInstallation();
        install.Hana.FailWith = sql => sql.StartsWith("CREATE SCHEMA") ? 258 : null;

        var error = await Assert.ThrowsAsync<NexFiscalException>(() => install.Start().SaveAsync(TestInstallation.Request(), Ct));

        Assert.Equal("insufficient_privilege", error.Kind);
        Assert.Contains("CREATE SCHEMA", error.Message);
        Assert.False(File.Exists(install.ConfigFile));
        Assert.Empty(install.Hana.Tables);
    }

    [Fact]
    public async Task Falha_no_meio_do_provisionamento_nao_salva_e_nova_tentativa_completa_so_o_que_falta()
    {
        using var install = new TestInstallation();
        install.Hana.FailWith = sql => sql.Contains("\"BRAI_NEX_Envelope\" (") ? 2048 : null;
        var setup = install.Start();

        var error = await Assert.ThrowsAsync<NexFiscalException>(() => setup.SaveAsync(TestInstallation.Request(), Ct));
        Assert.Equal("hana_error", error.Kind);
        Assert.Contains("2048", error.Message);
        Assert.False(File.Exists(install.ConfigFile));
        Assert.Equal(3, install.Hana.Tables.Count);

        install.Hana.FailWith = _ => null;
        var retry = await setup.SaveAsync(TestInstallation.Request(), Ct);
        Assert.Equal(["BRAI_NEX_Envelope", "BRAI_NEX_CaptureLog", "BRAI_NEX_CompanyCertificate"], retry.CreatedTables);
        Assert.True(retry.Saved);
    }

    [Fact]
    public async Task Falha_ao_salvar_preserva_a_configuracao_valida_anterior()
    {
        using var install = new TestInstallation();
        var setup = install.Start();
        await setup.SaveAsync(TestInstallation.Request(), Ct);
        var before = File.ReadAllBytes(install.ConfigFile);

        await Assert.ThrowsAsync<NexFiscalException>(() => setup.SaveAsync(TestInstallation.Request(password: "errada"), Ct));

        Assert.Equal(before, File.ReadAllBytes(install.ConfigFile));
        Assert.Equal("ready", (await setup.StatusAsync(Ct)).State);
    }

    // ---------- Status e reinício (H09, H11, H12) ----------

    [Fact]
    public async Task Status_sem_configuracao()
    {
        using var install = new TestInstallation();
        var status = await install.Start().StatusAsync(Ct);
        Assert.Equal(("not_configured", false, false), (status.State, status.Configured, status.Connected));
        Assert.Equal(0, install.Hana.OpenCount);
    }

    [Fact]
    public async Task Apos_reiniciar_reconhece_a_conexao_salva_e_nunca_devolve_a_senha()
    {
        using var install = new TestInstallation();
        await install.Start().SaveAsync(TestInstallation.Request(), Ct);

        var status = await install.Start().StatusAsync(Ct); // nova instância = processo reiniciado

        Assert.Equal(("ready", true, true, true), (status.State, status.Configured, status.Connected, status.SchemaReady));
        Assert.Equal(("hana.teste.local", 30015, FakeHanaServer.User), (status.Server, status.Port, status.Username));
        var json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain(FakeHanaServer.Password, json);
        Assert.DoesNotContain("assword", json);
    }

    [Fact]
    public async Task Status_configurado_mas_hana_indisponivel()
    {
        using var install = new TestInstallation();
        await install.Start().SaveAsync(TestInstallation.Request(), Ct);
        install.Hana.Online = false;

        var status = await install.Start().StatusAsync(Ct);

        Assert.Equal(("unavailable", true, false), (status.State, status.Configured, status.Connected));
        Assert.True(File.Exists(install.ConfigFile));
    }

    [Fact]
    public async Task Status_configurado_mas_estrutura_removida_no_banco()
    {
        using var install = new TestInstallation();
        await install.Start().SaveAsync(TestInstallation.Request(), Ct);
        install.Hana.Tables.Remove("BRAI_NEX_Envelope");

        var status = await install.Start().StatusAsync(Ct);

        Assert.Equal(("schema_pending", true, true, false), (status.State, status.Configured, status.Connected, status.SchemaReady));
        Assert.Contains("Tabela ausente: BRAI_NEX_Envelope", status.Issues!);
    }

    [Theory]
    [InlineData(false)] // chaves perdidas / outro usuário Windows: não descriptografa
    [InlineData(true)]  // arquivo corrompido
    public async Task Configuracao_ilegivel_e_informada_e_nunca_sobrescrita_sem_confirmacao(bool corrupt)
    {
        using var install = new TestInstallation();
        await install.Start().SaveAsync(TestInstallation.Request(), Ct);
        if (corrupt) File.WriteAllText(install.ConfigFile, "conteudo-corrompido"); else install.LoseKeys();
        var original = File.ReadAllBytes(install.ConfigFile);
        var opensBefore = install.Hana.OpenCount;
        var setup = install.Start();

        var status = await setup.StatusAsync(Ct);
        var save = await Assert.ThrowsAsync<NexFiscalException>(() => setup.SaveAsync(TestInstallation.Request(), Ct));

        Assert.Equal(("unreadable", true, false), (status.State, status.Configured, status.Connected));
        Assert.Equal((409, "stored_config_unreadable"), (save.StatusCode, save.Kind));
        Assert.Equal(opensBefore, install.Hana.OpenCount);
        Assert.Equal(original, File.ReadAllBytes(install.ConfigFile));

        // Testar continua permitido e não escreve nada.
        await setup.TestAsync(TestInstallation.Request(), Ct);
        Assert.Equal(original, File.ReadAllBytes(install.ConfigFile));
    }

    [Fact]
    public async Task Substituir_configuracao_ilegivel_exige_confirmacao_e_preserva_backup()
    {
        using var install = new TestInstallation();
        await install.Start().SaveAsync(TestInstallation.Request(), Ct);
        install.LoseKeys();
        var original = File.ReadAllBytes(install.ConfigFile);

        var result = await install.Start().SaveAsync(TestInstallation.Request(replace: true), Ct);

        Assert.True(result.Saved);
        var backup = Assert.Single(Directory.GetFiles(install.Root, ".nexfiscal-hana.dat.ilegivel-*.bak"));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Contains(backup, result.Message);
        Assert.Equal("ready", (await install.Start().StatusAsync(Ct)).State);
    }

    [Fact]
    public async Task Confirmacao_de_substituicao_nao_destroi_nada_se_o_hana_falhar()
    {
        using var install = new TestInstallation();
        await install.Start().SaveAsync(TestInstallation.Request(), Ct);
        install.LoseKeys();
        var original = File.ReadAllBytes(install.ConfigFile);
        install.Hana.Online = false;

        await Assert.ThrowsAsync<NexFiscalException>(() => install.Start().SaveAsync(TestInstallation.Request(replace: true), Ct));

        Assert.Equal(original, File.ReadAllBytes(install.ConfigFile));
        Assert.Empty(Directory.GetFiles(install.Root, "*.bak"));
    }

    // ---------- Atualização de estrutura entre etapas ----------

    private static void DowngradeToStage1(TestInstallation install)
    {
        install.Hana.Tables.Remove("BRAI_NEX_CompanyCertificate");
        install.Hana.Versions.Remove(2);
        install.Hana.Executed.Clear();
    }

    [Fact]
    public async Task Instalacao_da_etapa_1_pede_atualizacao_e_cria_so_a_tabela_nova_sem_redigitar_senha()
    {
        using var install = new TestInstallation();
        await install.Start().SaveAsync(TestInstallation.Request(), Ct);
        DowngradeToStage1(install);
        var setup = install.Start();

        var before = await setup.StatusAsync(Ct);
        Assert.Equal(("schema_pending", true), (before.State, before.CanUpgrade));
        Assert.Empty(install.Hana.Writes);

        var upgrade = await setup.UpgradeAsync(Ct);

        Assert.Equal(["BRAI_NEX_CompanyCertificate"], upgrade.CreatedTables);
        Assert.Equal(2, install.Hana.Writes.Count()); // 1 tabela + 1 linha de versão
        Assert.Equal([1, 2], install.Hana.Versions);
        Assert.Equal("ready", (await setup.StatusAsync(Ct)).State);
        Assert.Empty((await setup.UpgradeAsync(Ct)).CreatedTables);
    }

    [Fact]
    public async Task Atualizacao_de_estrutura_sem_conexao_salva_ou_com_tabela_incompativel_e_recusada()
    {
        using var install = new TestInstallation();
        var none = await Assert.ThrowsAsync<NexFiscalException>(() => install.Start().UpgradeAsync(Ct));
        Assert.Equal("hana_not_configured", none.Kind);
        Assert.Equal(0, install.Hana.OpenCount);

        await install.Start().SaveAsync(TestInstallation.Request(), Ct);
        DowngradeToStage1(install);
        install.Hana.Tables["BRAI_NEX_Company"].RemoveAll(c => c.Column == "Cnpj");

        var status = await install.Start().StatusAsync(Ct);
        var blocked = await Assert.ThrowsAsync<NexFiscalException>(() => install.Start().UpgradeAsync(Ct));

        Assert.False(status.CanUpgrade);
        Assert.Equal("schema_incompatible", blocked.Kind);
        Assert.Empty(install.Hana.Writes);
    }

    // ---------- Garantias estruturais ----------

    [Fact]
    public void Migracoes_contem_apenas_criacoes_e_espelha_as_colunas_validadas()
    {
        var statements = HanaSchemaInstaller.CreateStatements;
        Assert.Equal(6, statements.Count);
        foreach (var (table, sql) in statements)
        {
            Assert.DoesNotMatch(new Regex(@"\b(DROP|TRUNCATE|ALTER|DELETE|UPDATE)\b", RegexOptions.IgnoreCase), sql);
            var columns = Regex.Matches(sql, "^\\s+\"(\\w+)\"\\s+([A-Z]+)", RegexOptions.Multiline)
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToArray();
            Assert.Equal(HanaSchemaVerifier.Required[table], columns);
        }
        // Empresa precisa existir antes das tabelas que a referenciam.
        var order = statements.Select(s => s.Table).ToList();
        Assert.True(order.IndexOf("BRAI_NEX_Company") < order.IndexOf("BRAI_NEX_CaptureCursor"));
        Assert.True(order.IndexOf("BRAI_NEX_Company") < order.IndexOf("BRAI_NEX_Envelope"));
        Assert.True(order.IndexOf("BRAI_NEX_Company") < order.IndexOf("BRAI_NEX_CompanyCertificate"));
    }

    [Fact]
    public void Registros_de_credenciais_nao_imprimem_a_senha()
    {
        var request = TestInstallation.Request();
        Assert.DoesNotContain(FakeHanaServer.Password, request.ToString());
        Assert.DoesNotContain(FakeHanaServer.Password, HanaCredentials.From(request).ToString());
    }

    [Theory]
    [InlineData(10, "invalid_credentials")]
    [InlineData(414, "password_change_required")]
    [InlineData(416, "user_locked")]
    [InlineData(258, "insufficient_privilege")]
    [InlineData(386, "schema_not_accessible")]
    [InlineData(288, "table_not_accessible")]
    [InlineData(4321, "tls_required")]
    [InlineData(-10807, "unreachable")]
    [InlineData(99999, "hana_error")]
    public void Codigos_do_hana_viram_mensagens_especificas_sem_texto_do_driver(int code, string kind)
    {
        var credentials = HanaCredentials.From(TestInstallation.Request());
        var failure = HanaFailure.Translate(new FakeHanaException(code, "TEXTO-INTERNO-DO-DRIVER"), credentials, saving: true);
        Assert.Equal(kind, failure.Kind);
        Assert.DoesNotContain("TEXTO-INTERNO-DO-DRIVER", failure.Message);
        Assert.DoesNotContain(FakeHanaServer.Password, failure.Message);
    }

    [Theory]
    [InlineData("Connection failed (RTE:[89001] Cannot resolve host name 'x' rc=-2)", "host_not_found")]
    [InlineData("Connection failed (RTE:[89006] System call 'connect' failed, rc=111:Connection refused)", "connection_refused")]
    [InlineData("Connection failed (RTE:[89013] Socket closed by peer; timeout expired)", "connection_timeout")]
    [InlineData("Connection failed (outro motivo)", "unreachable")]
    public void Falhas_de_rede_sao_diferenciadas(string driverMessage, string kind)
    {
        var failure = HanaFailure.Translate(new FakeHanaException(-10709, driverMessage),
            HanaCredentials.From(TestInstallation.Request()), saving: false);
        Assert.Equal((503, kind), (failure.StatusCode, failure.Kind));
    }
}
