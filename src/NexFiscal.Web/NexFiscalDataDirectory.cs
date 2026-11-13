public sealed class NexFiscalDataDirectory
{
    public string Root { get; }

    public NexFiscalDataDirectory(string? overrideDirectory = null)
    {
        var configured = !string.IsNullOrWhiteSpace(overrideDirectory)
            ? overrideDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NexFiscal", "V1");
        if (!Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException("NEXFISCAL_DATA_DIR precisa ser um caminho absoluto.");
        Root = Path.GetFullPath(configured);
        Directory.CreateDirectory(Root);
    }

    public string PathFor(string name) => Path.Combine(Root, name);

    // No desenvolvimento os recursos da interface ficam em src/NexFiscal.Web/wwwroot.
    // Em publicação independente, ficam junto ao executável.
    public static string ResolveContentRoot(string assemblyDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(assemblyDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "NexFiscal.Web.csproj")))
                return current.FullName;
            current = current.Parent;
        }
        return Path.GetFullPath(assemblyDirectory);
    }
}
