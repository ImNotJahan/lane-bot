using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Lane.Surfaces.Api.Voice;

/// <summary>
/// A real WebSocket behind the voice contract.
///
/// The lock is the whole point: a socket permits one send at a time, and Lane's audio and
/// her text are produced by different tasks. Without it the two interleave into a protocol
/// error that closes the connection.
/// </summary>
public sealed class WebSocketVoiceSocket(WebSocket socket) : IVoiceSocket, IDisposable
{
    private readonly SemaphoreSlim _send = new(1, 1);

    public bool IsOpen => socket.State == WebSocketState.Open;

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        if (pcm.Length == 0) return;

        await SendAsync(pcm, WebSocketMessageType.Binary, ct).ConfigureAwait(false);
    }

    public async Task SendEventAsync<T>(string name, T payload, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(
            new VoiceEnvelope<T>(name, payload), ApiJson.Options);

        await SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, ct).ConfigureAwait(false);
    }

    private async Task SendAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (!IsOpen) return;

            await socket.SendAsync(bytes, type, endOfMessage: true, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!IsOpen)
        {
            // The client hung up mid-send. Nothing to report.
        }
        finally
        {
            _send.Release();
        }
    }

    public void Dispose() => _send.Dispose();

    private sealed record VoiceEnvelope<T>(string Type, T Data);
}
