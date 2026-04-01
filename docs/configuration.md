# Configuration

1. [.env](#env)
2. [appsettings.json](#appsettingsjson)
    - [`"MemoryHandlers"`](#memoryhandlers)
    - [`"DefaultDiscordChannel"`](#defaultdiscordchannel)
    - [`"ExclusiveToChannel"`](#exclusivetochannel)
    - [`"TimezoneSettings"`](#timezonesettings)
    - [`"Logging"`](#logging)
    - [`"RespondToThought"`](#respondtothought)
    - [Example](#example)

## .env
API keys and endpoints need to be specified in a .env file. The possible fields
are as follows:
```
DISCORD_API_KEY=
ANTHROPIC_API_KEY=
QDRANT_API_KEY=
QDRANT_ENDPOINT=
OPENAI_EMBEDDING_ENDPOINT=
OPENAI_EMBEDDING_API_KEY=
```

- `DISCORD_API_KEY` is needed iff using the Discord body
- `ANTHROPIC_API_KEY` is needed iff using anthropic as LLM
- `QDRANT_API_KEY`, `QDRANT_ENDPOINT`, `OPENAI_EMBEDDING_ENDPOINT`, and `OPENAI_EMBEDDING_API_KEY` are needed iff using RAG memory

## appsettings.json
This is where configuration is done. Everything in here should lie within a `"Settings"` block. Within this block,
there are a number of fields for various settings:

### `"MemoryHandlers"`
This is where the `MemoryHandler`s which you'd like the bot to use are specified. It should be an array of entries giving information
about some `MemoryHandler`. Each entry should look like so:
```json
{
    "Handler": "RAG",
    "Args": {
        "SelectLimit":    10,
        "WriteInterval": 10
    }
}
```
Where there is a `"Handler"` field giving the name of the handler, and an `"Args"` block denoting all of its needed arguments.

There are currently three `MemoryHandler`s which can be used, listed below:
- `SlidingWindow`
    - Keeps track of last few messages sent to bot
    - Args should consist of a `"MaxMessages"` field, whose value is an integer denoting how many messages it keeps track of
- `Summary`
    - Summarizes the conversation every few messages
    - Args should consist of an `"UpdateInterval"` field, whose value is an integer denoting after how many messages the summary is updated
- `RAG`
    - Stores all messages in a vector database and retrieves similar messages to that received in a conversation
    - Args should consist of two fields
        1. `"SelectLimit"`: an integer denoting how many messages it should retrieve in comparison
        2. `"WriteInterval"`: an integer denoting how often it should push new messages to the database

### `"DefaultDiscordChannel"`
If using the Discord body, this is the channel ID which the bot will voice its thoughts in if it
had not yet talked to anyone in Discord since it was started. Also, if `ExclusiveToChannel` is true, it
is the only channel the bot will respond to messages in.

### `"ExclusiveToChannel"`
Determines if the bot should only talk in `"DefaultDiscordChannel"` when using the Discord body. If false,
will talk whereever it receives a message.

### `"TimezoneSettings"`
This block has two fields:
1. `"HourShift"`: integer denoting hour shift from UTC the bot should use
2. `"MinuteShift"`: integer denoting minute shift from UTC the bot should use

### `"Logging"`
This is a block specifying how logging should be handled. It has three fields:
1. `"ConsoleLevel"`: the minimum level a logged message should have to be outputted to the console.
2. `"FileLevel"`: the minimum level a logged message should have to be outtputed to the log file.
3. `"FileLogPath"`: the path to the file to log to.

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

### Example
Here's what an example `appsettings.json` could look like:
```json
{
    "Settings": {
        "MemoryHandlers": [
            {
                "Handler": "Summary",
                "Args": {
                    "UpdateInterval": 10
                }
            },
            {
                "Handler": "SlidingWindow",
                "Args": {
                    "MaxMessages": 10
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
        "Logging": {
            "ConsoleLevel": "Warning",
            "FileLevel":    "Debug",
            "FileLogPath":  "wizard.log"
        }
    }
}
```