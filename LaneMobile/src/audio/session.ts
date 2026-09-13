import {
  AudioBufferQueueSourceNode,
  AudioContext,
  AudioManager,
  AudioRecorder,
} from 'react-native-audio-api';
import { LaneCredentials } from '../api/client';
import { VoiceFormat, VoiceSocket } from '../api/voice';
import { floatToPcm16, pcm16ToFloat, rms } from './pcm';

export type VoiceState = 'idle' | 'starting' | 'listening' | 'speaking' | 'stopping';

export interface VoiceSessionHandlers {
  onState: (state: VoiceState) => void;
  onTranscript: (text: string) => void;
  onLevel: (level: number) => void;
  onError: (message: string) => void;
  onStats: (stats: VoiceStats) => void;
}

/** Enough to tell a dead socket from a dead speaker without a debugger attached. */
export interface VoiceStats {
  socket: string;
  sent: number;
  textFrames: number;
  audioFrames: number;
  otherFrames: number;
  receivedBytes: number;
  enqueued: number;
  inputRate: number;
  outputRate: number;
  contextState: string;
  ready: boolean;
  lastText: string;
  lastError: string;
}

/** 64 ms at 16 kHz. Short enough that barge-in is not waiting on the buffer. */
const BUFFER_LENGTH = 1024;

const PREFERRED_RATE = 16000;

/** What the server sends unless asked otherwise; confirmed by the ready frame. */
const DEFAULT_OUTPUT_RATE = 24000;

const REPORT_INTERVAL_MS = 500;

/**
 * A continuous conversation: the microphone open, Lane's voice coming back, until it is
 * stopped.
 *
 * The sample rate is learned rather than assumed. `onAudioReady` is documented to deliver
 * whatever the hardware could actually provide, which may not be what was asked for, and the
 * server accepts only 16 kHz mono or 48 kHz — so the socket is opened after the first buffer
 * arrives, using the rate that buffer actually has.
 */
export class VoiceSession {
  private recorder: AudioRecorder | null = null;
  private socket: VoiceSocket | null = null;
  private context: AudioContext | null = null;
  private queue: AudioBufferQueueSourceNode | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;
  private outputRate = DEFAULT_OUTPUT_RATE;
  private inputRate = 0;
  private sent = 0;
  private enqueued = 0;
  private receivedBytes = 0;
  private ready = false;
  private lastError = '';
  private state: VoiceState = 'idle';
  private stopped = false;

  constructor(
    private readonly credentials: LaneCredentials,
    private readonly diarize: boolean,
    private readonly handlers: VoiceSessionHandlers,
  ) {}

  async start(): Promise<void> {
    this.stopped = false;
    this.move('starting');

    this.timer = setInterval(() => this.report(), REPORT_INTERVAL_MS);

    const permission = await AudioManager.requestRecordingPermissions();

    if (permission !== 'Granted') {
      this.fail('Lane needs the microphone. Grant it in Settings and try again.');
      return;
    }

    // voiceChat is what turns on the platform's echo cancellation. Without it the microphone
    // hears Lane through the speaker and she interrupts herself.
    AudioManager.setAudioSessionOptions({
      iosCategory: 'playAndRecord',
      iosMode: 'voiceChat',
      iosOptions: ['defaultToSpeaker', 'allowBluetoothHFP'],
    });

    await AudioManager.setAudioSessionActivity(true);

    // The speaker is opened before the microphone, not from inside a socket callback. Built
    // there, a failure to construct it vanished into the WebSocket listener and left playback
    // silently absent; here it fails up front where it can be seen.
    await this.startPlayback(DEFAULT_OUTPUT_RATE);

    const recorder = new AudioRecorder();

    recorder.onError(error => this.fail(`The microphone stopped: ${error.message ?? 'unknown error'}`));

    recorder.onAudioReady({ sampleRate: PREFERRED_RATE, bufferLength: BUFFER_LENGTH, channelCount: 1 }, event => {
      const samples = event.buffer.getChannelData(0);

      if (!this.socket) this.openSocket(event.buffer.sampleRate, event.buffer.numberOfChannels);

      this.handlers.onLevel(rms(samples));

      if (this.socket?.isOpen) {
        this.socket.send(floatToPcm16(samples));
        this.sent += 1;
      }
    });

    this.recorder = recorder;

    await recorder.start();
  }

  /** Stop talking, without ending the session — the same barge-in a spoken interruption causes. */
  interrupt(): void {
    this.socket?.interrupt();
  }

  async stop(): Promise<void> {
    if (this.stopped) return;

    this.stopped = true;
    this.move('stopping');

    this.socket?.close();

    try {
      this.recorder?.clearOnAudioReady();
      this.recorder?.clearOnError();
      await this.recorder?.stop();
    } catch {
      // Already stopped, or never started.
    }

    this.recorder = null;

    this.teardownPlayback();

    try {
      await AudioManager.setAudioSessionActivity(false);
    } catch {
      // Deactivating a session nothing else wants is not worth reporting.
    }

    this.report();

    if (this.timer) clearInterval(this.timer);
    this.timer = null;
    this.socket = null;

    this.move('idle');
  }

  private openSocket(rate: number, channels: number): void {
    if (!isSupported(rate, channels)) {
      this.fail(
        `This device records at ${rate} Hz on ${channels} channel(s). Lane accepts 16000 Hz mono, ` +
          'or 48000 Hz mono or stereo.',
      );

      void this.stop();
      return;
    }

    this.inputRate = rate;

    this.socket = new VoiceSocket(
      { credentials: this.credentials, inputRate: rate, inputChannels: channels, diarize: this.diarize },
      {
        onReady: output => {
          this.ready = true;

          if (output.rate !== this.outputRate) void this.startPlayback(output.rate);

          this.move('listening');
        },
        onSpeaking: state => this.move(state === 'start' ? 'speaking' : 'listening'),
        onTranscript: this.handlers.onTranscript,
        onAudio: pcm => {
          this.receivedBytes += pcm.byteLength;
          this.play(pcm);
        },
        onError: error => this.fail(error.message),
        onClose: () => {
          if (!this.stopped) {
            this.fail('The voice connection closed.');
            void this.stop();
          }
        },
      },
    );

    this.socket.open();
  }

  private async startPlayback(rate: number): Promise<void> {
    this.teardownPlayback();

    try {
      this.outputRate = rate;

      const context = new AudioContext({ sampleRate: rate });
      const queue = context.createBufferQueueSource();

      queue.connect(context.destination);

      // Both arguments are explicit because start() cannot be called bare in 0.13.3: offset
      // defaults to -1, which the same method's own `offset && offset < 0` guard rejects.
      queue.start(0, 0);

      this.context = context;
      this.queue = queue;

      // Nothing opens the output device on its own: the driver is started by tryStartDriver,
      // which is only reachable through resume(). A source node starting does not do it.
      await context.resume();
    } catch (error) {
      this.fail(`Could not open the speaker: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

  /** One frame of her voice, appended to whatever is already scheduled. */
  private play(pcm: ArrayBuffer): void {
    if (!this.context || !this.queue) {
      this.lastError = 'audio arrived with no speaker open';
      return;
    }

    const samples = pcm16ToFloat(pcm);

    if (samples.length === 0) return;

    try {
      const buffer = this.context.createBuffer(1, samples.length, this.outputRate);

      buffer.copyToChannel(samples, 0);

      this.queue.enqueueBuffer(buffer);
      this.enqueued += 1;
    } catch (error) {
      this.lastError = `enqueue failed: ${error instanceof Error ? error.message : String(error)}`;
    }
  }

  private teardownPlayback(): void {
    try {
      this.queue?.clearBuffers();
      this.queue?.stop();
      void this.context?.close();
    } catch {
      // Closing a context that never opened is not an error worth surfacing.
    }

    this.queue = null;
    this.context = null;
  }

  private report(): void {
    const wire = this.socket?.wire;

    this.handlers.onStats({
      socket: this.socket?.readyState ?? 'none',
      sent: this.sent,
      textFrames: wire?.text ?? 0,
      audioFrames: wire?.binary ?? 0,
      otherFrames: wire?.other ?? 0,
      receivedBytes: this.receivedBytes,
      enqueued: this.enqueued,
      inputRate: this.inputRate,
      outputRate: this.outputRate,
      contextState: this.context?.state ?? 'none',
      ready: this.ready,
      lastText: wire?.lastText ?? '',
      lastError: this.lastError || wire?.lastError || '',
    });
  }

  private move(state: VoiceState): void {
    if (this.state === state) return;

    this.state = state;
    this.handlers.onState(state);
  }

  private fail(message: string): void {
    this.lastError = message;
    this.handlers.onError(message);
  }
}

/** Exactly what ApiAudioSource.IsSupported accepts. */
function isSupported(rate: number, channels: number): boolean {
  return (rate === 16000 && channels === 1) || (rate === 48000 && (channels === 1 || channels === 2));
}
