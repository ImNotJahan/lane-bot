using NAudio.Wave;
using SoundTouch;

namespace Wizard.Head.Mouths
{
    public sealed class SoundTouchSampleProvider : ISampleProvider, IDisposable
    {
        private readonly ISampleProvider     _source;
        private readonly SoundTouchProcessor _processor;

        private readonly int     _channels;
        private readonly float[] _inputBuffer;
        private readonly float[] _outputBuffer;

        private int  _outputReadIndex;
        private int  _outputSamplesAvailable;
        private bool _sourceEnded;
        private bool _flushed;

        public SoundTouchSampleProvider(
            ISampleProvider source,
            float tempo               = 0f, // percent change, 0 = unchanged
            float pitchSemiTones      = 0f, // semitones,      0 = unchanged
            float rate                = 0f, // percent change, 0 = unchanged
            bool  tuneForSpeech       = true,
            int   inputFrameChunkSize = 4096
        )
        {
            _source   = source ?? throw new ArgumentNullException(nameof(source));
            _channels = source.WaveFormat.Channels;

            if (_channels <= 0) throw new ArgumentException("Source must have at least one channel.", nameof(source));

            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate,
                _channels
            );

            _processor = new SoundTouchProcessor
            {
                SampleRate     = source.WaveFormat.SampleRate,
                Channels       = _channels,
                TempoChange    = tempo,
                PitchSemiTones = pitchSemiTones,
                RateChange     = rate
            };

            if (tuneForSpeech)
            {
                _processor.SetSetting(SettingId.SequenceDurationMs,   40);
                _processor.SetSetting(SettingId.SeekWindowDurationMs, 15);
                _processor.SetSetting(SettingId.OverlapDurationMs,    8);
            }

            // SoundTouch works on float samples; official examples use 32-bit float processing.
            // inputFrameChunkSize is in frames, so multiply by channels for raw sample count.
            _inputBuffer = new float[inputFrameChunkSize * _channels];

            // Make output buffer larger because tempo changes can vary how much is available.
            _outputBuffer = new float[inputFrameChunkSize * _channels * 4];
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();

            int written = 0;

            while (written < count)
            {
                // First drain anything already produced by SoundTouch.
                if (_outputReadIndex < _outputSamplesAvailable)
                {
                    int available = _outputSamplesAvailable - _outputReadIndex;
                    int toCopy    = Math.Min(available, count - written);

                    Array.Copy(_outputBuffer, _outputReadIndex, buffer, offset + written, toCopy);

                    _outputReadIndex += toCopy;
                    written          += toCopy;

                    if (_outputReadIndex >= _outputSamplesAvailable)
                    {
                        _outputReadIndex        = 0;
                        _outputSamplesAvailable = 0;
                    }

                    continue;
                }

                // Refill SoundTouch output buffer.
                if (TryFillOutputBuffer()) continue;

                // Nothing more available.
                break;
            }

            return written;
        }

        private bool TryFillOutputBuffer()
        {
            _outputReadIndex        = 0;
            _outputSamplesAvailable = 0;

            // Try pulling already-buffered processed samples first.
            int receivedFrames = ReceiveProcessedFrames(_outputBuffer);

            if (receivedFrames > 0)
            {
                _outputSamplesAvailable = receivedFrames * _channels;
                return true;
            }

            // Feed more source audio if source not ended.
            if (!_sourceEnded)
            {
                int samplesRead = _source.Read(_inputBuffer, 0, _inputBuffer.Length);

                if (samplesRead > 0)
                {
                    int framesRead = samplesRead / _channels;
                    InputFrames(_inputBuffer, framesRead);

                    receivedFrames = ReceiveProcessedFrames(_outputBuffer);
                    if (receivedFrames > 0)
                    {
                        _outputSamplesAvailable = receivedFrames * _channels;
                        return true;
                    }
                }
                else
                {
                    _sourceEnded = true;
                }
            }

            // Flush once after source ends so trailing audio is emitted.
            if (_sourceEnded && !_flushed)
            {
                FlushProcessor();
                _flushed = true;

                receivedFrames = ReceiveProcessedFrames(_outputBuffer);

                if (receivedFrames > 0)
                {
                    _outputSamplesAvailable = receivedFrames * _channels;
                    return true;
                }
            }

            return false;
        }

        private void InputFrames(float[] samples, int frameCount) => _processor.PutSamples(samples, frameCount);

        private int ReceiveProcessedFrames(float[] destination)
        {
            return _processor.ReceiveSamples(destination, destination.Length / _channels);
        }

        private void FlushProcessor()
        {
            _processor.Flush();
        }

        public void Dispose()
        {
            _processor.Clear();

            if (_source is IDisposable d) d.Dispose();
        }
    }
}