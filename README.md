# Wizard/Lane (version 2)
Lane is a chatbot designed to feel authentic, following in the spirit of
the Twitch streamer Neuro-sama.

## Current features
- Can be interacted with through either terminal or discord
- Has window, summary, and RAG memory
- Chooses whether to respond to messages or not
- Can see images
- Has an internal monologue
- Can initiate conversations of their own volition

## Roadmap
- Support for more interactive mediums
- Support for more models
- Sentiment analysis of own messages for creating a longterm run of mood
- Optimization of context used

## Tech stack
Fully written in C#. Using Claude Haiku 4.5 for the main LLM and OpenAI 
text-embedding-3-small for vector embedding. Qdrant is used for the vector
database.

## Usage
You'll need to specify the folowing values in a `.env` file at the root of the `Wizard` folder:
```
DISCORD_API_KEY=
ANTHROPIC_API_KEY=
QDRANT_API_KEY=
QDRANT_ENDPOINT=
OPENAI_EMBEDDING_ENDPOINT=
OPENAI_EMBEDDING_API_KEY=
```