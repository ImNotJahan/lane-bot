namespace Lane.Core.Sessions;

public sealed class SessionOptions
{
    /// <summary>
    /// How long a pump waits after the first message before starting a turn, so a burst of
    /// three messages becomes one reply instead of three. Short enough not to feel laggy.
    /// </summary>
    public TimeSpan BatchWindow { get; set; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Voice needs longer — the batch is really "until the speaker stops".</summary>
    public TimeSpan VoiceBatchWindow { get; set; } = TimeSpan.FromMilliseconds(900);

    /// <summary>Per-session queue depth. Writers wait rather than drop when full.</summary>
    public int InboxCapacity { get; set; } = 512;

    /// <summary>
    /// Maximum turns running at once across every session. The cost governor: without it,
    /// one busy server multiplies straight into the API bill.
    /// </summary>
    public int MaxConcurrentTurns { get; set; } = 4;

    /// <summary>How long a session may sit idle before the registry closes it.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>
/// The global concurrency ceiling. Held for the duration of a turn, so sessions run in
/// parallel only up to the configured budget.
/// </summary>
public sealed class TurnBudget : IDisposable
{
    private readonly SemaphoreSlim _slots;

    public TurnBudget(int maxConcurrent) =>
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrent), Math.Max(1, maxConcurrent));

    public int Available => _slots.CurrentCount;

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        return new Slot(_slots);
    }

    public void Dispose() => _slots.Dispose();

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release();
        }
    }
}
