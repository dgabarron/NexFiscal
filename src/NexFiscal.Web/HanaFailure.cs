using System.Data.Common;
using Sap.Data.Hana;

namespace NexFiscal.Web;

/// <summary>
/// Traduz exceções do driver SAP HANA em mensagens objetivas para o operador.
/// Usa apenas o código numérico do HANA; o texto original do driver não é repassado.
/// </summary>
public static class HanaFailure
{
    /// <summary>Código nativo do HANA (ex.: 10 = autenticação) ou null se a exceção não veio do banco.</summary>
    public static int? NativeCode(Exception ex) => ex switch
    {
        HanaException hana => hana.NativeError,
        DbException db => db.ErrorCode,
        _ => null
    };

    public static NexFiscalException Translate(Exception ex, HanaCredentials credentials, bool saving)
    {
        if (ex is NexFiscalException known) return known;
        if (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException or EntryPointNotFoundException)
            return new("driver_unavailable",
                "O driver SAP HANA não pôde ser carregado nesta máquina. Recompile o projeto com o script de execução e confirme que o Windows é 64 bits.", 500);

        var target = $"{credentials.Server}:{credentials.Port}";
        var code = NativeCode(ex);
        return code switch
        {
            -10709 => Unreachable(ex.Message, target),
            -10807 or -10806 => new("unreachable",
                $"A conexão com {target} foi interrompida. Verifique a rede e se o SAP HANA está ativo.", 503),
            10 => new("invalid_credentials",
                "Usuário ou senha do SAP HANA incorretos. Atenção: várias tentativas erradas podem bloquear o usuário no banco.", 422),
            414 => new("password_change_required",
                "O SAP HANA exige a troca da senha deste usuário antes do uso. Altere a senha no HANA e tente novamente.", 422),
            415 or 416 => new("user_locked",
                "O usuário está bloqueado ou desativado no SAP HANA. Peça o desbloqueio ao administrador do banco.", 422),
            4321 => new("tls_required",
                "Este servidor SAP HANA só aceita conexões criptografadas (TLS), que esta etapa ainda não suporta.", 422),
            258 => new("insufficient_privilege", saving
                ? "O usuário não tem permissão para criar o schema BRAI_NEX ou suas tabelas. É necessário o privilégio CREATE SCHEMA (primeira instalação) ou acesso completo ao schema BRAI_NEX existente. Nada foi salvo."
                : "O usuário conectou, mas não tem permissão para consultar a estrutura do banco.", 422),
            386 => new("schema_not_accessible",
                "O schema BRAI_NEX já existe no SAP HANA, mas este usuário não tem acesso a ele. Use o usuário dono do schema ou peça a concessão de acesso. Nada foi alterado.", 422),
            288 => new("table_not_accessible",
                "Uma tabela do NexFiscal já existe no schema BRAI_NEX, mas este usuário não consegue enxergá-la. Peça acesso ao schema BRAI_NEX. Nenhuma tabela foi alterada.", 422),
            259 => new("schema_outdated",
                "A estrutura do SAP HANA está desatualizada para esta versão. Abra a aba Conexão SAP HANA e clique em \"Atualizar estrutura\".", 409),
            301 => new("duplicate",
                "Já existe um registro com esses dados no SAP HANA. Nada foi alterado.", 409),
            not null => new("hana_error",
                $"O SAP HANA recusou a operação (código {code}). " + (saving ? "A configuração não foi salva." : "Nada foi alterado."), 502),
            _ => new("unexpected",
                "Falha inesperada ao acessar o SAP HANA. " + (saving ? "A configuração não foi salva." : "Nada foi alterado."), 500)
        };
    }

    private static NexFiscalException Unreachable(string driverMessage, string target)
    {
        if (driverMessage.Contains("resolve host", StringComparison.OrdinalIgnoreCase))
            return new("host_not_found",
                $"Servidor \"{target.Split(':')[0]}\" não encontrado. Confira o nome ou IP do host.", 503);
        if (driverMessage.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return new("connection_refused",
                $"Conexão recusada em {target}. Confira a porta SQL e se o SAP HANA está em execução.", 503);
        if (driverMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            driverMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return new("connection_timeout",
                $"Sem resposta de {target}. Confira o host, a porta e se há firewall bloqueando o acesso.", 503);
        return new("unreachable",
            $"Não foi possível conectar a {target}. Confira host, porta, rede e firewall.", 503);
    }
}
