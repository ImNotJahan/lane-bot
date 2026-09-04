using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;

namespace Lane.Tests;

/// <summary>
/// Collects what was written down, and who it was written down as.
///
/// Attribution is only observable here. A model request carries the words but not the
/// speaker, and a reply carries neither, so a test that asserted on either would pass just
/// as happily with everyone in a room filed under one name — which is the exact fault the
/// tests using this exist to catch.
/// </summary>
internal sealed class RecordingTranscript : ITranscriptStore
{
    private readonly List<LaneMessage> _messages = [];
    private readonly Lock _gate = new();

    private long _sequence;

    public ValueTask<long> AppendAsync(LaneMessage message, CancellationToken ct)
    {
        lock (_gate)
        {
            long sequence = ++_sequence;

            _messages.Add(message with { Sequence = sequence });

            return ValueTask.FromResult(sequence);
        }
    }

    public ValueTask<IReadOnlyList<LaneMessage>> ReadAsync(TranscriptQuery query, CancellationToken ct)
    {
        lock (_gate)
        {
            IEnumerable<LaneMessage> found = _messages;

            if (query.Session is not null)
                found = found.Where(m => m.Session?.Value == query.Session.Value);

            if (query.GlobalUserId is not null)
                found = found.Where(m => m.Author?.GlobalUserId == query.GlobalUserId);

            return ValueTask.FromResult<IReadOnlyList<LaneMessage>>([.. found.Take(query.Limit)]);
        }
    }

    /// <summary>What each person was heard to say, keyed by the name they were written down as.</summary>
    public ILookup<string, string> Said
    {
        get
        {
            lock (_gate)
                return Spoken().ToLookup(m => m.Author!.DisplayName, m => m.TextContent);
        }
    }

    public IReadOnlyList<Participant> Speakers
    {
        get { lock (_gate) return [.. Spoken().Select(m => m.Author!)]; }
    }

    private IEnumerable<LaneMessage> Spoken() =>
        _messages.Where(m => m.Role == LaneRole.User && m.Author is not null);
}
