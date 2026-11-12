namespace NexFiscal.Capture;

/// <summary>
/// Escolhe apenas um NSU faltante ENTRE envelopes recebidos do mesmo CNPJ.
/// Não inventa NSUs anteriores ao primeiro documento nem altera checkpoints.
/// Lacunas podem ser legítimas (NSU não gerado/indisponível).
/// </summary>
public static class NfeRecoveryCandidateSelector
{
    public static long? FindNext(IReadOnlyList<long> knownOrderedNsus,
        IReadOnlySet<long> attempted, int maxGapScan = 20_000)
    {
        ArgumentNullException.ThrowIfNull(knownOrderedNsus);
        ArgumentNullException.ThrowIfNull(attempted);
        if (maxGapScan < 1) throw new ArgumentOutOfRangeException(nameof(maxGapScan));
        for (var i = 1; i < knownOrderedNsus.Count; i++)
        {
            var previous = knownOrderedNsus[i - 1];
            var next = knownOrderedNsus[i];
            if (next <= previous || previous < 1)
                throw new ArgumentException("NSUs conhecidos devem ser positivos e ordenados sem duplicatas.");
            var limit = Math.Min(next - previous - 1, maxGapScan);
            for (var offset = 1L; offset <= limit; offset++)
            {
                var candidate = previous + offset;
                if (!attempted.Contains(candidate)) return candidate;
            }
        }
        return null;
    }
}
