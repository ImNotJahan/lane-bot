# How It Works

This page describes Lane's core systems and how they work conceptually.

## Message Lifecycle

When Lane receives a text message, the following happens in order:

1. **Image extraction** — any image attachments are stored in memory as image messages
2. **Routing** — an LLM call determines whether and how enthusiastically to respond
3. **Response** — if routing passes, a second LLM call generates a reply
4. **Memory** — the incoming message and response are stored in all configured memory handlers
5. **Persistence** — memory state is serialized to disk

---

## Routing

Before generating a response, Lane runs a routing classifier using `Routing.md`. The LLM returns a single float between `0.0` and `1.0` representing how enthusiastically Lane should engage with the message.

| Score | Meaning |
|---|---|
| `0.0` | Do not respond |
| `0.1–0.4` | Respond briefly and neutrally |
| `0.5–0.7` | Respond normally |
| `0.8–1.0` | Respond with high engagement |

If the score is `≤ 0.2`, Lane stays silent — the message is still stored in memory, but no response is generated. This is what produces Lane's selective response behavior: she ignores messages that aren't directed at her or don't interest her.

The enthusiasm score is also passed into `Respond_Dynamic.md` as `low`, `neutral`, or `high`, so the same score that decides *whether* to respond also shapes *how* the response is written.

---

## Monologue

Lane runs a continuous background thought loop, independent of incoming messages.

Each iteration:
1. Memory context is assembled (same as for responses)
2. An LLM call is made using `Monologue.md`, producing a JSON object:

```json
{
  "thought": "...",
  "speak": false,
  "message": "",
  "next_thought_in_seconds": 300
}
```

3. The `thought` is stored in memory as a thought-type message (visible to future LLM calls but never sent to users)
4. If `speak` is `true`, the `message` is sent — to the voice channel if the last interaction was in VC, otherwise to the text channel
5. The loop waits `next_thought_in_seconds` before the next iteration

**Timer reset**: whenever Lane receives a message, the countdown resets to the `RespondToThought` value from config. This ensures she thinks again shortly after a conversation, rather than waiting out a long idle timer.

The monologue starts automatically on Discord body startup. It does not run in the Terminal body.

---

## Memory Assembly

Context passed to the LLM is assembled from all configured memory handlers. Handlers are split into two categories:

- **Recent** (`IsRecent() = true`): handlers that track the current/ongoing conversation, e.g. `SlidingWindow`. These are used as the primary conversation context.
- **Non-recent** (`IsRecent() = false`): handlers that retrieve older, long-term context, e.g. `RAG` and `Summary`. These are injected separately via the `*_Memory.md` template files.

This split allows the LLM to receive both an immediate conversation window and relevant long-term memories without conflating the two.

Thoughts (from the monologue) are stored in memory and appear in context prefixed with `"Lane thinks:"`, distinguishing them from regular messages (`"Lane says:"`).

**Persistence**: after every message and thought, all memory handler state is serialized to a `data.json` file next to the binary. On startup, this file is loaded back in, so Lane's memory survives restarts.

---

## Voice (Discord)

### Joining

When the Discord body starts up and receives the `GuildCreate` event, it automatically joins the **first voice channel** found in the guild. No configuration is needed beyond having `Speech` and/or `Hearing` set in `appsettings.json`.

If neither `Speech` nor `Hearing` is configured, the bot still joins the voice channel but will not speak or listen.

### Listening (STT)

Incoming audio from each speaker is processed per-user:

1. Opus packets are decoded to PCM at 48 kHz
2. Audio is downsampled to 16 kHz mono PCM (the format Azure STT expects)
3. A `UserListenLoop` runs for each active speaker, feeding audio into an `IEar` instance
4. When the STT provider returns a transcription, it is treated as a regular text message prefixed with `[Voice chat]` and routed through the normal message pipeline

A user's listener is cleaned up when they leave the voice channel.

### Speaking (TTS)

When Lane generates a response (or a monologue thought with `"speak": true`):

- If the **last interaction was in VC**: the response is spoken via the configured `IMouth`
- Otherwise: the response is sent as a text message to the most recently active text channel (or `DefaultDiscordChannel` if none)

TTS output (PCM audio) is Opus-encoded and streamed to the Discord voice connection.

### Image Support

In the Discord body, image attachments on messages are extracted as URLs and stored in memory as image-type messages. The LLM receives these as image content blocks and can reason about them. Image support is not available in the Terminal body.

### Message Formatting

Before a Discord message reaches Lane, mentions (`<@userid>`) are resolved to readable usernames (`@username`), and reply context is appended (e.g., `"in response to Username: original message"`).

---

## Terminal Body

The Terminal body is a simple read-eval loop. It:
- Reads a line of input from stdin
- Passes it to `Bot.OnMessageCreated` under the author name `"User"`
- Prints the response, or nothing if Lane chose not to respond

The monologue currently does **not** run in Terminal mode. There is no voice, no image support, and no persistence between sessions unless memory was previously written by a Discord session.
