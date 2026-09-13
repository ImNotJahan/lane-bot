import { LaneCredentials, normaliseBaseUrl } from './client';

/** Frames the socket sends as text, envelope and all: `{"type":name,"data":{...}}`. */
interface VoiceEnvelope {
  type: string;
  data?: Record<string, unknown>;
}

export interface VoiceFormat {
  rate: number;
  channels: number;
  encoding: string;
}

/** What has actually come off the wire, by kind. */
export interface VoiceWireStats {
  text: number;
  binary: number;
  other: number;
  lastText: string;
  lastError: string;
}

export interface VoiceSocketHandlers {
  onReady: (output: VoiceFormat) => void;
  onSpeaking: (state: 'start' | 'end') => void;
  onTranscript: (text: string) => void;
  onAudio: (pcm: ArrayBuffer) => void;
  onError: (error: Error) => void;
  onClose: () => void;
}

/**
 * React Native's WebSocket takes a third argument the DOM one does not — `{headers}` on the
 * handshake. The runtime has always accepted it (Libraries/WebSocket/WebSocket.js); only the
 * ambient DOM type is narrower, so it is restated here rather than cast away at the call.
 */
type WebSocketWithHeaders = new (
  url: string,
  protocols: string | string[] | undefined,
  options: { headers?: Record<string, string> },
) => WebSocket;

const HeaderedWebSocket = WebSocket as unknown as WebSocketWithHeaders;

export interface VoiceSocketOptions {
  credentials: LaneCredentials;
  inputRate: number;
  inputChannels: number;
  diarize: boolean;
}

/**
 * The microphone and the speaker, over one socket.
 *
 * Binary frames are audio in both directions; text frames are control. Nothing here names a
 * speaker: left unset the server attributes the turn to the client's own participant, which
 * is the one carrying GlobalUserId. Passing `speaker` would mint a fresh participant instead
 * and quietly detach the conversation from everything Lane knows about you.
 */
export class VoiceSocket {
  private socket: WebSocket | null = null;
  private closed = false;

  readonly wire: VoiceWireStats = { text: 0, binary: 0, other: 0, lastText: '', lastError: '' };

  constructor(
    private readonly options: VoiceSocketOptions,
    private readonly handlers: VoiceSocketHandlers,
  ) {}

  get isOpen(): boolean {
    return this.socket?.readyState === WebSocket.OPEN;
  }

  get readyState(): string {
    return ['connecting', 'open', 'closing', 'closed'][this.socket?.readyState ?? 3] ?? 'unknown';
  }

  open(): void {
    const url = this.buildUrl();

    // React Native allows headers on the handshake, unlike a browser — so the client key
    // never has to travel as a query parameter that proxies and access logs would record.
    const socket = new HeaderedWebSocket(url, undefined, {
      headers: { Authorization: `Bearer ${this.options.credentials.clientKey}` },
    });

    socket.binaryType = 'arraybuffer';

    socket.onmessage = event => this.receive(event.data);

    socket.onerror = () => {
      if (!this.closed) this.handlers.onError(new Error('The voice connection failed.'));
    };

    socket.onclose = () => {
      this.socket = null;
      if (!this.closed) {
        this.closed = true;
        this.handlers.onClose();
      }
    };

    this.socket = socket;
  }

  send(pcm: ArrayBuffer): void {
    if (this.isOpen) this.socket!.send(pcm);
  }

  /** Barge-in for a client whose user pressed a button rather than spoke. */
  interrupt(): void {
    if (this.isOpen) this.socket!.send(JSON.stringify({ type: 'cancel' }));
  }

  close(): void {
    this.closed = true;

    try {
      this.socket?.close();
    } catch {
      // Already gone.
    }

    this.socket = null;
  }

  private buildUrl(): string {
    const base = normaliseBaseUrl(this.options.credentials.baseUrl).replace(/^http/i, 'ws');

    const query = new URLSearchParams({
      rate: String(this.options.inputRate),
      channels: String(this.options.inputChannels),
    });

    if (this.options.diarize) query.set('diarize', 'true');

    return `${base}/v1/sessions/${encodeURIComponent(this.options.credentials.sessionKey)}/voice?${query}`;
  }

  /** Every exit from here is counted: a frame that vanishes silently is what made this hard to debug. */
  private receive(data: unknown): void {
    try {
      this.dispatch(data);
    } catch (error) {
      this.wire.lastError = error instanceof Error ? error.message : String(error);
      this.handlers.onError(new Error(`Handling a frame failed: ${this.wire.lastError}`));
    }
  }

  private dispatch(data: unknown): void {
    if (data instanceof ArrayBuffer) {
      this.wire.binary += 1;
      this.handlers.onAudio(data);
      return;
    }

    if (typeof data !== 'string') {
      this.wire.other += 1;
      this.wire.lastError = `unexpected frame of type ${Object.prototype.toString.call(data)}`;
      return;
    }

    this.wire.text += 1;
    this.wire.lastText = data.slice(0, 120);

    const envelope = JSON.parse(data) as VoiceEnvelope;

    switch (envelope.type) {
      case 'ready':
        this.handlers.onReady((envelope.data?.output as VoiceFormat | undefined) ?? {
          rate: 24000,
          channels: 1,
          encoding: 'pcm_s16le',
        });
        break;

      case 'speaking':
        this.handlers.onSpeaking(envelope.data?.state === 'start' ? 'start' : 'end');
        break;

      case 'message':
        if (typeof envelope.data?.text === 'string') this.handlers.onTranscript(envelope.data.text);
        break;
    }
  }
}
