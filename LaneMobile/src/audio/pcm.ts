/**
 * Between the Web Audio world, which is float, and the wire, which is 16-bit signed PCM.
 *
 * ApiAudioSource accepts nothing else: 16 kHz mono or 48 kHz mono/stereo, 16 bits, little
 * endian. Anything outside that is refused at the handshake rather than resampled.
 */

/** Clamped, because a sample past unity wraps to the opposite sign and sounds like a click. */
export function floatToPcm16(samples: Float32Array): ArrayBuffer {
  const out = new Int16Array(samples.length);

  for (let i = 0; i < samples.length; i++) {
    const sample = Math.max(-1, Math.min(1, samples[i]));

    out[i] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;
  }

  return out.buffer;
}

// The buffer type is pinned: copyToChannel refuses a view that might be backed by a
// SharedArrayBuffer, and the default Float32Array is exactly that union.
export function pcm16ToFloat(pcm: ArrayBuffer): Float32Array<ArrayBuffer> {
  // An odd length would mean a truncated sample; dropping the stray byte keeps the rest
  // aligned, where letting Int16Array throw would lose the whole frame.
  const usable = pcm.byteLength - (pcm.byteLength % 2);
  const view = new Int16Array(pcm, 0, usable / 2);
  const out = new Float32Array(view.length);

  for (let i = 0; i < view.length; i++) out[i] = view[i] / 0x8000;

  return out;
}

/** Loudness of a block, for a level meter. */
export function rms(samples: Float32Array): number {
  if (samples.length === 0) return 0;

  let total = 0;

  for (let i = 0; i < samples.length; i++) total += samples[i] * samples[i];

  return Math.sqrt(total / samples.length);
}
