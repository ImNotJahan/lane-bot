using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Recording;

public sealed class ResponseRecordingOptions
{
    public bool Enabled { get; set; }

    /// <summary>Relative paths resolve against the assembly directory.</summary>
    public string Directory { get; set; } = "training";

    /// <summary>Records arriving while this many are waiting to be written are dropped.</summary>
    public int QueueCapacity { get; set; } = 1024;
}

public interface IResponseRecorder
{
    /// <summary>Queues one model call to be written. Never blocks and never throws.</summary>
    void Record(ModelDescriptor model, ModelRequest request, ModelResponse response);
}

/// <summary>
/// Appends each model call, name-redacted, to <c>responses-yyyy-MM-dd.jsonl</c> (by UTC date) on a
/// background task. Names are taken from the request's authors and from <c>roster</c> at the time
/// of the call.
/// </summary>
public sealed class JsonlResponseRecorder : IResponseRecorder, IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string                          _directory;
    private readonly Func<IEnumerable<Participant>>  _roster;
    private readonly TimeProvider                    _time;
    private readonly ILogger<JsonlResponseRecorder>  _log;
    private readonly Channel<Pending>                _queue;

    private Task? _pump;

    public JsonlResponseRecorder(
        ResponseRecordingOptions        options,
        Func<IEnumerable<Participant>>  roster,
        TimeProvider                    time,
        ILogger<JsonlResponseRecorder>  log)
    {
        _directory = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(AppContext.BaseDirectory, options.Directory);

        _roster = roster;
        _time   = time;
        _log    = log;

        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(Math.Max(1, options.QueueCapacity))
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
    }

    public string Directory => _directory;

    public void Record(ModelDescriptor model, ModelRequest request, ModelResponse response)
    {
        try
        {
            IReadOnlyList<LaneMessage> messages = [.. request.Messages];

            Pending pending = new(
                model,
                request with { Messages = messages, System = [.. request.System] },
                response,
                [.. messages.Select(m => m.Author), .. _roster()],
                _time.GetUtcNow());

            if (!_queue.Writer.TryWrite(pending))
                _log.LogWarning("Dropped a response record for {Model}: the queue is full or closed", model);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not queue a response record for {Model}", model);
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(_directory);

        _pump ??= Task.Run(PumpAsync, CancellationToken.None);

        _log.LogInformation("Recording model responses to {Directory}", _directory);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _queue.Writer.TryComplete();

        if (_pump is null) return;

        try
        {
            await _pump.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Stopped before every response record was written");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task PumpAsync()
    {
        Dictionary<string, StringBuilder> batch = [];

        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out Pending? pending))
            {
                try
                {
                    JsonObject record = ResponseRecord.Build(
                        pending.Model, pending.Request, pending.Response, new NameRedactor(pending.People), pending.At);

                    string path = Path.Combine(_directory, $"responses-{pending.At.UtcDateTime:yyyy-MM-dd}.jsonl");

                    if (!batch.TryGetValue(path, out StringBuilder? lines)) batch[path] = lines = new();

                    lines.Append(record.ToJsonString(Json)).Append('\n');
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not build a response record for {Model}", pending.Model);
                }
            }

            foreach ((string path, StringBuilder lines) in batch)
            {
                try
                {
                    await File.AppendAllTextAsync(path, lines.ToString()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not write response records to {Path}", path);
                }
            }

            batch.Clear();
        }
    }

    private sealed record Pending(
        ModelDescriptor            Model,
        ModelRequest               Request,
        ModelResponse              Response,
        IReadOnlyList<Participant> People,
        DateTimeOffset             At);
}
