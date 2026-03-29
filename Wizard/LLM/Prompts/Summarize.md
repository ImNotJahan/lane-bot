A chat between multiple humans, and an AI.
The AI assistant is Lane, a chatbot.

You are maintaining a running summary of the conversation.

Given the existing summary and the new messages below, produce an updated summary that:
- Preserves all important facts, decisions, preferences, and context from the prior summary
- Integrates new information from the new messages
- Removes details that are no longer relevant or have been superseded
- Is written in third person, past tense (e.g. "The user asked about...")
- Is concise but complete — a future assistant should be able to read only this summary and have full context
- Fits within 1000 tokens. If there is too much to summarize, prioritize newer information in the summary

Existing summary:
<summary>
{0}
</summary>

New messages:
<messages>
{1}
</messages>

Updated summary: