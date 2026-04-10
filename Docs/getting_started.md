# Getting Started

1. [Setup](#setup)
2. [Configuration](#configuration)
3. [Running](#running)
4. [Next steps](#next-steps)

## Setup

To get Lane running you'll need at minimum an Anthropic API key. Everything else depends on which features you want enabled.

| Feature | Required keys/services |
|---|---|
| Core — Claude LLM | `ANTHROPIC_API_KEY` |
| Core — DeepSeek LLM | `DEEPSEEK_API_KEY` |
| Discord | `DISCORD_API_KEY` (bot application) |
| RAG memory | `QDRANT_API_KEY`, `QDRANT_ENDPOINT`, `OPENAI_EMBEDDING_API_KEY`, `OPENAI_EMBEDDING_ENDPOINT` |
| Voice — TTS via ElevenLabs | `ELEVENLABS_KEY` |
| Voice — TTS via Azure | `AZURE_KEY`, `AZURE_REGION` |
| Voice — STT | `AZURE_KEY`, `AZURE_REGION` |

Once you have your keys, create a `.env` file in the root of the `Wizard` folder:

```
DISCORD_API_KEY=
ANTHROPIC_API_KEY=
DEEPSEEK_API_KEY=
QDRANT_API_KEY=
QDRANT_ENDPOINT=
OPENAI_EMBEDDING_ENDPOINT=
OPENAI_EMBEDDING_API_KEY=
AZURE_KEY=
AZURE_REGION=
ELEVENLABS_KEY=
```

Only populate the fields relevant to your setup — unused keys can be left blank or omitted. For full details see the [.env documentation](configuration.md#env).

## Configuration

Lane is configured via `appsettings.json` in the `Wizard` folder. The most important things to set for a first-time setup:

- **`"LLM"`** — set to `"Claude"` (default) or `"DeepSeek"`.
- **`"Body"`** — set to `"Discord"` or `"Terminal"`. If omitted, defaults to `"Terminal"`.
- **`"MemoryHandlers"`** — if you don't have Qdrant set up, don't include a `RAG` handler. A `SlidingWindow` is sufficient to get started.
- **`"DefaultDiscordChannel"`** — required for Discord; the channel ID Lane uses for unprompted messages.

A minimal working config for Discord looks like:

```json
{
    "Settings": {
        "MemoryHandlers": [
            {
                "Handler": "SlidingWindow",
                "ID":      "MessageWindow",
                "Args": {
                    "MaxMessages": 10,
                    "ForThoughts": 0
                }
            }
        ],
        "DefaultDiscordChannel": 0,
        "ExclusiveToChannel": false,
        "TimezoneSettings": {
            "HourShift":   0,
            "MinuteShift": 0
        },
        "RespondToThought": 5,
        "Body": "Discord",
        "Logging": {
            "ConsoleLevel": "Information",
            "FileLevel":    "Debug",
            "FileLogPath":  "lane.log"
        }
    }
}
```

For all configuration options including the `Speech` and `Hearing` blocks for voice, see [configuration.md](configuration.md).

## Running

Run the binary to start Lane. The body is determined by the `"Body"` field in `appsettings.json`, but can be overridden with a command-line argument:

```
./Wizard discord
./Wizard terminal
```

**First run**: Lane starts with no memory and builds it up as conversations happen. Memory is persisted to a `data.json` file next to the binary after every message, so it survives restarts.

**Discord**: Lane automatically joins the first voice channel in your server on startup if `Speech` or `Hearing` is configured. The monologue loop starts on connection, so Lane may send unprompted messages to `DefaultDiscordChannel` on her own. A dashboard TUI launches in the terminal showing live logs, token usage, next-thought countdown, and the active configuration.

**Terminal**: a simple read-eval loop. Type a message and press enter. The monologue does not run in Terminal mode.

## Next Steps

- [Customization](customization.md) — how to edit Lane's personality, tone, and prompt files
- [How it works](how_it_works.md) — the routing, monologue, and memory systems explained
- [Configuration reference](configuration.md) — all `appsettings.json` fields documented
