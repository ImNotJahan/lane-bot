namespace Lane.Audio.Recognition;

/// <summary>
/// The last few seconds of what was sent to a recogniser, so an utterance's audio can be
/// taken back out of it once the recogniser says where that utterance was.
///
/// It exists because the two halves arrive at different times and in different directions.
/// Audio is pushed in continuously, as it is captured; the recogniser answers later, out of
/// band, with an offset and a duration into that same stream. Nothing else in the pipeline
/// still holds the samples by then — frames are handed to the recogniser and dropped — so a
/// voiceprint could not be taken at all without keeping a window open behind the write head.
///
/// Deliberately a fixed window rather than a growing one: a recogniser that stalls or a
/// speaker who never stops must cost a known amount of memory, and audio old enough to have
/// rolled out of the window is audio nobody is going to ask about.
/// </summary>
internal sealed class UtteranceBuffer(AudioFormat format, TimeSpan window)
{
    private readonly byte[] _buffer = new byte[Math.Max(format.BytesFor(window), format.BytesPerFrame)];

    private readonly Lock _gate = new();

    /// <summary>Total bytes ever written. The stream position of the write head.</summary>
    private long _written;

    public int Capacity => _buffer.Length;

    /// <summary>Appends audio at the write head, overwriting whatever has rolled out behind it.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length == 0) return;

        lock (_gate)
        {
            // A block bigger than the window can only leave its own tail behind, so only the
            // tail is copied. Without this the modulo wrap below would write past the end.
            if (pcm.Length >= _buffer.Length)
            {
                _written += pcm.Length;

                pcm[^_buffer.Length..].CopyTo(_buffer);

                return;
            }

            int head = (int)(_written % _buffer.Length);
            int first = Math.Min(pcm.Length, _buffer.Length - head);

            pcm[..first].CopyTo(_buffer.AsSpan(head));

            if (first < pcm.Length) pcm[first..].CopyTo(_buffer);

            _written += pcm.Length;
        }
    }

    /// <summary>
    /// The audio between two points in the stream, or empty if it has already rolled past.
    ///
    /// Empty is a normal answer, not a failure: it means the recogniser fell far enough
    /// behind that the samples are gone, and the caller should fall back to what the label
    /// alone can tell it rather than take a voiceprint from the wrong person's words.
    /// </summary>
    public ReadOnlyMemory<byte> Slice(TimeSpan offset, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return ReadOnlyMemory<byte>.Empty;

        lock (_gate)
        {
            long start = Align(format.BytesFor(offset));
            long end   = Align(start + format.BytesFor(duration));

            // Never read ahead of the write head: the recogniser can report an utterance
            // whose last samples this buffer has not been handed yet.
            end = Math.Min(end, _written);

            // Nor behind the window, which is the case this whole class is bounded by.
            if (start < _written - _buffer.Length || end <= start) return ReadOnlyMemory<byte>.Empty;

            byte[] slice = new byte[end - start];

            int from  = (int)(start % _buffer.Length);
            int first = Math.Min(slice.Length, _buffer.Length - from);

            _buffer.AsSpan(from, first).CopyTo(slice);

            if (first < slice.Length) _buffer.AsSpan(0, slice.Length - first).CopyTo(slice.AsSpan(first));

            return slice;
        }
    }

    /// <summary>
    /// Rounds to a whole sample. A slice that starts half way through one turns every
    /// sample after it into noise, which is the kind of fault that degrades a voiceprint
    /// rather than failing it.
    /// </summary>
    private long Align(long bytes) => bytes - bytes % format.BytesPerFrame;
}
