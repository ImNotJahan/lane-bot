using Lane.Audio;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;
using NetCord.Gateway.Voice;

namespace Lane.Surfaces.Discord.Voice;

/// <summary>
/// Lane's voice in one Discord voice channel.
///
/// The frames handed in are already at the format this channel asked for, so all that
/// happens here is Opus encoding and the speaking state Discord requires before it will
/// accept audio at all.
/// </summary>
internal sealed class DiscordVoiceOutput(
    SessionId id,
    VoiceClient client,
    Action onSpeaking,
    ILogger log)
    : SessionChannelBase(id, id.Surface, ChannelCapabilities.Voice | ChannelCapabilities.Interrupt), IVoiceOutput
{
    private readonly SemaphoreSlim _speaking = new(1, 1);

    public AudioFormat Format => AudioFormat.Pcm48kStereo;

    public async Task PlayAsync(IAsyncEnumerable<AudioFrame> audio, CancellationToken ct)
    {
        // One utterance at a time. Two overlapping streams into one Opus encoder is noise.
        await _speaking.WaitAsync(ct).ConfigureAwait(false);

        onSpeaking();

        try
        {
            await client.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone), cancellationToken: ct)
                        .ConfigureAwait(false);

            Stream voice = client.CreateVoiceStream();

            await using OpusEncodeStream opus = new(
                voice, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Voip);

            await foreach (AudioFrame frame in audio.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                await opus.WriteAsync(frame.Pcm, ct).ConfigureAwait(false);
            }

            await opus.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Interrupted. Expected, and the caller already knows.
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not speak in {Session}", Id);
        }
        finally
        {
            onSpeaking();

            _speaking.Release();
        }
    }

    public Task StopAsync() => Task.CompletedTask;

    public override ValueTask DisposeAsync()
    {
        _speaking.Dispose();

        return ValueTask.CompletedTask;
    }
}
