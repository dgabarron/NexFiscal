using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexFiscal.Web;
using static NexFiscal.Tests.TestCertificates;

namespace NexFiscal.Tests;

public class CompanyServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ---------- CNPJ ----------

    [Theory]
    [InlineData("11222333000181", true)]
    [InlineData("11444777000161", true)]
    [InlineData("12ABC34501DE35", true)]  // exemplo oficial do CNPJ alfanumérico
    [InlineData("11222333000182", false)]
    [InlineData("12ABC34501DE36", false)]
    [InlineData("00000000000000", false)]
    [InlineData("1122233300018", false)]
    [InlineData("11.222.333/0001-81", false)]
    [InlineData(null, false)]
    public void Validacao_de_cnpj(string? cnpj, bool valid) => Assert.Equal(valid, Cnpj.IsValid(cnpj));

    [Fact]
    public void Formatacao_de_cnpj() => Assert.Equal("11.222.333/0001-81", Cnpj.Format(CnpjA));

    // ---------- C01: inspeção ----------

    [Fact]
    public async Task Inspecionar_extrai_dados_do_certificado_e_nao_grava_nada()
    {
        using var install = new CompanyInstallation();
        var preview = await install.Start().InspectAsync(Upload(), Ct);

        Assert.Equal((CnpjA, "11.222.333/0001-81", "EMPRESA TESTE LTDA", "SP"), (preview.Cnpj, preview.CnpjFormatted, preview.LegalName, preview.Uf));
        Assert.Equal("EMPRESA TESTE LTDA:" + CnpjA, preview.Issuer); // autoassinado: emissor = titular
        Assert.Equal(Today.AddDays(335).UtcDateTime, preview.NotAfterUtc);
        Assert.Equal(DateTimeKind.Utc, preview.NotAfterUtc.Kind);
        Assert.Equal(335, preview.DaysRemaining);
        Assert.Null(preview.ExistingCompanyId);
        Assert.Empty(install.VaultFiles());
        Assert.Empty(install.Store.Rows);
        Assert.Equal(0, install.Store.Writes);
    }

    [Fact]
    public async Task Uf_ausente_ou_fora_do_padrao_fica_nao_identificada()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        Assert.Null((await service.InspectAsync(Upload(Create(state: null)), Ct)).Uf);
        Assert.Null((await service.InspectAsync(Upload(Create(state: "Sao Paulo")), Ct)).Uf);
    }

    [Fact]
    public async Task Cnpj_do_san_prevalece_e_o_do_nome_serve_de_reserva()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var sanWins = await service.InspectAsync(Upload(Create(sanCnpj: CnpjB, commonName: "OUTRA LTDA:" + CnpjA)), Ct);
        var fallback = await service.InspectAsync(Upload(Create(sanCnpj: null, commonName: "SO NOME LTDA:" + CnpjA)), Ct);
        Assert.Equal(CnpjB, sanWins.Cnpj);
        Assert.Equal((CnpjA, "SO NOME LTDA"), (fallback.Cnpj, fallback.LegalName));
    }

    // ---------- C02: arquivo ou senha inválidos ----------

    public static TheoryData<string, string?, string, string> InvalidUploads() => new()
    {
        { Convert.ToBase64String(Create()), "senha-errada", "invalid_password", "Senha do certificado incorreta" },
        { Convert.ToBase64String(Encoding.ASCII.GetBytes("isto nao e um pfx")), Password, "invalid_file", "não é um certificado A1 válido" },
        { "@@@nao-base64@@@", Password, "invalid_file", "corrompido" },
        { "", Password, "invalid_input", "Selecione o arquivo" },
        { Convert.ToBase64String(Create()), "", "invalid_input", "Informe a senha" },
        { Convert.ToBase64String(Create(withPrivateKey: false)), Password, "no_private_key", "chave privada" },
        { Convert.ToBase64String(Create(sanCnpj: null, commonName: "FULANO DE TAL:12345678909")), Password, "no_cnpj", "CNPJ" },
        { Convert.ToBase64String(Create(sanCnpj: "11222333000199", commonName: "DIGITO ERRADO LTDA")), Password, "no_cnpj", "CNPJ" },
        { Convert.ToBase64String(Create(notBefore: Today.AddDays(-400), notAfter: Today.AddDays(-1))), Password, "certificate_expired", "venceu em" },
        { Convert.ToBase64String(Create(notBefore: Today.AddDays(2))), Password, "certificate_not_yet_valid", "só passa a valer" },
        { Convert.ToBase64String(new byte[CertificateReader.MaxPfxBytes + 1]), Password, "invalid_file", "grande demais" },
    };

    [Theory]
    [MemberData(nameof(InvalidUploads))]
    public async Task Certificado_invalido_gera_erro_legivel_sem_cadastro_parcial(string base64, string? password, string kind, string text)
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var upload = new CertificateUpload(base64, password);

        var inspect = await Assert.ThrowsAsync<NexFiscalException>(() => service.InspectAsync(upload, Ct));
        var register = await Assert.ThrowsAsync<NexFiscalException>(() => service.RegisterAsync(upload, Ct));

        Assert.Equal((400, kind), (inspect.StatusCode, inspect.Kind));
        Assert.Contains(text, inspect.Message);
        Assert.Equal(kind, register.Kind);
        Assert.Empty(install.VaultFiles());
        Assert.Empty(install.Store.Rows);
    }

    // ---------- Cadastro ----------

    [Fact]
    public async Task Cadastrar_grava_empresa_e_a1_cifrado_e_confirma_relendo()
    {
        using var install = new CompanyInstallation();
        var pfx = Create();
        var result = await install.Start().RegisterAsync(Upload(pfx), Ct);

        Assert.Equal("registered", result.Outcome);
        var view = result.Company!;
        Assert.Equal((CnpjA, "EMPRESA TESTE LTDA", "SP", "Produção", "stored", false),
            (view.Cnpj, view.LegalName, view.Uf, view.Environment, view.CertificateStatus, view.Archived));
        var row = Assert.Single(install.Store.Rows);
        Assert.Equal((view.CompanyId, CnpjA, CompanyService.ProductionEnvironment), (row.CompanyId, row.Cnpj, row.Environment));
        Assert.True(Guid.TryParse(row.CompanyId, out _));

        var file = Assert.Single(install.VaultFiles());
        Assert.Equal(install.VaultFile(view.CompanyId), file);
        var stored = File.ReadAllText(file);
        Assert.DoesNotContain(Convert.ToBase64String(pfx)[..40], stored);
        Assert.DoesNotContain(Password, stored);
        Assert.DoesNotContain(CnpjA, stored);
        Assert.DoesNotContain(install.Logs.Lines, line => line.Contains(Password));

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("assword", json);
        Assert.DoesNotContain("Pfx", json);
    }

    [Fact]
    public async Task Varias_empresas_podem_ser_cadastradas()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        await service.RegisterAsync(Upload(), Ct);
        await service.RegisterAsync(Upload(Create(sanCnpj: CnpjB, commonName: "SEGUNDA EMPRESA SA:" + CnpjB, state: "RJ")), Ct);

        var list = await service.ListAsync(Ct);
        Assert.Equal([CnpjA, CnpjB], list.Select(c => c.Cnpj).Order());
        Assert.All(list, c => Assert.Equal("stored", c.CertificateStatus));
        Assert.Equal(2, install.VaultFiles().Length);
    }

    // ---------- C03: duplicidade ----------

    [Fact]
    public async Task Cnpj_duplicado_e_recusado_sem_tocar_no_certificado_existente()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var first = (await service.RegisterAsync(Upload(), Ct)).Company!;
        var before = File.ReadAllBytes(install.VaultFile(first.CompanyId));

        var preview = await service.InspectAsync(Upload(Create()), Ct);
        var error = await Assert.ThrowsAsync<NexFiscalException>(() => service.RegisterAsync(Upload(Create()), Ct));

        Assert.Equal(first.CompanyId, preview.ExistingCompanyId);
        Assert.Contains("Atualizar A1", preview.Message);
        Assert.Equal((409, "duplicate_company"), (error.StatusCode, error.Kind));
        Assert.Single(install.Store.Rows);
        Assert.Single(install.VaultFiles());
        Assert.Equal(before, File.ReadAllBytes(install.VaultFile(first.CompanyId)));
    }

    [Fact]
    public async Task Falha_do_hana_no_cadastro_nao_deixa_certificado_orfao()
    {
        using var install = new CompanyInstallation();
        install.Store.FailWrites = true;

        var error = await Assert.ThrowsAsync<NexFiscalException>(() => install.Start().RegisterAsync(Upload(), Ct));

        Assert.Equal("hana_error", error.Kind);
        Assert.Empty(install.Store.Rows);
        Assert.Empty(install.VaultFiles());
    }

    [Fact]
    public async Task Sem_hana_configurado_nada_e_gravado()
    {
        using var install = new CompanyInstallation();
        install.Store.Unavailable = true;
        var service = install.Start();

        var list = await Assert.ThrowsAsync<NexFiscalException>(() => service.ListAsync(Ct));
        var register = await Assert.ThrowsAsync<NexFiscalException>(() => service.RegisterAsync(Upload(), Ct));

        Assert.Equal((409, "hana_not_configured"), (list.StatusCode, list.Kind));
        Assert.Equal("hana_not_configured", register.Kind);
        Assert.Empty(install.VaultFiles());
    }

    // ---------- C04–C06: estado real do A1 ----------

    [Fact]
    public async Task Apos_reiniciar_empresa_e_certificado_continuam_acessiveis()
    {
        using var install = new CompanyInstallation();
        await install.Start().RegisterAsync(Upload(), Ct);

        var company = Assert.Single(await install.Start().ListAsync(Ct)); // nova instância = processo reiniciado

        Assert.Equal("stored", company.CertificateStatus);
        Assert.Contains("válido até", company.CertificateMessage);
        Assert.Equal(335, company.DaysRemaining);
    }

    [Fact]
    public async Task Arquivo_presente_mas_nao_descriptografavel_e_ilegivel_e_nao_ausente()
    {
        using var install = new CompanyInstallation();
        var id = (await install.Start().RegisterAsync(Upload(), Ct)).Company!.CompanyId;
        install.LoseKeys();
        var original = File.ReadAllBytes(install.VaultFile(id));

        var company = Assert.Single(await install.Start().ListAsync(Ct));

        Assert.Equal("unreadable", company.CertificateStatus);
        Assert.Equal("11.222.333/0001-81", company.CnpjFormatted);
        Assert.NotNull(company.NotAfterUtc); // dados do cadastro continuam visíveis
        Assert.Equal(original, File.ReadAllBytes(install.VaultFile(id)));
    }

    [Fact]
    public async Task Arquivo_ausente_e_arquivo_danificado_sao_estados_distintos()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var id = (await service.RegisterAsync(Upload(), Ct)).Company!.CompanyId;

        File.Delete(install.VaultFile(id));
        Assert.Equal("missing", Assert.Single(await service.ListAsync(Ct)).CertificateStatus);

        File.WriteAllText(install.VaultFile(id), "lixo");
        Assert.Equal("unreadable", Assert.Single(await service.ListAsync(Ct)).CertificateStatus);
    }

    [Fact]
    public async Task Certificado_que_vence_com_o_tempo_passa_a_vencido_e_avisa_antes()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        await service.RegisterAsync(Upload(), Ct);

        install.Clock.Now = Today.AddDays(320);
        var near = Assert.Single(await service.ListAsync(Ct));
        Assert.Equal(("stored", 15), (near.CertificateStatus, near.DaysRemaining));
        Assert.Contains("vence em 15 dia(s)", near.CertificateMessage);

        install.Clock.Now = Today.AddDays(336);
        var expired = Assert.Single(await service.ListAsync(Ct));
        Assert.Equal("expired", expired.CertificateStatus);
        Assert.Contains("vencido", expired.CertificateMessage);
    }

    // ---------- C07–C08: atualização ----------

    [Fact]
    public async Task Atualizar_a1_do_mesmo_cnpj_preserva_cadastro_e_mantem_backup_do_anterior()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var original = (await service.RegisterAsync(Upload(), Ct)).Company!;
        install.Store.History.Add(original.CompanyId);
        var oldFile = File.ReadAllBytes(install.VaultFile(original.CompanyId));
        var oldThumbprint = install.Store.Rows[0].Thumbprint;
        install.Clock.Now = Today.AddDays(300);

        var renewed = Create(notBefore: Today.AddDays(290), notAfter: Today.AddDays(655));
        var result = await service.UpdateCertificateAsync(original.CompanyId, Upload(renewed), Ct);

        Assert.Equal("updated", result.Outcome);
        Assert.Contains("NSUs e histórico foram preservados", result.Message);
        var row = Assert.Single(install.Store.Rows);
        Assert.Equal((original.CompanyId, original.Cnpj, original.LegalName, original.CreatedAtUtc), (row.CompanyId, row.Cnpj, row.LegalName, row.CreatedAtUtc));
        Assert.NotEqual(oldThumbprint, row.Thumbprint);
        Assert.Equal(Today.AddDays(655).UtcDateTime, result.Company!.NotAfterUtc);
        Assert.True(result.Company.HasFiscalHistory);

        var backup = Assert.Single(install.VaultFiles(), f => f.EndsWith(".bak"));
        Assert.Contains(".anterior-", backup);
        Assert.Equal(oldFile, File.ReadAllBytes(backup));
        Assert.Equal("stored", Assert.Single(await install.Start().ListAsync(Ct)).CertificateStatus);
    }

    [Fact]
    public async Task Atualizar_com_a1_de_outro_cnpj_e_rejeitado_sem_alterar_nada()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var id = (await service.RegisterAsync(Upload(), Ct)).Company!.CompanyId;
        var before = File.ReadAllBytes(install.VaultFile(id));
        var writes = install.Store.Writes;

        var other = Create(sanCnpj: CnpjB, commonName: "OUTRA EMPRESA LTDA:" + CnpjB);
        var error = await Assert.ThrowsAsync<NexFiscalException>(() => service.UpdateCertificateAsync(id, Upload(other), Ct));

        Assert.Equal((409, "cnpj_mismatch"), (error.StatusCode, error.Kind));
        Assert.Equal(before, File.ReadAllBytes(install.VaultFile(id)));
        Assert.Single(install.VaultFiles());
        Assert.Equal(writes, install.Store.Writes);
    }

    [Fact]
    public async Task Falha_do_hana_na_atualizacao_devolve_o_certificado_anterior_ao_lugar()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var id = (await service.RegisterAsync(Upload(), Ct)).Company!.CompanyId;
        var before = File.ReadAllBytes(install.VaultFile(id));
        install.Store.FailWrites = true;

        await Assert.ThrowsAsync<NexFiscalException>(() => service.UpdateCertificateAsync(id, Upload(Create()), Ct));

        Assert.Equal(before, File.ReadAllBytes(install.VaultFile(id)));
        Assert.Single(install.VaultFiles());
        install.Store.FailWrites = false;
        Assert.Equal("stored", Assert.Single(await service.ListAsync(Ct)).CertificateStatus);
    }

    [Fact]
    public async Task Certificado_ilegivel_so_e_substituido_com_confirmacao_e_fica_preservado()
    {
        using var install = new CompanyInstallation();
        var id = (await install.Start().RegisterAsync(Upload(), Ct)).Company!.CompanyId;
        install.LoseKeys();
        var unreadable = File.ReadAllBytes(install.VaultFile(id));
        var service = install.Start();

        var refused = await Assert.ThrowsAsync<NexFiscalException>(() => service.UpdateCertificateAsync(id, Upload(Create()), Ct));
        Assert.Equal((409, "stored_certificate_unreadable"), (refused.StatusCode, refused.Kind));
        Assert.Equal(unreadable, File.ReadAllBytes(install.VaultFile(id)));
        Assert.Single(install.VaultFiles());

        var replaced = await service.UpdateCertificateAsync(id, Upload(Create(), replace: true), Ct);
        Assert.Equal("stored", replaced.Company!.CertificateStatus);
        Assert.Equal(unreadable, File.ReadAllBytes(Assert.Single(install.VaultFiles(), f => f.EndsWith(".bak"))));
    }

    [Fact]
    public async Task Certificado_ausente_pode_ser_reenviado_pela_atualizacao()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var id = (await service.RegisterAsync(Upload(), Ct)).Company!.CompanyId;
        File.Delete(install.VaultFile(id));

        var result = await service.UpdateCertificateAsync(id, Upload(Create()), Ct);

        Assert.Equal("stored", result.Company!.CertificateStatus);
        Assert.Single(install.VaultFiles());
    }

    [Fact]
    public async Task Empresa_inexistente_retorna_nao_encontrada()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var update = await Assert.ThrowsAsync<NexFiscalException>(() => service.UpdateCertificateAsync(Guid.NewGuid().ToString(), Upload(), Ct));
        var remove = await Assert.ThrowsAsync<NexFiscalException>(() => service.RemoveAsync("../../etc", Ct));
        Assert.Equal(404, update.StatusCode);
        Assert.Equal(404, remove.StatusCode);
        Assert.Empty(install.VaultFiles());
    }

    // ---------- C09–C10: remoção ----------

    [Fact]
    public async Task Remover_sem_historico_exclui_cadastro_e_material_privado()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var keep = (await service.RegisterAsync(Upload(Create(sanCnpj: CnpjB, commonName: "FICA LTDA:" + CnpjB)), Ct)).Company!;
        var id = (await service.RegisterAsync(Upload(), Ct)).Company!.CompanyId;
        await service.UpdateCertificateAsync(id, Upload(Create()), Ct); // gera um backup, que também deve sair

        var result = await service.RemoveAsync(id, Ct);

        Assert.Equal("removed", result.Outcome);
        Assert.Equal(keep.CompanyId, Assert.Single(install.Store.Rows).CompanyId);
        Assert.Equal([install.VaultFile(keep.CompanyId)], install.VaultFiles());
    }

    [Fact]
    public async Task Remover_com_historico_apenas_arquiva_e_preserva_tudo()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var id = (await service.RegisterAsync(Upload(), Ct)).Company!.CompanyId;
        install.Store.History.Add(id);
        var file = File.ReadAllBytes(install.VaultFile(id));

        var result = await service.RemoveAsync(id, Ct);

        Assert.Equal("archived", result.Outcome);
        Assert.Contains("preservados", result.Message);
        var row = Assert.Single(install.Store.Rows);
        Assert.NotNull(row.ArchivedAtUtc);
        Assert.Equal(file, File.ReadAllBytes(install.VaultFile(id)));
        var listed = Assert.Single(await service.ListAsync(Ct));
        Assert.True(listed.Archived);
        Assert.True(listed.HasFiscalHistory);
    }

    [Fact]
    public async Task Cnpj_arquivado_nao_vira_empresa_nova_e_e_reativado_pelo_mesmo_cadastro()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var id = (await service.RegisterAsync(Upload(), Ct)).Company!.CompanyId;
        install.Store.History.Add(id);
        await service.RemoveAsync(id, Ct);

        var preview = await service.InspectAsync(Upload(Create()), Ct);
        var duplicate = await Assert.ThrowsAsync<NexFiscalException>(() => service.RegisterAsync(Upload(Create()), Ct));
        Assert.True(preview.ExistingArchived);
        Assert.Contains("Reativar com A1", preview.Message);
        Assert.Equal("duplicate_company", duplicate.Kind);

        var reactivated = await service.UpdateCertificateAsync(id, Upload(Create()), Ct);
        Assert.Equal("reactivated", reactivated.Outcome);
        Assert.False(reactivated.Company!.Archived);
        Assert.Equal(id, Assert.Single(install.Store.Rows).CompanyId);
        Assert.Null(install.Store.Rows[0].ArchivedAtUtc);
    }

    [Fact]
    public async Task Falha_do_hana_na_remocao_nao_apaga_o_certificado()
    {
        using var install = new CompanyInstallation();
        var service = install.Start();
        var id = (await service.RegisterAsync(Upload(), Ct)).Company!.CompanyId;
        install.Store.FailWrites = true;

        await Assert.ThrowsAsync<NexFiscalException>(() => service.RemoveAsync(id, Ct));

        Assert.Single(install.Store.Rows);
        Assert.Single(install.VaultFiles());
    }

    // ---------- SQL do HANA (sem HANA real: conferência estática) ----------

    [Fact]
    public void Sql_de_empresas_so_usa_tabelas_e_colunas_das_migracoes_e_nunca_comandos_destrutivos_amplos()
    {
        var known = HanaSchemaVerifier.Required.Keys
            .Concat(HanaSchemaVerifier.Required.Values.SelectMany(columns => columns.Select(c => c.Column)))
            .Append("BRAI_NEX").ToHashSet(StringComparer.Ordinal);
        var statements = typeof(HanaCompanyStore).Assembly.GetType("NexFiscal.Web.CompanySql")!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        Assert.True(statements.Count >= 12);

        foreach (var (name, sql) in statements)
        {
            foreach (Match identifier in Regex.Matches(sql, "\"(\\w+)\""))
                Assert.True(known.Contains(identifier.Groups[1].Value), $"{name}: identificador desconhecido {identifier.Value}");
            Assert.DoesNotMatch(new Regex(@"\b(DROP|TRUNCATE|ALTER|CREATE)\b", RegexOptions.IgnoreCase), sql);
            if (Regex.IsMatch(sql, @"^(DELETE|UPDATE)\b"))
                Assert.EndsWith("WHERE \"CompanyId\" = ?", sql);
        }
        // Só o cadastro da empresa pode ser excluído; nunca cursor, envelope ou log.
        Assert.Equal(["DeleteCertificate", "DeleteCompany"], statements.Where(s => s.Value.StartsWith("DELETE")).Select(s => s.Key).Order());
        Assert.DoesNotContain(statements.Values, sql => Regex.IsMatch(sql, "^(DELETE|UPDATE|INSERT).*(CaptureCursor|Envelope|CaptureLog)"));
        // Colunas de cada tabela conferidas contra a migração.
        foreach (var column in Regex.Matches(statements["InsertCertificate"], "\"(\\w+)\"").Skip(2).Select(m => m.Groups[1].Value))
            Assert.Contains(HanaSchemaVerifier.Required["BRAI_NEX_CompanyCertificate"], c => c.Column == column);
        foreach (var column in Regex.Matches(statements["InsertCompany"], "\"(\\w+)\"").Skip(2).Select(m => m.Groups[1].Value))
            Assert.Contains(HanaSchemaVerifier.Required["BRAI_NEX_Company"], c => c.Column == column);
    }

    [Fact]
    public void Registros_do_certificado_nao_imprimem_conteudo()
    {
        var upload = Upload();
        Assert.DoesNotContain(Password, upload.ToString());
        Assert.DoesNotContain(upload.PfxBase64![..20], upload.ToString());
        Assert.DoesNotContain(Password, new VaultEntry("AAAA", Password).ToString());
    }
}
