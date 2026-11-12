using NexFiscal.Capture;
using Xunit;

namespace NexFiscal.Capture.Tests;

public sealed class NfeCaptureOrchestratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);
    private const string Cnpj = "12345678000190";

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeClient : INfeDistributionClient
    {
        public int Calls { get; private set; }
        public Task<string> QueryByNsuAsync(string cnpj, int environment, long lastNsu, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(Cnpj, cnpj);
            Assert.Equal(1, environment);
            Assert.Equal(10, lastNsu);
            return Task.FromResult("<retDistDFeInt><cStat>138</cStat><ultNSU>000000000000011</ultNSU><maxNSU>000000000000020</maxNSU></retDistDFeInt>");
        }
    }

    private sealed class FakeStore : INfeCaptureStore
    {
        public NfeCheckpoint State = new(Cnpj, 1, 10, DateTimeOffset.MinValue);
        public int Reservations;
        public int Commits;

        public Task<NfeCheckpoint> LoadAsync(string cnpj, int environment, CancellationToken ct)
        {
            Assert.Equal(Cnpj, cnpj);
            Assert.Equal(1, environment);
            return Task.FromResult(State);
        }

        public Task ReserveNextAttemptAsync(NfeCheckpoint expected, DateTimeOffset nextAllowed, CancellationToken ct)
        {
            Assert.Equal(expected, State);
            State = expected with { NextAllowedAt = nextAllowed };
            Reservations++;
            return Task.CompletedTask;
        }

        public Task CommitAsync(NfeCheckpoint expected, NfeCheckpoint updated, IReadOnlyList<NfeEnvelope> documents, CancellationToken ct)
        {
            Assert.Equal(expected, State);
            Assert.Equal(11, updated.LastNsu);
            Assert.Empty(documents);
            State = updated;
            Commits++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Commits_After_Persistent_Reservation()
    {
        var store = new FakeStore();
        var client = new FakeClient();
        var worker = new NfeCaptureOrchestrator(store, client, new FakeClock());
        var result = await worker.RunOnceAsync(Cnpj, 1);

        Assert.NotNull(result);
        Assert.Equal(138, result.Status);
        Assert.Equal(1, store.Reservations);
        Assert.Equal(1, store.Commits);
        Assert.Equal(11, store.State.LastNsu);
        Assert.Equal(Now.AddMinutes(3), store.State.NextAllowedAt);

        // Consulta repetida dentro da janela deve ser ignorada.
        Assert.Null(await worker.RunOnceAsync(Cnpj, 1));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Network_Failure_Keeps_Conservative_Reservation()
    {
        var store = new FakeStore();
        var client = new FailedClient();
        var worker = new NfeCaptureOrchestrator(store, client, new FakeClock());
        await Assert.ThrowsAsync<HttpRequestException>(() => worker.RunOnceAsync(Cnpj, 1));
        Assert.Equal(Now.AddHours(1), store.State.NextAllowedAt);
        Assert.Equal(0, store.Commits);
    }

    private sealed class FailedClient : INfeDistributionClient
    {
        public Task<string> QueryByNsuAsync(string cnpj, int environment, long lastNsu, CancellationToken ct)
            => throw new HttpRequestException("Simulação de falha de comunicação");
    }
}
