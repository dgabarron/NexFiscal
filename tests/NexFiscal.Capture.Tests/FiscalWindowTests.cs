using System.Text.Json;
using Xunit;

public sealed class FiscalWindowTests
{
    private static JsonDocument Read(string value) => JsonDocument.Parse(
        "{\"nextAllowedUtc\":" + value + "}");

    [Theory]
    [InlineData("\"2026-11-13T14:00:00Z\"")]
    [InlineData("\"2026-11-13T14:00:00\"")]
    [InlineData("\"2026-11-13T15:00:00+01:00\"")]
    public void DatasComOuSemFusoRepresentamMesmoInstanteUtc(string timestamp)
    {
        using var doc = Read(timestamp);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 14, 0, 0, TimeSpan.Zero),
            FiscalWindow.NextAllowedUtc(doc.RootElement));
    }

    [Fact]
    public void BloqueiaConsultaSomenteEnquantoHorarioNaoChegou()
    {
        using var doc = Read("\"2026-11-13T14:00:00Z\"");
        Assert.True(FiscalWindow.IsWaiting(doc.RootElement,
            new DateTimeOffset(2026, 11, 13, 13, 59, 0, TimeSpan.Zero)));
        Assert.False(FiscalWindow.IsWaiting(doc.RootElement,
            new DateTimeOffset(2026, 11, 13, 14, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"data-invalida\"")]
    public void CamposNaoValidosNaoSaoInterpretadosComoFusoLocal(string value)
    {
        using var doc = Read(value);
        Assert.Null(FiscalWindow.NextAllowedUtc(doc.RootElement));
    }
}
