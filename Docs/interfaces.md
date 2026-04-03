# Interfaces

Lane is built around a set of pluggable interfaces. Implementing one lets you swap out a component (e.g., a different LLM provider, a new TTS service, a custom memory backend) without touching the rest of the codebase.

---

## `ILLM` — Language Model

**File**: `Wizard/LLM/ILLM.cs`

The interface for the language model backend.

```csharp
public interface ILLM
{
    Task<MessageContainer> Prompt(
        List<MessageContainer> context,
        string                 systemPrompt,
        string                 cachedDynamicPrompt = "",
        string                 dynamicPrompt       = ""
    );
}
```

### Parameters

| Parameter | Description |
|---|---|
| `context` | The conversation messages to send as the human turn |
| `systemPrompt` | The static system prompt (e.g. contents of `Respond.md`) |
| `cachedDynamicPrompt` | A dynamic system prompt suffix that is relatively stable across calls (e.g. memory context). Passed as a cache-eligible prefix where supported. |
| `dynamicPrompt` | A dynamic system prompt suffix that changes every call (e.g. recent conversation, current timestamp) |

### Returns

A `MessageContainer` containing the LLM's response text.

### Current Implementation

`Claude` (`Wizard/LLM/Claude.cs`) — uses Anthropic Claude Haiku 4.5. The three prompt segments are assembled into a single system prompt, with `cachedDynamicPrompt` marked for prompt caching where the API supports it.

---

## `IMemoryHandler` — Memory Backend

**File**: `Wizard/Memory/IMemoryHandler.cs`

The interface for a memory storage and retrieval backend.

```csharp
public interface IMemoryHandler
{
    Task RememberMessage(MessageContainer message);
    Task<List<MessageContainer>> RecallMemory(MessageContainer? message);
    bool IsRecent();
    JToken Serialize();
    void   Deserialize(JToken data);
}
```

### Methods

| Method | Description |
|---|---|
| `RememberMessage(message)` | Store a message in this handler's backing store |
| `RecallMemory(message?)` | Retrieve relevant messages; `message` is the query (may be null for monologue calls) |
| `IsRecent()` | Return `true` if this handler tracks the current/recent conversation; `false` if it retrieves older long-term context. Controls which prompt template the recalled messages are injected into. |
| `Serialize()` | Serialize handler state to JSON for persistence |
| `Deserialize(data)` | Restore handler state from a previously serialized JSON token |

### Current Implementations

| Class | `IsRecent()` | Description |
|---|---|---|
| `SlidingWindow` | `true` | Keeps a fixed-size window of the most recent messages. Can be configured to track either regular messages or thoughts (`ForThoughts`). |
| `Summary` | `false` | Maintains a running LLM-generated summary of the full conversation history, updated every N messages. |
| `RAG` | `false` | Stores all messages as vector embeddings in Qdrant and retrieves the most semantically similar ones to the current message. |

---

## `IMouth` — Text-to-Speech

**File**: `Wizard/Head/Mouths/IMouth.cs`

The interface for a TTS provider.

```csharp
public interface IMouth
{
    Task<byte[]> Speak(string text);
}
```

### Parameters

| Parameter | Description |
|---|---|
| `text` | The text to synthesize |

### Returns

Raw PCM audio bytes, which the Discord body Opus-encodes and streams to the voice channel.

### Current Implementations

| Class | Provider | Relevant config keys |
|---|---|---|
| `ElevenlabsTTS` | ElevenLabs | `ELEVENLABS_KEY`, `Speech.Voice`, `Speech.Stability`, `Speech.Similarity`, `Speech.Tune` |
| `AzureTTS` | Azure Cognitive Services | `AZURE_KEY`, `AZURE_REGION`, `Speech.Voice`, `Speech.Tempo`, `Speech.Pitch`, `Speech.Rate`, `Speech.Tune` |

---

## `IEar` — Speech-to-Text

**File**: `Wizard/Head/Ears/IEar.cs`

The interface for an STT provider.

```csharp
public interface IEar
{
    Task<string> Listen();
}
```

### Returns

A transcribed string from the audio stream. Returns an empty or whitespace string if nothing intelligible was heard; the caller skips these.

Each `IEar` instance is bound to a single user's audio stream (`DiscordAudioStream`) and runs in a per-user loop for the duration of their time in the voice channel.

### Current Implementations

| Class | Provider | Relevant config keys |
|---|---|---|
| `AzureSTT` | Azure Cognitive Services | `AZURE_KEY`, `AZURE_REGION` |

---

## `MessageContainer`

**File**: `Wizard/LLM/MessageContainer.cs`

Not an interface, but the core data type passed between all components. Wraps a message with its author, type, and timestamp.

### Author

| Value | Meaning |
|---|---|
| `Author.User` | A message from a human |
| `Author.Bot` | A message or thought from Lane |

### MessageType

| Value | Meaning |
|---|---|
| `MessageType.Text` | A regular text message |
| `MessageType.Image` | An image (stored as a URL; sent to the LLM as an image content block) |
| `MessageType.Thought` | An internal monologue thought; wrapped in `<thought>` tags when sent to the LLM, and prefixed with `"Lane thinks:"` in context strings |
