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
}
