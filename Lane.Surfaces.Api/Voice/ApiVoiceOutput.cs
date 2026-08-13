using Lane.Audio;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Api.Voice;

/// <summary>
/// Where a voice socket's two kinds of output go.
///
/// An interface rather than a <c>WebSocket</c> because a socket permits exactly one send at
/// a time, so audio frames and text frames have to be serialised against each other — and
/// because it makes the channel testable without opening one.
/// </summary>
public interface IVoiceSocket
{
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);

    Task SendEventAsync<T>(string name, T payload, CancellationToken ct);

    bool IsOpen { get; }
}

/// <summary>
/// Lane's voice, and her words, on one client socket.
///
/// Both are sent: a client app showing a transcript alongside the audio should not have to
/// hold a second connection open to get it.
/// </summary>
public sealed class ApiVoiceOutput(
    SessionId id,
    IVoiceSocket socket,
    AudioFormat format,
    ILogger log)
    : SessionChannelBase(
        id, id.Surface,
        ChannelCapabilities.Voice | ChannelCapabilities.Text | ChannelCapabilities.Interrupt),
      IVoiceOutput, ITextOutput
{
    private readonly SemaphoreSlim _speaking = new(1, 1);

    public AudioFormat Format => format;

    public async Task PlayAsync(IAsyncEnumerable<AudioFrame> audio, CancellationToken ct)
    {
        // One utterance at a time. Two clauses interleaved on one socket is noise.
        await _speaking.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await socket.SendEventAsync("speaking", new { state = "start" }, ct).ConfigureAwait(false);

            await foreach (AudioFrame frame in audio.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                if (!socket.IsOpen) return;

                await socket.SendAudioAsync(frame.Pcm, ct).ConfigureAwait(false);
            }

            await socket.SendEventAsync("speaking", new { state = "end" }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Interrupted. The client already knows — it is what caused this.
            throw;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not send audio on {Session}", Id);
        }
        finally
        {
            _speaking.Release();
        }
    }

    public Task StopAsync() => Task.CompletedTask;

    public async Task SendAsync(OutboundText text, CancellationToken ct)
    {
        if (!socket.IsOpen) return;

        try
        {
            await socket.SendEventAsync("message", new { text = text.Text }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Could not send text on {Session}", Id);
        }
    }

    public override ValueTask DisposeAsync()
    {
        _speaking.Dispose();

        return ValueTask.CompletedTask;
    }
}
