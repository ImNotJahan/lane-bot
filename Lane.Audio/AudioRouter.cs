using System.Collections.Concurrent;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Audio;

/// <summary>Which conversation an audio source belongs to, and who is speaking on it.</summary>
public sealed record AudioBinding(AudioSourceId Source, SessionId Session, Participant Speaker);

/// <summary>
/// Listens to every microphone at once and turns what it hears into messages.
///
/// One recogniser per source, each on its own task. Two people in one voice channel are two
/// sources bound to one session, so their words arrive as one ordered conversation through
/// that session's pump — while a microphone bound elsewhere runs fully in parallel. v2
/// could only ever hear Discord, and only through a stream type its ear was built around.
/// </summary>
public sealed class AudioRouter(
    IAgentKernel kernel,
    Func<ISpeechRecognizer> recognizerFactory,
    IVoiceFloor floor,
    ILogger<AudioRouter> log) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Listener> _listeners = new();

    private CancellationTokenSource _lifetime = new();

    public int ActiveSources => _listeners.Count;

    /// <summary>Starts listening to a source and attributing it to a person in a conversation.</summary>
    public void Register(IAudioSource source, SessionId session, Participant speaker)
    {
        ArgumentNullException.ThrowIfNull(source);

        AudioBinding binding = new(source.Id, session, speaker);

        Listener listener = new(source, binding);

        if (!_listeners.TryAdd(source.Id.Value, listener))
        {
            log.LogDebug("Already listening to {Source}", source.Id);
            return;
        }

        listener.Task = Task.Run(() => ListenAsync(listener, _lifetime.Token), CancellationToken.None);

        log.LogInformation("Listening to {Source} as {Speaker} in {Session}",
            source.Id, speaker.DisplayName, session);
    }

    public async ValueTask UnregisterAsync(AudioSourceId id)
    {
        if (!_listeners.TryRemove(id.Value, out Listener? listener)) return;

        await listener.StopAsync().ConfigureAwait(false);

        log.LogInformation("Stopped listening to {Source}", id);
    }

    private async Task ListenAsync(Listener listener, CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, listener.Stop.Token);

        await using ISpeechRecognizer recognizer = recognizerFactory();

        try
        {
            await foreach (Transcript transcript in
                recognizer.TranscribeAsync(listener.Source, linked.Token).ConfigureAwait(false))
            {
                if (transcript.Text.Length == 0) continue;

                if (!transcript.IsFinal)
                {
                    // Someone starting to talk while Lane is talking is the barge-in signal.
                    floor.NoticeSpeech(listener.Binding.Session, transcript);
                    continue;
                }

                await SubmitAsync(listener.Binding, transcript, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            // One microphone failing must not take the others down with it.
            log.LogError(ex, "Listening to {Source} failed", listener.Source.Id);
        }
    }

    private async Task SubmitAsync(AudioBinding binding, Transcript transcript, CancellationToken ct)
    {
        log.LogInformation("[{Speaker}] heard: {Text}", binding.Speaker.DisplayName, transcript.Text);

        // Whoever was holding the floor has finished a sentence; anything Lane was saying
        // has already been cut off by the interim result that preceded this.
        floor.NoticeFinal(binding.Session);

        LaneMessage message = LaneMessage.User(
            binding.Session, binding.Speaker, transcript.Text, DateTimeOffset.UtcNow);

        await kernel.SubmitAsync(new InboundEvent
        {
            Session = binding.Session,
            Author  = binding.Speaker,
            Message = message
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        foreach (Listener listener in _listeners.Values) await listener.StopAsync().ConfigureAwait(false);

        _listeners.Clear();
        _lifetime.Dispose();
    }

    private sealed class Listener(IAudioSource source, AudioBinding binding)
    {
        public IAudioSource Source  { get; } = source;
        public AudioBinding Binding { get; } = binding;

        public CancellationTokenSource Stop { get; } = new();

        public Task? Task { get; set; }

        public async ValueTask StopAsync()
        {
            await Stop.CancelAsync().ConfigureAwait(false);

            if (Task is not null)
            {
                try { await Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception) { /* shutting down */ }
            }

            await Source.DisposeAsync().ConfigureAwait(false);

            Stop.Dispose();
        }
    }
}
