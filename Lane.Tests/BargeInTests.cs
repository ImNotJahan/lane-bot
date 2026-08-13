using Lane.Audio;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Being able to interrupt her.
///
/// The hard part is not stopping the audio — it is not stopping it by accident. Interim
/// transcripts are noisy, and a microphone in the same room hears Lane's own voice, so a
/// naive gate cuts her off on a cough or on her own first syllable.
/// </summary>
public sealed class BargeInTests
{
    private static readonly SessionId Session =
        new(new SurfaceId("discord.main"), SessionKind.Voice, "vc");

    private static readonly SessionId Elsewhere =
        new(new SurfaceId("discord.main"), SessionKind.Voice, "other-vc");

    private sealed class RecordingKernel : IAgentKernel
    {
        public List<(SessionId Session, string Reason)> Cancelled { get; } = [];

        public ValueTask SubmitAsync(InboundEvent evt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask PostAsync(SessionId session, SessionWorkItem item, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> CancelTurnAsync(SessionId session, string reason)
        {
            Cancelled.Add((session, reason));
            return ValueTask.FromResult(true);
        }
    }

    private static (VoiceFloor Floor, RecordingKernel Kernel, FakeTimeProvider Time) Build(
        BargeInOptions? options = null)
    {
        RecordingKernel kernel = new();
        FakeTimeProvider time = new();

        VoiceFloor floor = new(
            () => kernel,
            options ?? new BargeInOptions { MinimumSpeakingTime = TimeSpan.Zero, MinimumWords = 2 },
            NullLogger<VoiceFloor>.Instance,
            time);

        return (floor, kernel, time);
    }

    private static Transcript Interim(string text) => new(text, IsFinal: false, TimeSpan.Zero);

    [Fact]
    public void Talking_over_her_cuts_her_off_and_ends_the_turn()
    {
        (VoiceFloor floor, RecordingKernel kernel, _) = Build();

        using CancellationTokenSource speech = new();
        using IDisposable held = floor.BeginSpeaking(Session, speech);

        floor.NoticeSpeech(Session, Interim("hang on a moment"));

        Assert.True(speech.IsCancellationRequested);

        // The turn is cancelled too: generating a reply nobody is listening to costs money
        // and puts her further behind the conversation.
        Assert.Equal(Session, Assert.Single(kernel.Cancelled).Session);
    }

    [Fact]
    public void A_single_stray_word_is_not_an_interruption()
    {
        // A cough, a chair, a stray "mm" all produce an interim transcript.
        (VoiceFloor floor, RecordingKernel kernel, _) = Build();

        using CancellationTokenSource speech = new();
        using IDisposable held = floor.BeginSpeaking(Session, speech);

        floor.NoticeSpeech(Session, Interim("mm"));

        Assert.False(speech.IsCancellationRequested);
        Assert.Empty(kernel.Cancelled);
    }

    [Fact]
    public void She_is_given_a_moment_before_she_can_be_cut_off()
    {
        // Without this, a microphone in the room picks up her own first syllable and stops
        // her immediately, every single time.
        (VoiceFloor floor, RecordingKernel kernel, FakeTimeProvider time) =
            Build(new BargeInOptions { MinimumSpeakingTime = TimeSpan.FromMilliseconds(700), MinimumWords = 2 });

        using CancellationTokenSource speech = new();
        using IDisposable held = floor.BeginSpeaking(Session, speech);

        floor.NoticeSpeech(Session, Interim("wait what"));
        Assert.False(speech.IsCancellationRequested);

        time.Advance(TimeSpan.FromSeconds(1));

        floor.NoticeSpeech(Session, Interim("wait what"));
        Assert.True(speech.IsCancellationRequested);
    }

    [Fact]
    public void Speech_in_another_conversation_does_not_silence_her_here()
    {
        // Floor state is per session: two people talking over each other in one room must
        // not stop her talking in a different one.
        (VoiceFloor floor, RecordingKernel kernel, _) = Build();

        using CancellationTokenSource speech = new();
        using IDisposable held = floor.BeginSpeaking(Session, speech);

        floor.NoticeSpeech(Elsewhere, Interim("something over here"));

        Assert.False(speech.IsCancellationRequested);
        Assert.Empty(kernel.Cancelled);
    }

    [Fact]
    public void Speech_while_she_is_silent_interrupts_nothing()
    {
        (VoiceFloor floor, RecordingKernel kernel, _) = Build();

        floor.NoticeSpeech(Session, Interim("just talking normally"));

        Assert.Empty(kernel.Cancelled);
        Assert.False(floor.IsSpeaking(Session));
    }

    [Fact]
    public void She_is_only_interrupted_once_per_utterance()
    {
        // Every interim result during an interruption would otherwise cancel the turn again.
        (VoiceFloor floor, RecordingKernel kernel, _) = Build();

        using CancellationTokenSource speech = new();
        using IDisposable held = floor.BeginSpeaking(Session, speech);

        floor.NoticeSpeech(Session, Interim("hang on a moment"));
        floor.NoticeSpeech(Session, Interim("hang on a moment I said"));
        floor.NoticeSpeech(Session, Interim("hang on a moment I said please"));

        Assert.Single(kernel.Cancelled);
    }

    [Fact]
    public void Finishing_speaking_releases_the_floor()
    {
        (VoiceFloor floor, _, _) = Build();

        using CancellationTokenSource speech = new();

        IDisposable held = floor.BeginSpeaking(Session, speech);
        Assert.True(floor.IsSpeaking(Session));

        held.Dispose();
        Assert.False(floor.IsSpeaking(Session));
    }

    [Fact]
    public void Barge_in_can_be_turned_off_entirely()
    {
        (VoiceFloor floor, RecordingKernel kernel, _) = Build(new BargeInOptions { Enabled = false });

        using CancellationTokenSource speech = new();
        using IDisposable held = floor.BeginSpeaking(Session, speech);

        floor.NoticeSpeech(Session, Interim("stop talking please"));

        Assert.False(speech.IsCancellationRequested);
        Assert.Empty(kernel.Cancelled);
    }

    /// <summary>A clock the test moves by hand, so timing rules are checked rather than waited out.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
