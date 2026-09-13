/**
 * Feeds SseDecoder the exact byte shape SseWriter.SendAsync produces, chopped every way a
 * socket might chop it. Run with `npm run check:sse`.
 */
import assert from 'node:assert';
import { SseDecoder } from '../sse.ts';

/** Byte-for-byte what Lane.Surfaces.Api/Streaming/SseWriter.cs writes. */
function encode(name: string, payload: unknown): string {
  const json = JSON.stringify(payload);

  let frame = `event: ${name}\n`;

  for (const line of json.split('\n')) frame += `data: ${line}\n`;

  return frame + '\n';
}

const KEEP_ALIVE = ': keep-alive\n\n';

function decodeAll(stream: string, chunkSize: number) {
  const decoder = new SseDecoder();
  const frames = [];

  for (let i = 0; i < stream.length; i += chunkSize) {
    frames.push(...decoder.push(stream.slice(i, i + chunkSize)));
  }

  return frames;
}

let passed = 0;

function check(name: string, run: () => void) {
  run();
  passed += 1;
  console.log(`  ok  ${name}`);
}

// A JSON payload with a literal newline in the text, which SseWriter splits across several
// `data:` lines. Decoding those lines individually is the failure this exists to catch.
const multiline = { text: 'Tide pools.\n\nWorth a look, if you like that sort of thing.' };

const stream =
  encode('accepted', { sessionId: 'api/ios/main', messageId: 'abc' }) +
  encode('delta', { text: 'Tide ' }) +
  KEEP_ALIVE +
  encode('delta', { text: 'pools.' }) +
  encode('tool', { name: 'web_search', phase: 'start' }) +
  encode('tool', { name: 'web_search', phase: 'end', isError: false }) +
  encode('message', { sessionId: 'api/ios/main', ...multiline }) +
  encode('done', { silent: false });

check('decodes every frame in one chunk', () => {
  const frames = decodeAll(stream, stream.length);

  assert.deepEqual(
    frames.map(f => f.event),
    ['accepted', 'delta', 'delta', 'tool', 'tool', 'message', 'done'],
  );
});

check('survives being chopped one character at a time', () => {
  const frames = decodeAll(stream, 1);

  assert.equal(frames.length, 7);
  assert.equal(frames[0].event, 'accepted');
  assert.equal(frames[6].event, 'done');
});

check('reassembles multi-line data into valid JSON', () => {
  for (const size of [1, 3, 7, 64, 4096]) {
    const message = decodeAll(stream, size).find(f => f.event === 'message');

    assert.ok(message, `no message frame at chunk size ${size}`);
    assert.deepEqual(JSON.parse(message.data).text, multiline.text);
  }
});

check('ignores keep-alive comments', () => {
  assert.equal(decodeAll(KEEP_ALIVE + KEEP_ALIVE, 1).length, 0);
});

check('holds back a frame that has not terminated', () => {
  const decoder = new SseDecoder();

  assert.equal(decoder.push('event: delta\ndata: {"text":"half"}\n').length, 0);
  assert.equal(decoder.push('\n').length, 1);
});

check('decodes silent done', () => {
  const frames = decodeAll(encode('done', { silent: true, reason: 'not addressed' }), 5);

  assert.equal(JSON.parse(frames[0].data).silent, true);
});

check('tolerates CRLF line endings', () => {
  const crlf = 'event: delta\r\ndata: {"text":"x"}\r\n\r\n';
  const frames = decodeAll(crlf, 2);

  assert.equal(frames.length, 1);
  assert.equal(JSON.parse(frames[0].data).text, 'x');
});

console.log(`\n${passed} checks passed.`);
