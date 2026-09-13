import { openSse, SseHandle } from './sse';
import {
  AcceptedResponse,
  DeltaFrame,
  DoneFrame,
  ErrorFrame,
  ErrorResponse,
  HistoryResponse,
  MessageFrame,
  MessageResponse,
  SessionResponse,
  ToolFrame,
  TurnEvent,
} from './types';

export interface LaneCredentials {
  baseUrl: string;
  clientKey: string;
  sessionKey: string;
}

export class LaneError extends Error {
  constructor(message: string, readonly status?: number, readonly code?: string) {
    super(message);
    this.name = 'LaneError';
  }
}

/** Trailing slashes would produce `//v1/...`, which ASP.NET routing does not match. */
export function normaliseBaseUrl(raw: string): string {
  const trimmed = raw.trim().replace(/\/+$/, '');

  return /^https?:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`;
}

export class LaneClient {
  constructor(private readonly credentials: LaneCredentials) {}

  private get base(): string {
    return normaliseBaseUrl(this.credentials.baseUrl);
  }

  private get authHeaders(): Record<string, string> {
    return { Authorization: `Bearer ${this.credentials.clientKey}` };
  }

  private sessionPath(suffix = ''): string {
    return `${this.base}/v1/sessions/${encodeURIComponent(this.credentials.sessionKey)}${suffix}`;
  }

  /**
   * Reachability without authentication. Health deliberately bypasses the auth middleware,
   * so a failure here means the address is wrong and a failure elsewhere means the key is.
   */
  async health(signal?: AbortSignal): Promise<void> {
    const response = await fetch(`${this.base}/v1/health`, { signal });

    if (!response.ok) throw new LaneError(`Lane answered ${response.status} at that address.`, response.status);
  }

  async createSession(displayName?: string): Promise<void> {
    await this.request('POST', `${this.base}/v1/sessions`, {
      key: this.credentials.sessionKey,
      displayName,
    });
  }

  async listSessions(all = false): Promise<SessionResponse[]> {
    return this.request<SessionResponse[]>('GET', `${this.base}/v1/sessions${all ? '?all=true' : ''}`);
  }

  /** Oldest-first, by memory group — so a voice session and a text one read as one conversation. */
  async history(limit = 50, after?: number): Promise<MessageResponse[]> {
    const query = new URLSearchParams({ limit: String(limit) });

    if (after !== undefined) query.set('after', String(after));

    const body = await this.request<HistoryResponse>('GET', this.sessionPath(`/messages?${query}`));

    return body.messages;
  }

  async cancel(): Promise<void> {
    await this.request('POST', this.sessionPath('/cancel'));
  }

  /**
   * Sends a message and streams the reply.
   *
   * Returns immediately with a handle; frames arrive through `onEvent` until `onClose`.
   * Aborting stops the client listening, but the turn itself carries on server-side and is
   * still remembered — use {@link cancel} to actually stop her talking.
   */
  send(
    text: string,
    onEvent: (event: TurnEvent) => void,
    onError: (error: Error) => void,
    onClose: () => void,
  ): SseHandle {
    return openSse({
      url: this.sessionPath('/messages?stream=true'),
      headers: {
        ...this.authHeaders,
        'Content-Type': 'application/json',
        Accept: 'text/event-stream',
      },
      body: JSON.stringify({ text }),
      onFrame: frame => {
        const event = decodeFrame(frame.event, frame.data);
        if (event) onEvent(event);
      },
      onError,
      onClose,
    });
  }

  private async request<T>(method: string, url: string, body?: unknown): Promise<T> {
    const response = await fetch(url, {
      method,
      headers: {
        ...this.authHeaders,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });

    if (!response.ok) throw await describe(response);

    if (response.status === 204) return undefined as T;

    const text = await response.text();

    return (text.length > 0 ? JSON.parse(text) : undefined) as T;
  }
}

function decodeFrame(event: string, data: string): TurnEvent | null {
  let payload: unknown;

  try {
    payload = JSON.parse(data);
  } catch {
    return null;
  }

  switch (event) {
    case 'accepted':
      return { type: 'accepted', data: payload as AcceptedResponse };
    case 'delta':
      return { type: 'delta', data: payload as DeltaFrame };
    case 'tool':
      return { type: 'tool', data: payload as ToolFrame };
    case 'message':
      return { type: 'message', data: payload as MessageFrame };
    case 'done':
      return { type: 'done', data: payload as DoneFrame };
    case 'error':
      return { type: 'error', data: payload as ErrorFrame };
    default:
      return null;
  }
}

async function describe(response: Response): Promise<LaneError> {
  if (response.status === 401) return new LaneError('That client key was rejected.', 401, 'unauthorized');

  try {
    const body = (await response.json()) as ErrorResponse;

    return new LaneError(body.detail ?? body.error, response.status, body.error);
  } catch {
    return new LaneError(`Lane answered ${response.status}.`, response.status);
  }
}
