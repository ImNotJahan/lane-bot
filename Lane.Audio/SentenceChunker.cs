using System.Text;

namespace Lane.Audio;

/// <summary>
/// Breaks streamed text into pieces worth speaking.
///
/// Speech cannot start until there is a clause to say, but waiting for the whole reply
/// means waiting for the whole reply. Flushing on sentence boundaries — or on length, when
/// a sentence refuses to end — is what gets first audio out in a few hundred milliseconds
/// instead of several seconds.
/// </summary>
/// <param name="firstChunkMaxLength">
/// The first clause is cut shorter than the rest on purpose. Everything before Lane starts
/// talking is dead air, and the listener cannot tell whether a long opening clause means
/// she is thinking or broken. Later chunks can be longer because by then she is already
/// speaking and the buffer is running ahead of playback.
/// </param>
public sealed class SentenceChunker(int maxLength = 200, int minLength = 12, int firstChunkMaxLength = 70)
{
    private readonly StringBuilder _buffer = new();

    private bool _spokenYet;

    /// <summary>
    /// Adds streamed text and returns any pieces now ready to speak.
    /// </summary>
    public IReadOnlyList<string> Add(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        List<string> ready = [];

        foreach (char c in text)
        {
            _buffer.Append(c);

            if (ShouldFlush(c)) TakeInto(ready);
        }

        return ready;
    }

    /// <summary>Whatever is left when the stream ends.</summary>
    public string? Flush()
    {
        string remaining = _buffer.ToString().Trim();

        _buffer.Clear();

        return remaining.Length > 0 ? remaining : null;
    }

    /// <summary>How long this chunk may grow before it is broken by force.</summary>
    private int Ceiling => _spokenYet ? maxLength : firstChunkMaxLength;

    private bool ShouldFlush(char last)
    {
        if (_buffer.Length < minLength) return false;

        // A sentence ending, but not mid-decimal or mid-ellipsis.
        if (last is '.' or '!' or '?' or '\n')
        {
            // "3.5" and "e.g." should not each become an utterance.
            if (last == '.' && _buffer.Length >= 2 && char.IsAsciiDigit(_buffer[^2])) return false;

            return true;
        }

        // A clause boundary, once it is long enough to be worth saying on its own.
        if (last is ';' or ':' or ',' && _buffer.Length >= Ceiling / 2) return true;

        // Nothing has ended and it has gone on long enough — break by force.
        return _buffer.Length >= Ceiling;
    }

    private void TakeInto(List<string> ready)
    {
        string text = _buffer.ToString();

        // Only break by force when length demanded it; a real sentence ending is exact.
        if (text.Length >= Ceiling && !EndsClause(text[^1]))
        {
            int cut = BreakPoint(text);

            if (cut > minLength)
            {
                ready.Add(text[..cut].Trim());

                _buffer.Clear();
                _buffer.Append(text[cut..].TrimStart());

                _spokenYet = true;

                return;
            }
        }

        string trimmed = text.Trim();

        if (trimmed.Length > 0)
        {
            ready.Add(trimmed);
            _spokenYet = true;
        }

        _buffer.Clear();
    }

    /// <summary>
    /// Where to cut a run that will not end on its own. A comma is far better than an
    /// arbitrary space — "creating a temporary ecosystem" / "teeming with life" sounds
    /// like a fault, where a clause break just sounds like a pause.
    /// </summary>
    private static int BreakPoint(string text)
    {
        int comma = text.LastIndexOf(',');

        if (comma > text.Length / 3) return comma + 1;

        int space = text.LastIndexOf(' ');

        return space > 0 ? space : text.Length;
    }

    private static bool EndsClause(char c) => c is '.' or '!' or '?' or '\n' or ';' or ':' or ',';
}
