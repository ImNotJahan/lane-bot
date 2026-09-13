/**
 * Server-sent events over XMLHttpRequest.
 *
 * React Native's `fetch` cannot stream a response body, so incremental delivery has to come
 * from XHR's growing `responseText`. The endpoint this reads is a POST with a JSON body,
 * which rules out `EventSource` as well.
 */

export interface SseFrame {
  event: string;
  data: string;
}

export interface SseOptions {
  url: string;
  headers: Record<string, string>;
  body?: string;
  method?: string;
  onFrame: (frame: SseFrame) => void;
  onError: (error: Error) => void;
  onClose: () => void;
}

export interface SseHandle {
  abort: () => void;
}

const FRAME_SEPARATOR = /\r?\n\r?\n/;

/**
 * Turns arbitrarily-chopped stream text into whole frames.
 *
 * Separate from the transport because a frame can be split across any number of reads, and
 * because this is the part worth testing without a socket.
 */
export class SseDecoder {
  private pending = '';

  push(chunk: string): SseFrame[] {
    this.pending += chunk;

    const parts = this.pending.split(FRAME_SEPARATOR);

    // The last part is whatever arrived mid-frame; it stays buffered until its blank line.
    this.pending = parts.pop() ?? '';

    return parts.map(parseFrame).filter((frame): frame is SseFrame => frame !== null);
  }
}

export function openSse(options: SseOptions): SseHandle {
  const xhr = new XMLHttpRequest();

  const decoder = new SseDecoder();

  let consumed = 0;
  let aborted = false;
  let finished = false;

  /** Fires onClose exactly once, whichever of error, abort or load gets there first. */
  const finish = () => {
    if (finished) return;
    finished = true;
    options.onClose();
  };

  const drain = (text: string) => {
    const chunk = text.slice(consumed);
    consumed = text.length;

    for (const frame of decoder.push(chunk)) options.onFrame(frame);
  };

  xhr.open(options.method ?? 'POST', options.url, true);

  for (const [name, value] of Object.entries(options.headers)) {
    xhr.setRequestHeader(name, value);
  }

  xhr.onreadystatechange = () => {
    if (aborted) return;

    if (xhr.readyState === 3 || xhr.readyState === 4) {
      if (xhr.status >= 200 && xhr.status < 300) drain(xhr.responseText ?? '');
    }

    if (xhr.readyState === 4) {
      if (xhr.status < 200 || xhr.status >= 300) {
        options.onError(new Error(describeFailure(xhr)));
      }
      finish();
    }
  };

  xhr.onerror = () => {
    if (aborted) return;
    options.onError(new Error('Could not reach Lane. Check the address and that she is running.'));
    finish();
  };

  xhr.ontimeout = () => {
    if (aborted) return;
    options.onError(new Error('The connection timed out.'));
    finish();
  };

  xhr.send(options.body);

  return {
    abort: () => {
      if (finished) return;
      aborted = true;
      try {
        xhr.abort();
      } catch {
        // Already gone.
      }
      finish();
    },
  };
}

/**
 * One `event:`/`data:` block. Data lines are joined with newlines rather than concatenated:
 * SseWriter splits any JSON containing a newline across several `data:` lines, so decoding
 * them individually would fail on every multi-line reply.
 */
function parseFrame(raw: string): SseFrame | null {
  let event = 'message';
  const data: string[] = [];

  for (const line of raw.split(/\r?\n/)) {
    if (line.length === 0 || line.startsWith(':')) continue;

    if (line.startsWith('event:')) {
      event = line.slice(6).trim();
    } else if (line.startsWith('data:')) {
      const value = line.slice(5);
      data.push(value.startsWith(' ') ? value.slice(1) : value);
    }
  }

  return data.length > 0 ? { event, data: data.join('\n') } : null;
}

function describeFailure(xhr: XMLHttpRequest): string {
  if (xhr.status === 401) return 'That client key was rejected.';
  if (xhr.status === 403) return 'This client is not permitted to do that.';

  try {
    const parsed = JSON.parse(xhr.responseText) as { error?: string; detail?: string };
    if (parsed.detail) return parsed.detail;
    if (parsed.error) return parsed.error;
  } catch {
    // Not JSON; fall through to the status code.
  }

  return `Lane answered ${xhr.status}.`;
}
