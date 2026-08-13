using System.Collections.Concurrent;
using Lane.Core.Identity;
using Lane.Core.Sessions;

namespace Lane.Testing;

/// <summary>
/// A session channel that records what was sent to it instead of talking to a platform.
///
/// One per session in tests: asserting that channel A never received channel B's reply is
/// the whole point of the cohesion guarantee, so the recorder has to be per-session too.
/// </summary>
public sealed class RecordingChannel : SessionChannelBase, ITextOutput
{
    private readonly ConcurrentQueue<OutboundText> _sent = new();

    public RecordingChannel(
        SessionId id,
        ChannelCapabilities capabilities = ChannelCapabilities.Text)
        : base(id, id.Surface, capabilities) { }

    public IReadOnlyList<OutboundText> Sent => [.. _sent];

    public IReadOnlyList<string> Texts => [.. _sent.Select(s => s.Text)];

    /// <summary>Signalled on every send, so tests can await delivery rather than sleep.</summary>
    public event Action<OutboundText>? Delivered;

    public Task SendAsync(OutboundText text, CancellationToken ct)
    {
        _sent.Enqueue(text);
        Delivered?.Invoke(text);

        return Task.CompletedTask;
    }

    /// <summary>Waits until at least <paramref name="count"/> messages have been delivered.</summary>
    public Task<IReadOnlyList<string>> WaitForAsync(int count) =>
        WaitForAsync(count, TimeSpan.FromSeconds(5));

    /// <summary>
    /// Asserts that nothing arrives.
    ///
    /// Proving a negative needs a wait long enough that a reply on its way would have landed;
    /// the turn is otherwise still in flight and the test passes for the wrong reason.
    /// </summary>
    public async Task QuietAsync(TimeSpan? window = null)
    {
        await Task.Delay(window ?? TimeSpan.FromMilliseconds(750), CancellationToken.None).ConfigureAwait(false);

        if (_sent.IsEmpty) return;

        throw new InvalidOperationException(
            $"Expected {Id} to stay quiet, but it received: [{string.Join(" | ", Texts)}]");
    }

    /// <summary>Waits until at least <paramref name="count"/> messages have been delivered.</summary>
    public async Task<IReadOnlyList<string>> WaitForAsync(int count, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);

        while (_sent.Count < count)
        {
            if (cts.IsCancellationRequested)
                throw new TimeoutException(
                    $"Expected {count} message(s) on {Id} within {timeout.TotalSeconds:0.#}s, " +
                    $"got {_sent.Count}: [{string.Join(" | ", Texts)}]");

            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }

        return Texts;
    }
}
