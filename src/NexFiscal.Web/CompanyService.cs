namespace NexFiscal.Web;

/// <summary>Arquivo A1 enviado pela tela. O conteúdo vem em Base64 dentro de JSON.</summary>
public sealed record CertificateUpload(string? PfxBase64, string? Password, bool ReplaceUnreadable = false)
{
    public override string ToString() => "CertificateUpload(conteúdo oculto)";
}

/// <summary>Prévia do certificado antes de gravar qualquer coisa.</summary>
/// <param name="ExistingCompanyId">Preenchido quando o CNPJ já está cadastrado.</param>
public sealed record CertificatePreview(
    string Cnpj, string CnpjFormatted, string LegalName, string? Uf, string Issuer,
    DateTime NotBeforeUtc, DateTime NotAfterUtc, int DaysRemaining,
    string? ExistingCompanyId, bool ExistingArchived, string Message);

/// <summary>Empresa na lista. Nunca contém PFX, senha ou chave.</summary>
/// <param name="CertificateStatus">stored | missing | unreadable | expired | invalid</param>
public sealed record CompanyView(
    string CompanyId, string Cnpj, string CnpjFormatted, string LegalName, string? Uf, string Environment,
    bool Archived, DateTime CreatedAtUtc,
    string CertificateStatus, string CertificateMessage, string? Issuer,
    DateTime? NotBeforeUtc, DateTime? NotAfterUtc, int? DaysRemaining, bool HasFiscalHistory);

public sealed record CompanyChange(string Outcome, string Message, CompanyView? Company);

/// <summary>
/// Função 2 da V1: cadastro de empresas pelo certificado A1.
/// O HANA guarda o cadastro; o cofre local guarda o A1 cifrado. Não consulta a SEFAZ.
/// </summary>
public sealed class CompanyService(
    ICompanyStore store, CertificateVault vault, TimeProvider clock, ILogger<CompanyService> logger)
{
    /// <summary>A V1 captura documentos reais: toda empresa é cadastrada no ambiente de produção da NF-e.</summary>
    public const int ProductionEnvironment = 1;

    private static readonly SemaphoreSlim ChangeLock = new(1, 1);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<CompanyView>> ListAsync(CancellationToken ct)
    {
        return (await store.ListAsync(ct)).Select(View).ToList();
    }

    /// <summary>Somente leitura: abre o certificado e mostra o que seria cadastrado.</summary>
    public async Task<CertificatePreview> InspectAsync(CertificateUpload? upload, CancellationToken ct)
    {
        var (_, _, info) = Open(upload);
        var existing = (await store.ListAsync(ct))
            .FirstOrDefault(c => c.Cnpj == info.Cnpj && c.Environment == ProductionEnvironment);
        var message = existing is null
            ? "Certificado válido. Confira os dados e confirme o cadastro."
            : existing.ArchivedAtUtc is not null
                ? "Este CNPJ já está cadastrado e arquivado. Use \"Reativar com A1\" na lista de empresas."
                : "Este CNPJ já está cadastrado. Para trocar o certificado, use \"Atualizar A1\" na lista de empresas.";
        return new CertificatePreview(info.Cnpj, Cnpj.Format(info.Cnpj), info.LegalName, info.Uf, info.Issuer,
            info.NotBeforeUtc, info.NotAfterUtc, DaysRemaining(info.NotAfterUtc),
            existing?.CompanyId, existing?.ArchivedAtUtc is not null, message);
    }

    public async Task<CompanyChange> RegisterAsync(CertificateUpload? upload, CancellationToken ct)
    {
        var (pfx, password, info) = Open(upload);
        await ChangeLock.WaitAsync(ct);
        try
        {
            if ((await store.ListAsync(ct)).Any(c => c.Cnpj == info.Cnpj && c.Environment == ProductionEnvironment))
                throw new NexFiscalException("duplicate_company",
                    $"O CNPJ {Cnpj.Format(info.Cnpj)} já está cadastrado. Para trocar o certificado, use \"Atualizar A1\" na lista de empresas. Nada foi alterado.", 409);

            var now = Now;
            var company = new CompanyRecord(Guid.NewGuid().ToString(), info.Cnpj, info.LegalName, ProductionEnvironment, now,
                info.Uf, info.Issuer, info.Thumbprint, info.NotBeforeUtc, info.NotAfterUtc, now, null);

            // Cofre primeiro; se o HANA recusar, o arquivo recém-criado é removido (nunca fica cadastro sem A1).
            var write = await vault.WriteAsync(company.CompanyId, pfx, password, ct);
            try { await store.InsertAsync(company, ct); }
            catch
            {
                vault.Rollback(write);
                throw;
            }

            var view = ConfirmStored(company, info);
            logger.LogInformation("Empresa {CompanyId} cadastrada.", company.CompanyId);
            return new CompanyChange("registered", $"Empresa {info.LegalName} cadastrada com o certificado A1 protegido.", view);
        }
        finally { ChangeLock.Release(); }
    }

    /// <summary>Troca o A1 de uma empresa existente (ou reativa uma arquivada). Exige o mesmo CNPJ.</summary>
    public async Task<CompanyChange> UpdateCertificateAsync(string companyId, CertificateUpload? upload, CancellationToken ct)
    {
        var (pfx, password, info) = Open(upload);
        await ChangeLock.WaitAsync(ct);
        try
        {
            var company = await FindAsync(companyId, ct);
            if (company.Cnpj != info.Cnpj)
                throw new NexFiscalException("cnpj_mismatch",
                    $"Este certificado é do CNPJ {Cnpj.Format(info.Cnpj)}, diferente do CNPJ da empresa ({Cnpj.Format(company.Cnpj)}). Nada foi alterado.", 409);

            if (vault.Read(companyId).State == VaultState.Unreadable && !upload!.ReplaceUnreadable)
                throw new NexFiscalException("stored_certificate_unreadable",
                    "O certificado guardado desta empresa não pôde ser descriptografado. Ele não será substituído sem a sua confirmação explícita.", 409);

            var wasArchived = company.ArchivedAtUtc is not null;
            var write = await vault.WriteAsync(companyId, pfx, password, ct);
            try { await store.SetCertificateAsync(companyId, info, Now, ct); }
            catch
            {
                vault.Rollback(write); // o certificado anterior volta ao lugar
                throw;
            }

            var view = ConfirmStored(company with { ArchivedAtUtc = null }, info);
            logger.LogInformation("Certificado da empresa {CompanyId} atualizado.", companyId);
            var message = (wasArchived ? "Empresa reativada com o novo certificado A1." : "Certificado A1 atualizado.") +
                " Cadastro, NSUs e histórico foram preservados." +
                (write.Backup is not null ? " O arquivo anterior foi mantido como backup." : "");
            return new CompanyChange(wasArchived ? "reactivated" : "updated", message, view);
        }
        finally { ChangeLock.Release(); }
    }

    /// <summary>Sem histórico fiscal: exclui cadastro e A1. Com histórico: apenas arquiva, preservando tudo.</summary>
    public async Task<CompanyChange> RemoveAsync(string companyId, CancellationToken ct)
    {
        await ChangeLock.WaitAsync(ct);
        try
        {
            var company = await FindAsync(companyId, ct);
            if (await store.DeleteIfNoHistoryAsync(companyId, ct))
            {
                vault.Delete(companyId);
                logger.LogInformation("Empresa {CompanyId} removida (sem histórico fiscal).", companyId);
                return new CompanyChange("removed", $"Empresa {company.LegalName} removida. Não havia histórico fiscal.", null);
            }

            if (company.ArchivedAtUtc is null) await store.ArchiveAsync(companyId, Now, ct);
            logger.LogInformation("Empresa {CompanyId} arquivada (possui histórico fiscal).", companyId);
            var archived = View(company with { ArchivedAtUtc = company.ArchivedAtUtc ?? Now });
            return new CompanyChange("archived",
                $"Empresa {company.LegalName} arquivada. Como há histórico fiscal, documentos, NSUs e o certificado guardado foram preservados.", archived);
        }
        finally { ChangeLock.Release(); }
    }

    // ---------- apoio ----------

    private (byte[] Pfx, string Password, CertificateInfo Info) Open(CertificateUpload? upload)
    {
        if (upload is null || string.IsNullOrWhiteSpace(upload.PfxBase64))
            throw new NexFiscalException("invalid_input", "Selecione o arquivo do certificado A1 (.pfx ou .p12).", 400);
        if (string.IsNullOrEmpty(upload.Password) || upload.Password.Length > 1024)
            throw new NexFiscalException("invalid_input", "Informe a senha do certificado.", 400);
        if (upload.PfxBase64.Length > CertificateReader.MaxPfxBytes * 4 / 3 + 8)
            throw new NexFiscalException("invalid_file", "Arquivo grande demais para um certificado A1.", 400);

        byte[] pfx;
        try { pfx = Convert.FromBase64String(upload.PfxBase64); }
        catch (FormatException)
        {
            throw new NexFiscalException("invalid_file", "O arquivo do certificado chegou corrompido. Selecione-o novamente.", 400);
        }

        var info = CertificateReader.Read(pfx, upload.Password);
        var now = Now;
        if (info.NotAfterUtc <= now)
            throw new NexFiscalException("certificate_expired",
                $"Este certificado venceu em {Day(info.NotAfterUtc)}. Envie um certificado A1 dentro da validade.", 400);
        if (info.NotBeforeUtc > now)
            throw new NexFiscalException("certificate_not_yet_valid",
                $"Este certificado só passa a valer em {Day(info.NotBeforeUtc)}. Confira também a data e hora deste computador.", 400);
        return (pfx, upload.Password, info);
    }

    private async Task<CompanyRecord> FindAsync(string companyId, CancellationToken ct) =>
        (await store.ListAsync(ct)).FirstOrDefault(c => c.CompanyId == companyId)
        ?? throw new NexFiscalException("company_not_found", "Empresa não encontrada. Atualize a lista.", 404);

    /// <summary>Confirma relendo o cofre: só é sucesso se o A1 gravado abre e é o mesmo certificado.</summary>
    private CompanyView ConfirmStored(CompanyRecord company, CertificateInfo expected)
    {
        var view = View(company with
        {
            Uf = expected.Uf, Issuer = expected.Issuer, Thumbprint = expected.Thumbprint,
            NotBeforeUtc = expected.NotBeforeUtc, NotAfterUtc = expected.NotAfterUtc
        });
        var (state, entry) = vault.Read(company.CompanyId);
        var confirmed = state == VaultState.Readable && view.CertificateStatus == "stored" &&
            CertificateReader.Read(Convert.FromBase64String(entry!.PfxBase64), entry.Password).Thumbprint == expected.Thumbprint;
        if (!confirmed)
            throw new NexFiscalException("vault_verification_failed",
                "O cadastro foi gravado, mas o certificado guardado não pôde ser relido. Use \"Atualizar A1\" para enviá-lo novamente.", 500);
        return view;
    }

    /// <summary>Estado real do A1: decidido abrindo o arquivo cifrado, nunca pela mera existência dele ou do cadastro.</summary>
    private CompanyView View(CompanyRecord company)
    {
        string status, message;
        CertificateInfo? live = null;
        VaultState state;
        VaultEntry? entry = null;
        try { (state, entry) = vault.Read(company.CompanyId); }
        catch (NexFiscalException) { state = VaultState.Missing; } // identificador legado fora do padrão

        if (state == VaultState.Missing)
            (status, message) = ("missing", "Certificado A1 não encontrado neste computador. Use \"Atualizar A1\" para enviá-lo.");
        else if (state == VaultState.Unreadable)
            (status, message) = ("unreadable",
                "O certificado está guardado, mas não pôde ser descriptografado (outro usuário do Windows ou pasta .nexfiscal-keys ausente). Nada foi alterado.");
        else
        {
            try
            {
                live = CertificateReader.Read(Convert.FromBase64String(entry!.PfxBase64), entry.Password);
                if (live.Cnpj != company.Cnpj)
                    (status, message) = ("invalid", "O certificado guardado pertence a outro CNPJ. Envie o A1 correto em \"Atualizar A1\".");
                else if (live.NotAfterUtc <= Now)
                    (status, message) = ("expired", $"Certificado vencido em {Day(live.NotAfterUtc)}. Envie o A1 renovado em \"Atualizar A1\".");
                else if (live.NotBeforeUtc > Now)
                    (status, message) = ("invalid", $"Certificado ainda não é válido (início em {Day(live.NotBeforeUtc)}).");
                else
                {
                    var days = DaysRemaining(live.NotAfterUtc);
                    (status, message) = ("stored", days <= 30
                        ? $"Certificado válido até {Day(live.NotAfterUtc)} — vence em {days} dia(s)."
                        : $"Certificado válido até {Day(live.NotAfterUtc)}.");
                }
            }
            catch (Exception ex) when (ex is NexFiscalException or FormatException)
            {
                (status, message) = ("invalid", "O certificado guardado está danificado. Envie o A1 novamente em \"Atualizar A1\".");
            }
        }

        var notAfter = live?.NotAfterUtc ?? company.NotAfterUtc;
        return new CompanyView(company.CompanyId, company.Cnpj, Cnpj.Format(company.Cnpj), company.LegalName,
            live?.Uf ?? company.Uf, company.Environment == ProductionEnvironment ? "Produção" : "Homologação",
            company.ArchivedAtUtc is not null, company.CreatedAtUtc, status, message, live?.Issuer ?? company.Issuer,
            live?.NotBeforeUtc ?? company.NotBeforeUtc, notAfter,
            notAfter is null ? null : DaysRemaining(notAfter.Value),
            company.HasFiscalHistory);
    }

    private int DaysRemaining(DateTime notAfterUtc) => (int)Math.Floor((notAfterUtc - Now).TotalDays);

    private static string Day(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy");
}
