using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lane.Audio;
using Lane.Core.Agent;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Surfaces.Api.Voice;
using Lane.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// A client application as a microphone and a speaker.
///
/// This is the practical answer to "there will be multiple microphones": local capture is
/// Windows-only through NAudio, so a phone, a browser tab or a helper process becomes an
/// audio source by opening a socket.
/// </summary>
public sealed class ApiVoiceTests
{
    private static readonly SessionId Session =
        new(new SurfaceId("api"), SessionKind.Voice, "alpha/main");

    [Fact]
    public void Sixteen_kilohertz_mono_is_passed_through_untouched()
    {
        // It is already what recognition wants. Converting it would only add a chance to
        // get it wrong.
        ApiAudioSource source = new(new AudioSourceId("test"), AudioFormat.Pcm16kMono, "someone");

        byte[] pcm = Pcm(320, i => (short)(i * 37));

        source.Write(pcm);

        AudioFrame frame = Assert.Single(Drain(source));

        Assert.Equal(AudioFormat.Pcm16kMono, frame.Format);
        Assert.Equal(pcm, frame.Pcm.ToArray());
    }

    [Fact]
    public void Forty_eight_kilohertz_is_decimated_to_what_recognition_wants()
    {
        // Browsers capture at 48 kHz. It goes through the same decimation the Discord source
        // uses — code already checked sample-for-sample against v2's.
        ApiAudioSource source = new(new AudioSourceId("test"), new AudioFormat(48000, 1, 16), "someone");

        source.Write(Pcm(4800, i => (short)(8000 * Math.Sin(i * 0.05))));

        AudioFrame frame = Assert.Single(Drain(source));

        Assert.Equal(AudioFormat.Pcm16kMono, frame.Format);

        // 3:1, and 16-bit throughout.
        Assert.Equal(4800 / 3, frame.Pcm.Length / 2);
    }

    [Theory]
    [InlineData(16000, 1, true)]
    [InlineData(48000, 1, true)]
    [InlineData(48000, 2, true)]
    [InlineData(44100, 1, false)]      // needs a resampler that would have to run statefully
    [InlineData(16000, 2, false)]
    [InlineData(8000,  1, false)]
    public void Only_formats_with_a_tested_conversion_are_accepted(int rate, int channels, bool supported) =>
        Assert.Equal(supported, ApiAudioSource.IsSupported(new AudioFormat(rate, channels, 16)));

    [Fact]
    public void An_unsupported_format_is_refused_at_construction()
    {
        // Rather than accepted and quietly mangled, which shows up later as Lane mishearing
        // and gets blamed on the recogniser.
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new ApiAudioSource(new AudioSourceId("test"), new AudioFormat(44100, 2, 16), null));

        Assert.Contains("44100", error.Message);
    }

    [Fact]
    public async Task Audio_and_text_both_reach_the_client()
    {
        // A client showing a transcript beside the audio should not need a second connection.
        FakeSocket socket = new();

        ApiVoiceOutput output = new(Session, socket, new AudioFormat(24000, 1, 16), NullLogger.Instance);

        await output.PlayAsync(Frames(2, output.Format), CancellationToken.None);
        await output.SendAsync(new OutboundText("said out loud"), CancellationToken.None);

        Assert.Equal(2, socket.Audio.Count);

        Assert.Equal(["speaking", "speaking", "message"], socket.Events.Select(e => e.Name));
        Assert.Contains("said out loud", socket.Events[^1].Json);
    }

    [Fact]
    public async Task Two_utterances_never_interleave_on_one_socket()
    {
        // A socket takes one send at a time, and two clauses mixed together is noise rather
        // than merely out of order.
        FakeSocket socket = new();

        ApiVoiceOutput output = new(Session, socket, new AudioFormat(24000, 1, 16), NullLogger.Instance);

        await Task.WhenAll(
            output.PlayAsync(Frames(20, output.Format, tag: 1), CancellationToken.None),
            output.PlayAsync(Frames(20, output.Format, tag: 2), CancellationToken.None));

        // Every frame of one utterance before any of the other.
        List<byte> tags = [.. socket.Audio.Select(a => a[0])];

        int changes = tags.Zip(tags.Skip(1)).Count(pair => pair.First != pair.Second);

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task The_voice_channel_offers_both_kinds_of_output()
    {
        // Resolved by capability, exactly as a Discord channel is — the kernel never learns
        // that this one happens to be a WebSocket.
        await using LaneHarness harness = LaneHarness.Create();

        FakeSocket socket = new();

        ApiVoiceOutput output = new(Session, socket, AudioFormat.Pcm16kMono, NullLogger.Instance);

        harness.Sessions.GetOrCreate(new SessionDescriptor
        {
            Id           = Session,
            DisplayName  = "voice",
            MemoryGroup  = "api/alpha/main",
            Capabilities = output.Capabilities
        });

        using IDisposable attachment = harness.Sessions.Attach(output);

        Assert.True(harness.Sessions.TryGet(Session, out Session? session));

        Assert.Single(session!.ResolveOutputs<IVoiceOutput>(DeliveryTarget.Primary));
        Assert.Single(session.ResolveOutputs<ITextOutput>(DeliveryTarget.Primary));
    }

    [Fact]
    public async Task A_client_speaks_and_is_answered_over_one_socket()
    {
        // The whole path, with only the recogniser and synthesiser faked: bytes in over the
        // socket, through the source, the router, the session pump and the model, and back
        // out as both text and audio.
        await using ApiFixture api = await ApiFixture.StartAsync(
            ScriptedLanguageModel.Transforming(said => $"you said {said}"), audio: true);

        using ClientWebSocket client = new();

        // In the query, not a header: browsers cannot set headers on a WebSocket handshake,
        // and a client app in a browser tab is the main reason this endpoint exists.
        await client.ConnectAsync(
            api.WebSocketUri("/v1/sessions/main/voice?key=alpha-key&rate=16000&channels=1"),
            CancellationToken.None);

        Assert.Equal("ready", (await ReceiveEventAsync(client)).GetProperty("type").GetString());

        await client.SendAsync(
            Encoding.UTF8.GetBytes("hello there"), WebSocketMessageType.Binary, true, CancellationToken.None);

        List<JsonElement> events = [];
        List<byte[]> audio = [];

        // Read until the text arrives; the speaking markers and the audio come before it.
        while (!events.Any(e => e.GetProperty("type").GetString() == "message"))
        {
            (JsonElement? evt, byte[]? binary) = await ReceiveAsync(client);

            if (evt is { } json) events.Add(json);
            if (binary is not null) audio.Add(binary);
        }

        Assert.Equal("you said hello there",
            events.Single(e => e.GetProperty("type").GetString() == "message")
                  .GetProperty("data").GetProperty("text").GetString());

        // She was marked as speaking around it, so a client can show that she is talking.
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "speaking");

        // And the same words came back as audio, on the same socket.
        Assert.Contains(audio, a => Encoding.UTF8.GetString(a).Contains("you said hello there"));
    }

    [Fact]
    public async Task Two_sockets_hold_two_separate_voice_conversations()
    {
        await using ApiFixture api = await ApiFixture.StartAsync(
            ScriptedLanguageModel.Transforming(said => $"heard {said}"), audio: true);

        using ClientWebSocket alpha = new();
        using ClientWebSocket beta  = new();

        await alpha.ConnectAsync(api.WebSocketUri("/v1/sessions/main/voice?key=alpha-key"), CancellationToken.None);
        await beta.ConnectAsync(api.WebSocketUri("/v1/sessions/main/voice?key=beta-key"), CancellationToken.None);

        await ReceiveEventAsync(alpha);
        await ReceiveEventAsync(beta);

        await alpha.SendAsync(
            Encoding.UTF8.GetBytes("alpha talking"), WebSocketMessageType.Binary, true, CancellationToken.None);

        string reply = await FirstMessageAsync(alpha);

        Assert.Equal("heard alpha talking", reply);

        // Beta was never spoken to, and hears nothing. Both said "main"; they are not the
        // same conversation.
        Assert.False(await HasPendingMessageAsync(beta));
    }

    [Fact]
    public async Task The_socket_refuses_a_format_it_cannot_convert()
    {
        await using ApiFixture api = await ApiFixture.StartAsync(audio: true);

        using ClientWebSocket client = new();

        client.Options.SetRequestHeader("Authorization", "Bearer alpha-key");

        // Refused at the handshake rather than accepted and quietly mangled.
        await Assert.ThrowsAsync<WebSocketException>(() =>
            client.ConnectAsync(api.WebSocketUri("/v1/sessions/main/voice?rate=44100"), CancellationToken.None));
    }

    [Fact]
    public async Task A_voice_socket_without_a_key_is_refused()
    {
        await using ApiFixture api = await ApiFixture.StartAsync(audio: true);

        using ClientWebSocket client = new();

        await Assert.ThrowsAsync<WebSocketException>(() =>
            client.ConnectAsync(api.WebSocketUri("/v1/sessions/main/voice"), CancellationToken.None));
    }

    [Fact]
    public async Task A_voice_socket_is_refused_when_audio_is_not_configured()
    {
        // Costs the socket, not the surface: every text endpoint keeps working, and the
        // client is told why rather than left guessing.
        await using ApiFixture api = await ApiFixture.StartAsync();

        using ClientWebSocket client = new();

        client.Options.SetRequestHeader("Authorization", "Bearer alpha-key");

        await Assert.ThrowsAsync<WebSocketException>(() =>
            client.ConnectAsync(api.WebSocketUri("/v1/sessions/main/voice"), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await api.Client().GetAsync("/v1/health")).StatusCode);
    }

    // ---- observer composition ---------------------------------------------

    [Fact]
    public async Task A_turn_can_be_watched_by_more_than_one_thing()
    {
        // A voice conversation held over the API wants audio and text deltas at once.
        // Picking a single observer would silently starve one of them.
        CountingObserver speech = new();
        CountingObserver text   = new();

        IAgentObserver both = CompositeAgentObserver.Of([speech, text])!;

        await both.OnTextAsync("hello", CancellationToken.None);
        await both.OnToolStartAsync("search", CancellationToken.None);
        await both.OnFinishedAsync(CancellationToken.None);

        Assert.Equal("hello", speech.Text);
        Assert.Equal("hello", text.Text);
        Assert.Equal(1, speech.Tools);
        Assert.Equal(1, text.Tools);
        Assert.True(speech.Finished && text.Finished);
    }

    [Fact]
    public async Task One_failing_observer_does_not_starve_the_others()
    {
        CountingObserver working = new();

        IAgentObserver both = CompositeAgentObserver.Of([new ThrowingObserver(), working])!;

        await both.OnTextAsync("still delivered", CancellationToken.None);

        Assert.Equal("still delivered", working.Text);
    }

    [Fact]
    public void One_observer_is_used_directly_rather_than_wrapped()
    {
        CountingObserver only = new();

        Assert.Same(only, CompositeAgentObserver.Of([only]));
        Assert.Null(CompositeAgentObserver.Of([]));
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<(JsonElement? Event, byte[]? Binary)> ReceiveAsync(
        ClientWebSocket socket, int timeoutMs = 5000)
    {
        byte[] buffer = new byte[64 * 1024];

        using CancellationTokenSource timeout = new(timeoutMs);

        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, timeout.Token);

        if (result.MessageType == WebSocketMessageType.Binary)
            return (null, buffer[..result.Count]);

        return (JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone(), null);
    }

    private static async Task<JsonElement> ReceiveEventAsync(ClientWebSocket socket)
    {
        while (true)
        {
            (JsonElement? evt, _) = await ReceiveAsync(socket);

            if (evt is { } json) return json;
        }
    }

    private static async Task<string> FirstMessageAsync(ClientWebSocket socket)
    {
        while (true)
        {
            JsonElement evt = await ReceiveEventAsync(socket);

            if (evt.GetProperty("type").GetString() == "message")
                return evt.GetProperty("data").GetProperty("text").GetString()!;
        }
    }

    private static async Task<bool> HasPendingMessageAsync(ClientWebSocket socket)
    {
        try
        {
            await ReceiveAsync(socket, timeoutMs: 700);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static byte[] Pcm(int samples, Func<int, short> value)
    {
        byte[] pcm = new byte[samples * 2];

        for (int i = 0; i < samples; i++) BitConverter.GetBytes(value(i)).CopyTo(pcm, i * 2);

        return pcm;
    }

    private static List<AudioFrame> Drain(ApiAudioSource source)
    {
        List<AudioFrame> frames = [];

        using CancellationTokenSource stop = new(TimeSpan.FromMilliseconds(200));

        try
        {
            IAsyncEnumerator<AudioFrame> reader = source.ReadAsync(stop.Token).GetAsyncEnumerator(stop.Token);

            while (reader.MoveNextAsync().AsTask().GetAwaiter().GetResult()) frames.Add(reader.Current);
        }
        catch (OperationCanceledException) { /* drained what was there */ }

        return frames;
    }

    private static async IAsyncEnumerable<AudioFrame> Frames(int count, AudioFormat format, byte tag = 0)
    {
        for (int i = 0; i < count; i++)
        {
            byte[] pcm = new byte[64];
            pcm[0] = tag;

            yield return AudioFrame.Of(pcm, format);

            await Task.Yield();
        }
    }

    private sealed class FakeSocket : IVoiceSocket
    {
        private readonly Lock _gate = new();

        public List<byte[]> Audio { get; } = [];

        public List<(string Name, string Json)> Events { get; } = [];

        public bool IsOpen => true;

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
        {
            lock (_gate) Audio.Add(pcm.ToArray());

            return Task.CompletedTask;
        }

        public Task SendEventAsync<T>(string name, T payload, CancellationToken ct)
        {
            lock (_gate) Events.Add((name, JsonSerializer.Serialize(payload)));

            return Task.CompletedTask;
        }
    }

    private sealed class CountingObserver : IAgentObserver
    {
        private readonly StringBuilder _text = new();

        public string Text => _text.ToString();
        public int    Tools { get; private set; }
        public bool   Finished { get; private set; }

        public ValueTask OnTextAsync(string delta, CancellationToken ct)
        {
            _text.Append(delta);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnToolStartAsync(string name, CancellationToken ct)
        {
            Tools++;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnToolEndAsync(string name, ToolResult result, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask OnFinishedAsync(CancellationToken ct)
        {
            Finished = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingObserver : IAgentObserver
    {
        public ValueTask OnTextAsync(string delta, CancellationToken ct) => throw new InvalidOperationException("no");

        public ValueTask OnToolStartAsync(string name, CancellationToken ct) => throw new InvalidOperationException("no");

        public ValueTask OnToolEndAsync(string name, ToolResult result, CancellationToken ct) =>
            throw new InvalidOperationException("no");

        public ValueTask OnFinishedAsync(CancellationToken ct) => throw new InvalidOperationException("no");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
