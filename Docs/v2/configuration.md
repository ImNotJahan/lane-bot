# Configuration

1. [.env](#env)
2. [appsettings.json](#appsettingsjson)
    - [`"MemoryHandlers"`](#memoryhandlers)
    - [`"DefaultDiscordChannel"`](#defaultdiscordchannel)
    - [`"ExclusiveToChannel"`](#exclusivetochannel)
    - [`"TimezoneSettings"`](#timezonesettings)
    - [`"Logging"`](#logging)
    - [`"RespondToThought"`](#respondtothought)
    - [`"LLM"`](#llm)
    - [`"Body"`](#body)
    - [`"Speech"`](#speech)
    - [`"Hearing"`](#hearing)
    - [Example](#example)

## .env
API keys and endpoints need to be specified in a .env file. The possible fields
are as follows:
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

- `DISCORD_API_KEY` is needed iff using the Discord body
- `ANTHROPIC_API_KEY` is needed iff using Anthropic Claude as LLM
- `DEEPSEEK_API_KEY` is needed iff using DeepSeek as LLM
- `QDRANT_API_KEY`, `QDRANT_ENDPOINT`, `OPENAI_EMBEDDING_ENDPOINT`, and `OPENAI_EMBEDDING_API_KEY` are needed iff using RAG memory
- `AZURE_KEY` and `AZURE_REGION` are needed iff using Azure for TTS or STT
- `ELEVENLABS_KEY` is needed iff using ElevenLabs for TTS

## appsettings.json
This is where configuration is done. Everything in here should lie within a `"Settings"` block. Within this block,
there are a number of fields for various settings:

### `"MemoryHandlers"`
This is where the `MemoryHandler`s which you'd like the bot to use are specified. It should be an array of entries giving information
about some `MemoryHandler`. Each entry should look like so:
```json
{
    "Handler": "RAG",
    "ID":      "RAG",
    "Args": {
        "SelectLimit":    10,
        "WriteInterval": 10
    }
}
```
Where there is a `"Handler"` field giving the name of the handler, an `"ID"` field giving a unique identifier for this handler instance,
and an `"Args"` block denoting all of its needed arguments. The `"ID"` field allows multiple instances of the same handler type to be used simultaneously.

There are currently three `MemoryHandler`s which can be used, listed below:
- `SlidingWindow`
    - Keeps track of the last few messages sent to the bot
    - Args:
        - `"MaxMessages"`: integer — how many messages to keep in the window
        - `"ForThoughts"`: integer (0 or 1) — if 1, this window tracks internal thoughts rather than regular messages
- `Summary`
    - Summarizes the conversation every few messages
    - Args:
        - `"UpdateInterval"`: integer — how many messages between summary updates
- `RAG`
    - Stores all messages in a vector database and retrieves similar messages to the one received
    - Args:
        - `"SelectLimit"`: integer — how many similar messages to retrieve
        - `"WriteInterval"`: integer — how often to push new messages to the database

### `"DefaultDiscordChannel"`
If using the Discord body, this is the channel ID which the bot will voice its thoughts in if it
had not yet talked to anyone in Discord since it was started. Also, if `ExclusiveToChannel` is true, it
is the only channel the bot will respond to messages in.

### `"ExclusiveToChannel"`
Determines if the bot should only talk in `"DefaultDiscordChannel"` when using the Discord body. If false,
will talk wherever it receives a message.

### `"TimezoneSettings"`
This block has two fields:
1. `"HourShift"`: integer denoting hour shift from UTC the bot should use
2. `"MinuteShift"`: integer denoting minute shift from UTC the bot should use

### `"Logging"`
This is a block specifying how logging should be handled. It has three fields:
1. `"ConsoleLevel"`: the minimum level a logged message should have to be outputted to the console.
2. `"FileLevel"`: the minimum level a logged message should have to be outputted to the log file.
3. `"FileLogPath"`: the path to the file to log to. The token `<date>` in the path is replaced with the current date at startup (e.g. `"lane-<date>.log"` → `"lane-2026-04-09.log"`).

The possible values for a logging level are as follows:
- `"Trace"`
- `"Debug"`
- `"Information"`
- `"Warning"`
- `"Error"`
- `"Critical"`
- `"None"`

### `"RespondToThought"`
The interval, in seconds, between when the bot receives a message and when it should next think.
Should be an integer.

### `"LLM"`
The language model backend to use. Possible values:
- `"Claude"`: Anthropic Claude Haiku 4.5 (default if omitted)
- `"DeepSeek"`: DeepSeek `deepseek-chat`

### `"Body"`
The interface the bot should run with. Possible values:
- `"Discord"`: runs as a Discord bot; a dashboard TUI launches in the terminal
- `"Terminal"`: runs in the terminal (default if omitted)

This can also be overridden at runtime via the `discord` or `terminal` command-line arguments.

### `"Speech"`
Configuration for text-to-speech (TTS). Only needed if the bot will be speaking in voice channels.

```json
"Speech": {
    "Mouth":      "ElevenLabs",
    "Voice":      "<voice-id>",
    "Stability":  0.84,
    "Similarity": 0.74,
    "Tempo":      25,
    "Pitch":      -2.5,
    "Rate":       0,
    "Tune":       true
}
```

- `"Mouth"`: TTS provider to use. Possible values: `"ElevenLabs"`, `"Azure"`
- `"Voice"`: Either ElevenLabs voice ID or Azure speech synthesis voice name
- `"Stability"`: ElevenLabs voice stability (0.0–1.0). Only used when `"Mouth"` is `"ElevenLabs"`
- `"Similarity"`: ElevenLabs similarity boost (0.0–1.0). Only used when `"Mouth"` is `"ElevenLabs"`
- `"Tempo"`: Tempo adjustment
- `"Pitch"`: Pitch adjustment
- `"Rate"`: Speaking rate
- `"Tune"`: boolean — whether to apply pitch/tempo tuning

### `"Hearing"`
Configuration for speech-to-text (STT). Only needed if the bot will be listening in voice channels.

```json
"Hearing": {
    "Ear": "Azure"
}
```

- `"Ear"`: STT provider to use. Currently only `"Azure"` is supported.

### Example
Here's what an example `appsettings.json` could look like:
```json
{
    "Settings": {
        "MemoryHandlers": [
            {
                "Handler": "Summary",
                "ID":      "Summary",
                "Args": {
                    "UpdateInterval": 10
                }
            },
            {
                "Handler": "SlidingWindow",
                "ID":      "MessageWindow",
                "Args": {
                    "MaxMessages": 10,
                    "ForThoughts": 0
                }
            },
            {
                "Handler": "SlidingWindow",
                "ID":      "ThoughtWindow",
                "Args": {
                    "MaxMessages": 2,
                    "ForThoughts": 1
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
        "LLM": "Claude",
        "Body": "Discord",
        "Logging": {
            "ConsoleLevel": "Warning",
            "FileLevel":    "Debug",
            "FileLogPath":  "wizard.log"
        },
        "Speech": {
            "Mouth":      "ElevenLabs",
            "Voice":      "<voice-id>",
            "Stability":  0.84,
            "Similarity": 0.74,
            "Tempo":      25,
            "Pitch":      -2.5,
            "Rate":       0,
            "Tune":       true
        },
        "Hearing": {
            "Ear": "Azure"
        }
    }
}
```
