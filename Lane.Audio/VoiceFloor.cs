using System.Collections.Concurrent;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Microsoft.Extensions.Logging;

namespace Lane.Audio;

public sealed class BargeInOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How much someone has to say before it counts as interrupting.
    ///
    /// Interim transcripts are noisy — a cough, a chair, a stray "mm" all produce one. A
    /// couple of words is the difference between being interruptible and being unable to
    /// finish a sentence.
    /// </summary>
    public int MinimumWords { get; set; } = 2;

    /// <summary>
    /// How long Lane is allowed to speak before she can be cut off.
    ///
    /// Without it, a listener's microphone picking up her own first syllable stops her
    /// immediately, every time.
    /// </summary>
    public TimeSpan MinimumSpeakingTime { get; set; } = TimeSpan.FromMilliseconds(700);
}

/// <summary>
/// Who currently has the floor in a voice conversation.
///
/// Per session, not global: two people talking over each other in one channel is that
/// channel's problem, and must not silence Lane somewhere else entirely.
/// </summary>
public interface IVoiceFloor
{
    /// <summary>Lane has started speaking. Disposing the token marks her finished.</summary>
    IDisposable BeginSpeaking(SessionId session, CancellationTokenSource speech);

    /// <summary>An interim transcript arrived. May cut Lane off.</summary>
    void NoticeSpeech(SessionId session, Transcript transcript);

    /// <summary>Someone finished an utterance.</summary>
    void NoticeFinal(SessionId session);

    bool IsSpeaking(SessionId session);
}

/// <param name="kernel">
/// Resolved lazily, and deliberately so. The kernel's pipeline builds the observer factory,
/// which needs this floor — taking the kernel in the constructor closes that loop and
/// deadlocks startup before a single surface comes up.
/// </param>
public sealed class VoiceFloor(
    Func<IAgentKernel> kernel,
    BargeInOptions options,
    ILogger<VoiceFloor> log,
    TimeProvider? time = null) : IVoiceFloor
{
    private readonly ConcurrentDictionary<string, Speaking> _speaking = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public IDisposable BeginSpeaking(SessionId session, CancellationTokenSource speech)
    {
        Speaking held = new(speech, _time.GetUtcNow());

        _speaking[session.Value] = held;

        return new Floor(this, session, held);
    }

    public bool IsSpeaking(SessionId session) => _speaking.ContainsKey(session.Value);

    public void NoticeSpeech(SessionId session, Transcript transcript)
    {
        if (!options.Enabled) return;

        if (!_speaking.TryGetValue(session.Value, out Speaking? held)) return;

        if (_time.GetUtcNow() - held.Since < options.MinimumSpeakingTime) return;

        if (CountWords(transcript.Text) < options.MinimumWords) return;

        if (!held.Interrupt()) return;

        log.LogInformation("Interrupted in {Session} by \"{Text}\"", session, transcript.Text);

        // Cancels the turn as well as the audio: continuing to generate a reply nobody is
        // listening to costs money and puts her further behind the conversation.
        _ = kernel().CancelTurnAsync(session, "interrupted");
    }

    public void NoticeFinal(SessionId session) { }

    private static int CountWords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private sealed class Speaking(CancellationTokenSource speech, DateTimeOffset since)
    {
        private int _interrupted;

        public DateTimeOffset Since { get; } = since;

        public bool Interrupt()
        {
            if (Interlocked.Exchange(ref _interrupted, 1) != 0) return false;

            try { speech.Cancel(); }
            catch (ObjectDisposedException) { return false; }

            return true;
        }
    }

    private sealed class Floor(VoiceFloor owner, SessionId session, Speaking held) : IDisposable
    {
        public void Dispose() =>
            owner._speaking.TryRemove(new KeyValuePair<string, Speaking>(session.Value, held));
    }
}

/// <summary>Used where there is no voice at all, so callers need no null checks.</summary>
public sealed class NoVoiceFloor : IVoiceFloor
{
    public IDisposable BeginSpeaking(SessionId session, CancellationTokenSource speech) => Nothing.Instance;

    public void NoticeSpeech(SessionId session, Transcript transcript) { }

    public void NoticeFinal(SessionId session) { }

    public bool IsSpeaking(SessionId session) => false;

    private sealed class Nothing : IDisposable
    {
        public static Nothing Instance { get; } = new();
        public void Dispose() { }
    }
}
