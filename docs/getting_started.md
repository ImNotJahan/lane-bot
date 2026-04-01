# Getting started

1. [Setup](#setup)
2. [Configuration](#configuration)
3. [Running](#running)

## Setup
In order to get an instance of Lane up and running, you'll need a few things
first.

The only required thing is an API key for one of the supported LLMs. Currently this is only Anthropic LLMs, so you'll need a Claude API key.

If you want to use the Discord body, you'll also need a Discord API key for an application with a bot.

Lastly, if you want to use the `RAG` memory handler, you'll need to get a Qdrant API key and an associated endpoint, and an OpenAI API key and endpoint for an embedding model.

Once you have your keys (and possibly endpoints), you need to put them all in a `.env` file near whereever your binary is. The format should be as shown below:

```
DISCORD_API_KEY=
ANTHROPIC_API_KEY=
QDRANT_API_KEY=
QDRANT_ENDPOINT=
OPENAI_EMBEDDING_ENDPOINT=
OPENAI_EMBEDDING_API_KEY=
```

For more information, take a look at the [.env documentation](configuration.md#env).

## Configuration
There are a number of settings you can configure. Notably, if you don't have all
the things set up for a `RAG` memory handler, you wont want that in your configuration. For information on configuring your bot, check out the
[appsettings.json documentation](configuration.md#appsettingsjson).

## Running
Just run the binary to start up the bot. You can use the command-line argument `discord` or `terminal` to specify which body to use- by default the terminal body will be used.