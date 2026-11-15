using System.Data.Common;
using Sap.Data.Hana;

namespace NexFiscal.Web;

/// <summary>Abre conexões com o SAP HANA. Interface existe para os testes automatizados usarem um banco simulado.</summary>
public interface IHanaConnectionFactory
{
    DbConnection Create(HanaCredentials credentials);
}

public sealed class HanaConnectionFactory : IHanaConnectionFactory
{
    public DbConnection Create(HanaCredentials credentials)
    {
        // O builder cuida do escape de caracteres especiais da senha. Sem pool: a senha não fica retida.
        var builder = new DbConnectionStringBuilder
        {
            ["Server"] = credentials.Server + ":" + credentials.Port,
            ["UID"] = credentials.Username,
            ["PWD"] = credentials.Password,
            ["Pooling"] = "False"
        };
        return new HanaConnection(builder.ConnectionString);
    }
}

/// <summary>
/// Função 1 da V1: testar, provisionar de forma não destrutiva, salvar protegido e informar o estado.
/// </summary>
public sealed class HanaSetup(
    HanaCredentialStore store, IHanaConnectionFactory connections, ILogger<HanaSetup> logger)
{
    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    /// <summary>Somente leitura: não cria objetos no HANA e não grava nada em disco.</summary>
    public async Task<HanaSetupResult> TestAsync(HanaSetupRequest? request, CancellationToken ct)
    {
        var credentials = HanaCredentials.From(request);
        try
        {
            await using var db = connections.Create(credentials);
            await db.OpenAsync(ct);
            var report = await HanaSchemaVerifier.VerifyAsync(db, ct);
            return ToResult(report, [], saved: false, report.State switch
            {
                "ready" => "Conexão válida. Estrutura BRAI_NEX existente e validada.",
                "pending" => $"Conexão válida; estrutura pendente. Ao salvar, o NexFiscal criará somente o que falta ({report.MissingTables.Count} tabela(s)). Nada foi criado neste teste.",
                _ => "Conexão válida, mas há tabela existente incompatível com o NexFiscal. Salvar ficará bloqueado até a correção; nenhuma tabela será alterada."
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw Fail(ex, credentials, saving: false); }
    }

    /// <summary>Cria apenas o que falta, valida e só então grava as credenciais cifradas.</summary>
    public async Task<HanaSetupResult> SaveAsync(HanaSetupRequest? request, CancellationToken ct)
    {
        var credentials = HanaCredentials.From(request);
        var replaceUnreadable = request!.ReplaceUnreadable;
        await SaveLock.WaitAsync(ct);
        try
        {
            // Recusar antes de tocar no HANA se há arquivo cifrado ilegível e não houve confirmação.
            if (store.Read().State == StoredConfigState.Unreadable && !replaceUnreadable)
                throw new HanaSetupException("stored_config_unreadable",
                    "Já existe uma configuração protegida que não pôde ser lida. Ela não será sobrescrita sem a sua confirmação explícita.", 409);

            HanaSchemaReport report;
            IReadOnlyList<string> created;
            try
            {
                await using var db = connections.Create(credentials);
                await db.OpenAsync(ct);
                (report, created) = await HanaSchemaInstaller.InstallAsync(db, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw Fail(ex, credentials, saving: true); }

            if (!report.Ready)
                throw new HanaSetupException("schema_incompatible",
                    "A estrutura BRAI_NEX existente não é compatível com o NexFiscal. A configuração não foi salva e nenhuma tabela foi alterada ou apagada. Corrija os itens listados com o administrador do banco.",
                    409, report.Issues);

            string? backup;
            try { backup = await store.WriteAsync(credentials, replaceUnreadable, ct); }
            catch (Exception ex) when (ex is not (OperationCanceledException or HanaSetupException))
            {
                logger.LogError("Falha ao gravar a configuração protegida: {Type}", ex.GetType().Name);
                throw new HanaSetupException("store_write_failed",
                    $"O SAP HANA está pronto, mas não foi possível gravar a configuração protegida em {store.ConfigPath}. Verifique permissão e espaço em disco e salve novamente.", 500);
            }

            logger.LogInformation("Configuração HANA salva para {Server}:{Port}. Tabelas criadas: {Count}.",
                credentials.Server, credentials.Port, created.Count);
            var message = created.Count > 0
                ? $"Conexão salva. {created.Count} tabela(s) ausente(s) criada(s) e estrutura validada."
                : "Conexão salva. A estrutura já existia e foi validada sem nenhuma alteração.";
            if (backup is not null)
                message += $" A configuração ilegível anterior foi preservada em {backup}.";
            return ToResult(report, created, saved: true, message);
        }
        finally { SaveLock.Release(); }
    }

    public async Task<HanaStatus> StatusAsync(CancellationToken ct)
    {
        var (state, credentials) = store.Read();
        if (state == StoredConfigState.Missing)
            return new("not_configured", false, false, false, 0, "Conexão SAP HANA ainda não configurada.");
        if (state == StoredConfigState.Unreadable)
            return new("unreadable", true, false, false, 0,
                $"Existe uma configuração salva, mas ela não pôde ser descriptografada. Isso ocorre quando o NexFiscal é executado com outro usuário do Windows ou quando a pasta .nexfiscal-keys foi movida ou apagada. Nada foi alterado. Arquivo: {store.ConfigPath}");

        var saved = credentials!;
        try
        {
            await using var db = connections.Create(saved);
            await db.OpenAsync(ct);
            var report = await HanaSchemaVerifier.VerifyAsync(db, ct);
            return report.Ready
                ? new("ready", true, true, true, report.TablesFound, "Conexão SAP HANA ativa e estrutura validada.",
                    saved.Server, saved.Port, saved.Username)
                : new("schema_pending", true, true, false, report.TablesFound,
                    "Conexão ativa, mas a estrutura BRAI_NEX está incompleta ou incompatível. Teste e salve a configuração novamente para revalidar.",
                    saved.Server, saved.Port, saved.Username, report.Issues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = Fail(ex, saved, saving: false);
            return new("unavailable", true, false, false, 0,
                "Configuração salva, mas o SAP HANA não está disponível agora. " + failure.Message,
                saved.Server, saved.Port, saved.Username);
        }
    }

    private HanaSetupException Fail(Exception ex, HanaCredentials credentials, bool saving)
    {
        var failure = HanaFailure.Translate(ex, credentials, saving);
        // Somente tipo, categoria e código numérico: o texto do driver não vai para o log.
        logger.LogWarning("Operação HANA falhou: {Type} / {Kind} / código {Code}",
            ex.GetType().Name, failure.Kind, HanaFailure.NativeCode(ex));
        return failure;
    }

    private static HanaSetupResult ToResult(
        HanaSchemaReport report, IReadOnlyList<string> created, bool saved, string message) =>
        new(true, report.Ready, report.TablesFound, message, report.State, report.Issues, created, saved);
}
