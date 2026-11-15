namespace NexFiscal.Web;

/// <summary>Corpo recebido em /api/setup/hana/test e /save.</summary>
public sealed record HanaSetupRequest(
    string? Server, int Port, string? Username, string? Password, bool ReplaceUnreadable = false)
{
    // O ToString padrão de um record imprime todas as propriedades, inclusive a senha.
    public override string ToString() => $"HanaSetupRequest({Server}:{Port}, usuário {Username}, senha oculta)";
}

/// <summary>Credenciais validadas. É este o conteúdo cifrado em .nexfiscal-hana.dat.</summary>
public sealed record HanaCredentials(string Server, int Port, string Username, string Password)
{
    public override string ToString() => $"HanaCredentials({Server}:{Port}, usuário {Username}, senha oculta)";

    /// <summary>Valida e normaliza a entrada. Lança <see cref="NexFiscalException"/> com mensagem por campo.</summary>
    public static HanaCredentials From(HanaSetupRequest? request)
    {
        if (request is null) throw Invalid("Informe servidor, porta, usuário e senha.");
        var server = request.Server?.Trim() ?? "";
        var username = request.Username?.Trim() ?? "";
        var password = request.Password ?? "";

        if (server.Length is 0 or > 255 ||
            server.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw Invalid("Servidor inválido. Informe apenas o nome ou IP do host (letras, números, ponto, hífen), sem porta e sem espaços.");
        if (request.Port is < 1 or > 65535)
            throw Invalid("Porta inválida. Informe um número entre 1 e 65535 (ex.: 30015).");
        if (username.Length is 0 or > 128 || username.Any(char.IsControl))
            throw Invalid("Informe o usuário do SAP HANA.");
        if (password.Length is 0 or > 1024 || password.Any(char.IsControl))
            throw Invalid("Informe a senha do SAP HANA.");
        return new HanaCredentials(server, request.Port, username, password);
    }

    private static NexFiscalException Invalid(string message) => new("invalid_input", message, 400);
}

/// <summary>Resultado de testar ou salvar. Nunca contém senha.</summary>
/// <param name="SchemaState">ready | pending | incompatible</param>
public sealed record HanaSetupResult(
    bool Connected, bool SchemaReady, int TablesFound, string Message,
    string SchemaState, IReadOnlyList<string> Issues, IReadOnlyList<string> CreatedTables, bool Saved);

/// <summary>Estado da configuração salva. Nunca contém senha.</summary>
/// <param name="State">not_configured | ready | schema_pending | unavailable | unreadable</param>
public sealed record HanaStatus(
    string State, bool Configured, bool Connected, bool SchemaReady, int TablesFound, string Message,
    string? Server = null, int? Port = null, string? Username = null,
    IReadOnlyList<string>? Issues = null, bool CanUpgrade = false);

/// <summary>Falha já traduzida para o operador: mensagem em português, sem segredos nem detalhes internos.</summary>
/// <remarks>Usada em todas as funções (HANA e empresas).</remarks>
public sealed class NexFiscalException(string kind, string message, int statusCode, IReadOnlyList<string>? issues = null)
    : Exception(message)
{
    public string Kind { get; } = kind;
    public int StatusCode { get; } = statusCode;
    public IReadOnlyList<string> Issues { get; } = issues ?? [];
}
