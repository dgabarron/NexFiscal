namespace NexFiscal.Capture;

/// <summary>O repositório deve confirmar envelopes e checkpoint na mesma transação.</summary>
public interface INfeCaptureStore
{
    Task<NfeCheckpoint> LoadAsync(string cnpj, int environment, CancellationToken cancellationToken);
    Task CommitAsync(NfeCheckpoint expected, NfeCheckpoint updated, IReadOnlyList<NfeEnvelope> documents, CancellationToken cancellationToken);
    Task ReserveNextAttemptAsync(NfeCheckpoint expected, DateTimeOffset nextAllowedAt, CancellationToken cancellationToken);
}

public interface INfeDistributionClient
{
    Task<string> QueryByNsuAsync(string cnpj, int environment, long lastNsu, CancellationToken cancellationToken);
}

public sealed class NfeCaptureOrchestrator(INfeCaptureStore store, INfeDistributionClient client, TimeProvider clock)
{
    public async Task<NfeBatch?> RunOnceAsync(string cnpj, int environment, CancellationToken cancellationToken = default)
    {
        if (cnpj.Length != 14 || !cnpj.All(char.IsAsciiDigit) || environment is not (1 or 2))
            throw new ArgumentException("Informe CNPJ de 14 dígitos e ambiente válido.");
        var current = await store.LoadAsync(cnpj, environment, cancellationToken);
        var now = clock.GetUtcNow();
        if (now < current.NextAllowedAt) return null;

        // Reserva conservadora antes da rede evita repetição imediata em falha, timeout ou reinício.
        await store.ReserveNextAttemptAsync(current, now.AddHours(1), cancellationToken);
        var response = await client.QueryByNsuAsync(cnpj, environment, current.LastNsu, cancellationToken);
        var batch = NfeDistribution.Parse(response);
        if (batch.Status is 137 or 138)
        {
            var updated = NfeDistribution.Advance(current, batch, now);
            await store.CommitAsync(current, updated, batch.Documents, cancellationToken);
        }
        return batch;
    }
}
