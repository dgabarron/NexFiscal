using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using NexFiscal.Web;

namespace NexFiscal.Tests;

/// <summary>
/// SAP HANA simulado em memória. Entende exatamente os comandos que a Função 1 pode emitir
/// e rejeita qualquer outro SQL — portanto um DROP/ALTER/TRUNCATE/DELETE/UPDATE quebraria os testes.
/// NÃO substitui a homologação em HANA real.
/// </summary>
public sealed class FakeHanaServer
{
    public const string User = "NEXFISCAL_APP";
    public const string Password = "S3nha;Secreta=\"x' y";

    public bool Online = true;
    public bool SchemaExists;
    public int VersionRows;
    public int OpenCount;
    public readonly Dictionary<string, List<(string Column, string Type)>> Tables = new(StringComparer.Ordinal);
    public readonly List<string> Executed = [];
    /// <summary>Recebe o SQL e devolve um código de erro HANA para simular falha, ou null.</summary>
    public Func<string, int?> FailWith = _ => null;

    public IEnumerable<string> Writes => Executed.Where(sql => !sql.StartsWith("SELECT", StringComparison.Ordinal));

    public void AddCompatibleTable(string table) =>
        Tables[table] = HanaSchemaVerifier.Required[table].Select(c => (c.Column, c.Type)).ToList();

    public void AddAllCompatibleTables()
    {
        SchemaExists = true;
        foreach (var table in HanaSchemaVerifier.Required.Keys) AddCompatibleTable(table);
    }

    internal object? Run(string sql, bool reader)
    {
        Executed.Add(sql);
        if (FailWith(sql) is { } code) throw new FakeHanaException(code, "simulated failure");

        if (sql.StartsWith("SELECT TABLE_NAME,COLUMN_NAME,DATA_TYPE_NAME FROM SYS.TABLE_COLUMNS WHERE SCHEMA_NAME='BRAI_NEX'", StringComparison.Ordinal))
        {
            var data = new DataTable();
            data.Columns.Add("TABLE_NAME"); data.Columns.Add("COLUMN_NAME"); data.Columns.Add("DATA_TYPE_NAME");
            foreach (var (table, columns) in Tables)
                foreach (var (column, type) in columns) data.Rows.Add(table, column, type);
            return data.CreateDataReader();
        }
        if (reader) throw new InvalidOperationException("Consulta inesperada: " + sql);
        if (sql == "SELECT COUNT(*) FROM SYS.SCHEMAS WHERE SCHEMA_NAME='BRAI_NEX'") return SchemaExists ? 1 : 0;
        if (sql == "SELECT COUNT(*) FROM \"BRAI_NEX\".\"BRAI_NEX_SchemaVersion\" WHERE \"Version\"=1")
            return Tables.ContainsKey("BRAI_NEX_SchemaVersion") ? VersionRows : throw new FakeHanaException(259, "invalid table name");
        if (sql == "CREATE SCHEMA \"BRAI_NEX\"")
        {
            if (SchemaExists) throw new FakeHanaException(386, "cannot use duplicate schema name");
            SchemaExists = true;
            return 0;
        }
        var create = Regex.Match(sql, "^CREATE COLUMN TABLE \"BRAI_NEX\"\\.\"(\\w+)\" \\(");
        if (create.Success)
        {
            var table = create.Groups[1].Value;
            if (!SchemaExists) throw new FakeHanaException(362, "invalid schema name");
            if (Tables.ContainsKey(table)) throw new FakeHanaException(288, "cannot use duplicate table name");
            Tables[table] = Regex.Matches(sql, "^\\s+\"(\\w+)\"\\s+([A-Z]+)", RegexOptions.Multiline)
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
            return 0;
        }
        if (sql.StartsWith("INSERT INTO \"BRAI_NEX\".\"BRAI_NEX_SchemaVersion\" (\"Version\",\"Name\",\"AppliedAtUtc\") VALUES (1,", StringComparison.Ordinal))
        {
            VersionRows++;
            return 1;
        }
        throw new InvalidOperationException("SQL não permitido na Função 1: " + sql);
    }
}

public sealed class FakeHanaException(int code, string message) : DbException(message, code);

public sealed class FakeHanaFactory(FakeHanaServer server) : IHanaConnectionFactory
{
    public DbConnection Create(HanaCredentials credentials) => new FakeConnection(server, credentials);
}

internal sealed class FakeConnection(FakeHanaServer server, HanaCredentials credentials) : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;
    [AllowNull] public override string ConnectionString { get; set; } = "";
    public override string Database => "FAKE";
    public override string DataSource => credentials.Server;
    public override string ServerVersion => "fake";
    public override ConnectionState State => _state;
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    public override void Close() => _state = ConnectionState.Closed;
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new FakeCommand(server, this);

    public override void Open()
    {
        server.OpenCount++;
        if (!server.Online)
            throw new FakeHanaException(-10709, "Connection failed (RTE:[89006] System call 'connect' failed, rc=111:Connection refused)");
        if (credentials.Username != FakeHanaServer.User || credentials.Password != FakeHanaServer.Password)
            throw new FakeHanaException(10, "authentication failed");
        _state = ConnectionState.Open;
    }
}

internal sealed class FakeCommand(FakeHanaServer server, FakeConnection connection) : DbCommand
{
    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; } = connection;
    protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

    private object? Run(bool reader)
    {
        if (connection.State != ConnectionState.Open) throw new InvalidOperationException("Conexão fechada.");
        return server.Run(CommandText, reader);
    }
    public override int ExecuteNonQuery() => Convert.ToInt32(Run(false));
    public override object? ExecuteScalar() => Run(false);
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => (DbDataReader)Run(true)!;
}
