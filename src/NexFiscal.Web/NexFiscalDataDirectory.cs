/// <summary>
/// Escolhe uma raiz única para os dados privados, independente de iniciar
/// pelo Visual Studio, pela solução ou pelo script PowerShell.
/// Não move nem sobrescreve dados existentes.
/// </summary>
public sealed class NexFiscalDataDirectory
{
    private static readonly string[] PrivateFolders =
    [
        ".nexfiscal-keys", ".nexfiscal-certificates",
        ".nexfiscal-schedules", ".nexfiscal-cte", ".nexfiscal-downloads"
    ];

    public string Root { get; }

    public NexFiscalDataDirectory(string assemblyBaseDirectory, string? explicitDirectory = null)
    {
        Root = Resolve(assemblyBaseDirectory, explicitDirectory);
        Directory.CreateDirectory(Root);
    }

    public string PathFor(string name) => Path.Combine(Root, name);

    // Servir wwwroot do mesmo lugar no F5 e no dotnet run da solução.
    public static string ResolveContentRoot(string assemblyBaseDirectory)
    {
        var binaryDirectory = Path.GetFullPath(assemblyBaseDirectory);
        return LocateProjectDirectory(binaryDirectory) ?? binaryDirectory;
    }

    public static string Resolve(string assemblyBaseDirectory, string? explicitDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            if (!Path.IsPathFullyQualified(explicitDirectory))
                throw new InvalidOperationException(
                    "NexFiscal: NEXFISCAL_DATA_DIR deve ser um caminho absoluto.");
            return Path.GetFullPath(explicitDirectory);
        }

        var binaryDirectory = Path.GetFullPath(assemblyBaseDirectory);
        var projectDirectory = LocateProjectDirectory(binaryDirectory);
        if (projectDirectory is null)
            return binaryDirectory; // instalação publicada

        var repositoryDirectory = Path.GetFullPath(
            Path.Combine(projectDirectory, "..", ".."));
        var candidates = new[] { projectDirectory, repositoryDirectory };
        var existing = candidates.Where(HasPrivateState).ToArray();

        if (existing.Length > 1)
            throw new InvalidOperationException(
                "NexFiscal encontrou arquivos privados em duas pastas diferentes: " +
                string.Join(" e ", existing) +
                ". Não é seguro decidir automaticamente qual contém o A1 e as chaves de proteção. " +
                "Preserve ambas as pastas e configure NEXFISCAL_DATA_DIR para a instalação correta.");

        return existing.Length == 1 ? existing[0] : projectDirectory;
    }

    private static string? LocateProjectDirectory(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NexFiscal.Web.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static bool HasPrivateState(string root)
    {
        if (File.Exists(Path.Combine(root, ".nexfiscal-hana.dat")))
            return true;

        foreach (var name in PrivateFolders)
        {
            var directory = Path.Combine(root, name);
            if (Directory.Exists(directory) &&
                Directory.EnumerateFileSystemEntries(directory).Any())
                return true;
        }
        return false;
    }
}
