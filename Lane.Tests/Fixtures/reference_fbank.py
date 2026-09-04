"""
Generates the reference log-mel features that Lane.Tests checks FilterBank against.

The waveform is defined by a formula rather than shipped as audio, so the C# test can
rebuild the identical samples and only the expected output needs to be committed.

Invoked exactly the way WeSpeaker invokes it, including the scaling to 16-bit range.
"""
import math
import torch
import torchaudio.compliance.kaldi as kaldi

SAMPLE_RATE = 16000
SAMPLES = 8000                      # half a second

# A voice-shaped signal: a low fundamental with three formant-ish partials above it.
# Integer amplitudes and a plain round() so both languages land on the same int16.
PARTIALS = [(130.0, 3000.0), (440.0, 1500.0), (1970.0, 800.0), (3300.0, 400.0)]


def waveform():
    out = []
    for i in range(SAMPLES):
        v = 0.0
        for freq, amp in PARTIALS:
            v += amp * math.sin(2.0 * math.pi * freq * i / SAMPLE_RATE)
        out.append(float(int(math.floor(v + 0.5))))
    return out


def main():
    samples = waveform()

    # WeSpeaker scales a [-1, 1] waveform up to 16-bit range before calling fbank; these
    # samples are already at that scale, so they go in unchanged.
    wav = torch.tensor([samples], dtype=torch.float32)

    feats = kaldi.fbank(
        wav,
        num_mel_bins=80,
        frame_length=25,
        frame_shift=10,
        dither=0.0,
        sample_frequency=SAMPLE_RATE,
    )

    # Per-utterance mean normalisation, as WeSpeaker does after fbank.
    feats = feats - torch.mean(feats, dim=0)

    frames, bins = feats.shape
    print(f"# {frames} frames x {bins} bins", flush=True)

    with open("fbank-reference.txt", "w") as f:
        f.write(f"{frames} {bins}\n")
        for frame in feats.tolist():
            f.write(" ".join(f"{v:.6f}" for v in frame) + "\n")

    print("wrote fbank-reference.txt")
    print("first frame, first 6 bins:", [round(v, 4) for v in feats[0][:6].tolist()])


if __name__ == "__main__":
    main()
