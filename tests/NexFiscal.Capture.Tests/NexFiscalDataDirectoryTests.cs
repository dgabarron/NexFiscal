using Xunit;

public sealed class NexFiscalDataDirectoryTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "nexfiscal-paths-" + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Root, "src", "NexFiscal.Web");
        public string Binaries => Path.Combine(Project, "bin", "Debug", "net8.0");

        public Fixture()
        {
            Directory.CreateDirectory(Binaries);
            File.WriteAllText(Path.Combine(Project, "NexFiscal.Web.csproj"), "<Project />");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public void SemDadosAnterioresUsaPastaDoProjeto()
    {
        using var fixture = new Fixture();
        Assert.Equal(fixture.Project, NexFiscalDataDirectory.Resolve(fixture.Binaries));
    }

    [Fact]
    public void EncontraInstalacaoLegadaNaRaizDaSolucao()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".nexfiscal-certificates"));
        File.WriteAllText(Path.Combine(fixture.Root, ".nexfiscal-certificates", "fixture.a1enc"), "fixture");
        Assert.Equal(fixture.Root, NexFiscalDataDirectory.Resolve(fixture.Binaries));
    }

    [Fact]
    public void PreservaConfiguracaoNaPastaDoProjeto()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Project, ".nexfiscal-hana.dat"), "fixture");
        Assert.Equal(fixture.Project, NexFiscalDataDirectory.Resolve(fixture.Binaries));
    }

    [Fact]
    public void DetectaRaizesConflitantesSemMoverOuApagarArquivos()
    {
        using var fixture = new Fixture();
        var old = Path.Combine(fixture.Root, ".nexfiscal-hana.dat");
        var newer = Path.Combine(fixture.Project, ".nexfiscal-hana.dat");
        File.WriteAllText(old, "a");
        File.WriteAllText(newer, "b");
        var error = Assert.Throws<InvalidOperationException>(
            () => NexFiscalDataDirectory.Resolve(fixture.Binaries));
        Assert.Contains("NEXFISCAL_DATA_DIR", error.Message);
        Assert.Equal("a", File.ReadAllText(old));
        Assert.Equal("b", File.ReadAllText(newer));
    }

    [Fact]
    public void PermitePastaExplicitaSemModificarDadosLegados()
    {
        using var fixture = new Fixture();
        var custom = Path.Combine(fixture.Root, "dados");
        Assert.Equal(custom, NexFiscalDataDirectory.Resolve(fixture.Binaries, custom));
    }

    [Fact]
    public void NegaCaminhoRelativo()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidOperationException>(
            () => NexFiscalDataDirectory.Resolve(fixture.Binaries, "dados"));
    }

    [Fact]
    public void PublicacaoSemProjetoUsaDiretorioDosBinarios()
    {
        using var fixture = new Fixture();
        var publish = Path.Combine(fixture.Root, "published");
        Directory.CreateDirectory(publish);
        Assert.Equal(publish, NexFiscalDataDirectory.Resolve(publish));
    }
}
