# Customizing Lane

Lane's personality, tone, and behavior are entirely defined by a set of editable Markdown prompt files under `Wizard/LLM/Prompts/`.

## Prompt Files Overview

| File | Role |
|---|---|
| `Respond.md` | Base system prompt for generating responses |
| `Respond_Memory.md` | Template: injects long-term memory context into responses |
| `Respond_Dynamic.md` | Template: injects recent conversation history and enthusiasm level |
| `Monologue.md` | Base system prompt for internal thoughts |
| `Monologue_Memory.md` | Template: injects long-term memory context into monologue |
| `Monologue_Dynamic.md` | Template: injects recent conversation history and current time |
| `Routing.md` | Base system prompt for the routing classifier |
| `Routing_Dynamic.md` | Template: injects recent conversation history for routing decisions |
| `Summarize.md` | System prompt used by the `Summary` memory handler |

## Base Prompts vs. Templates

**Base prompts** (`Respond.md`, `Monologue.md`, `Routing.md`, `Summarize.md`) are passed directly to the LLM as system prompts. Edit these freely to change personality, tone, and behavioral rules.

**Template files** (`*_Memory.md`, `*_Dynamic.md`, `Routing_Dynamic.md`) contain `{0}`, `{1}` format placeholders that are filled in at runtime with context data. **Do not remove these placeholders** — the application will fail if they are missing. You can change surrounding text or formatting, but keep the placeholders in place.

| File | `{0}` | `{1}` |
|---|---|---|
| `Respond_Memory.md` | Long-term memory messages | — |
| `Respond_Dynamic.md` | Recent conversation history | Enthusiasm level (`low`/`neutral`/`high`) |
| `Monologue_Memory.md` | Long-term memory messages | — |
| `Monologue_Dynamic.md` | Recent conversation history | Current timestamp |
| `Routing_Dynamic.md` | Recent conversation history | — |

## Changing Lane's Personality

`Respond.md` is the primary file to edit. It controls:

- **Tone and personality** — how Lane comes across in conversation
- **Language style** — capitalization, slang, punctuation habits, emoji rules
- **Conversation behavior** — response length, question avoidance, character rules

`Monologue.md` controls Lane's internal thought loop — what she thinks about when idle, how often she decides to speak unprompted, and the style of messages she sends spontaneously. See [how_it_works.md](how_it_works.md#monologue) for details on the monologue system.

## Enthusiasm System

The routing step produces a float score that is bucketed into three enthusiasm levels passed to `Respond_Dynamic.md`:

| Score | Enthusiasm label |
|---|---|
| ≥ 0.8 | `high` |
| ≥ 0.5 | `neutral` |
| < 0.5 | `low` |

`Respond.md` should describe what each level means for Lane's behavior. By default, `high` makes her more expressive and `low` makes her more terse.

## Changing the Summarizer

`Summarize.md` is used by the `Summary` memory handler to produce a running summary of the conversation. Edit it to change the format, length, or priority of what gets preserved. The prompt receives the existing summary as `{0}` and new messages as `{1}`.
