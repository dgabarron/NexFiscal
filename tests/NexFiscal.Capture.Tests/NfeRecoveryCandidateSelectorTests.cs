using NexFiscal.Capture;
using Xunit;

public sealed class NfeRecoveryCandidateSelectorTests
{
    [Fact]
    public void SemEnvelopesOuSemLacunasNaoConsulta()
    {
        Assert.Null(NfeRecoveryCandidateSelector.FindNext([], new HashSet<long>()));
        Assert.Null(NfeRecoveryCandidateSelector.FindNext([100, 101, 102], new HashSet<long>()));
    }

    [Fact]
    public void NuncaVarreAntesDoPrimeiroNsuConhecido()
    {
        Assert.Equal(201L, NfeRecoveryCandidateSelector.FindNext([200, 203], new HashSet<long>()));
    }

    [Fact]
    public void PulaTentativasAnteriores()
    {
        Assert.Equal(202L, NfeRecoveryCandidateSelector.FindNext(
            [200, 204], new HashSet<long> { 201 }));
    }

    [Fact]
    public void NaoRepeteNsuJaTentado()
    {
        Assert.Null(NfeRecoveryCandidateSelector.FindNext(
            [200, 202], new HashSet<long> { 201 }));
    }

    [Fact]
    public void RespeitaLimiteDeBuscaPorLacuna()
    {
        Assert.Null(NfeRecoveryCandidateSelector.FindNext(
            [100, 200], new HashSet<long> { 101, 102 }, maxGapScan: 2));
    }
}
