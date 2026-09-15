# Identities

Lane talks to people through many surfaces at once, so she keeps two ideas apart. An **account** is how one surface
knows someone. A **person** is one or more accounts that have been joined together. Which person someone is decides
what Lane remembers about them and what she calls them.

---

## Accounts and people

### Accounts

Every speaker is an account, written `surface:localId`. The surface part is the *configured instance*, so two Discord
bots are two separate namespaces.

| Where | Account |
|---|---|
| Discord | `discord.main:145898933151885325` (the bot instance's id, then the user's snowflake) |
| Terminal | `terminal:jahan` |
| API client | `api:ios` |
| Someone relayed by an API client | `api:ios:alice` |
| A named speaker on an API voice socket | `api:ios:Alice` |
| A sponsored API client | `api:sponsored.myapp` |
| A voice Lane recognises in a room | `api:voice/v-1a2b3c4d` (see [Voices](#voices)) |

Accounts can't collide across surfaces or clients. An API key can only ever create accounts inside its own client's
namespace, so it can't speak as another client's users or as someone on Discord.

### People

A person is a **global user id**, such as `jahan`, that one or more accounts belong to. An account with no global id
is a person on its own.

An account gets a global id in one of three ways:

1. **The operator's identity map** (`Lane:Identities`):
   ```jsonc
   "Identities": { "jahan": ["terminal:jahan", "discord.main:145898933151885325"] }
   ```
2. **An API client's `GlobalUserId`**, which makes the client itself that person.
3. **Linking in conversation**, which you can do yourself with [`link_identity`](#linking-your-accounts).

**Lane never guesses.** Two accounts called "Jahan" are two people until one of the above says otherwise, because
wrongly merging two strangers is far worse than failing to connect one person. Operator configuration always wins: a
link made in conversation can't move an account the operator has already assigned.

### What being one person changes

- **Memory.** Lane's memory has three scopes. *Session* memory, such as recent messages and the conversation summary,
  belongs to a conversation's memory group. *User* memory, such as what Lane knows about you, belongs to the person, so
  it follows you from Discord to the terminal to your app. *Global* memory, such as her thoughts and what she's read, is
  shared by everyone. The operator can limit some user memory to one-to-one conversations only.
- **Your name.** A name you choose is stored against the person, so it applies on every linked account.
- **Voices.** A voice you claim is bound to the person.

Changes apply **from your next message**. Linking doesn't move memory that was already written: anything Lane learned
about an account before it was linked stays with that account.

---

## Linking your accounts

To have Lane treat two of your accounts as one person:

1. **On the first account**, in a one-to-one conversation (a Discord DM, or a direct API session), ask Lane to link
   your accounts. She gives you a code like `K7M2-QXBP`.
2. **On the second account**, also one to one, tell her the code within **10 minutes**.

She links the two accounts as one person from the next message on.

The rules exist to keep someone from attaching themselves to *your* memory:

- **It only ever links the account that's speaking.** Lane can't be talked into linking someone else's account, and
  there's no way to name an account for her to link. The code proves the same person holds both accounts.
- **Both halves have to happen one to one.** A code shown in a channel could be used by anyone reading it.
- **Codes are single-use** and are spent even when an attempt fails. Asking again replaces your previous code. Case,
  spaces and the hyphen don't matter when you type it back.
- **Two accounts that are each already a person can't be merged.** Both already have memory under their own ids, so
  merging them is left to the operator. If only one side is already a person, the other account joins that person. If
  neither is, Lane creates a new global id from your name, such as `jahan-3f9a`.

### When proof is turned off

An operator can set `Lane:Tools:Identity:RequireProof` to `false`. Then there's no code: you ask on one account, and
say it's you on the other within 10 minutes. Both halves still have to happen one to one, and still only act on the
account that's speaking. But anyone who asks in turn gets linked, so this mode only suits an instance that nobody
untrusted can reach.

---

## Choosing your name

Ask Lane to call you something else ("call me Jax") and she will, from your next message on. The name replaces your
account's display name wherever Lane shows who spoke: in her prompt, in the transcript, in her session list and on the
dashboard. Ask her to go back to your account's name to undo it.

- It only ever renames **whoever is speaking**. Nobody can rename you.
- It's stored against the **person**, so it applies on all your linked accounts. If you chose a name before linking,
  it carries over.
- It can be up to **40 characters**. Line breaks, control characters and invisible formatting characters are removed.
- It can't be **Lane**, a name another person has already chosen, or the name of someone else in the same
  conversation.

Unlike linking, you can ask for this anywhere, not only one to one.

---

## Voices

When voiceprints are enabled on the instance (`Lane:Audio:Voiceprints`), Lane can tell apart the voices on a shared
microphone. This covers a microphone in a room, or an [API voice socket](api.md#voice-socket) opened with
`diarize=true`.

### How recognition works

- Every utterance long enough to measure (1.5 seconds by default) is compared with the voices Lane already knows.
- If it's close enough to one of them, it's attributed to that voice.
- If not, Lane remembers it as a **new voice** and calls it something like "Voice 3". Each unrecognised voice is its
  own account, with its own memory, and can [choose a name](#choosing-your-name) like any other account.
- **A match never makes a voice anyone.** Recognising a voice is a measurement, and measurements are sometimes wrong,
  so a voice only belongs to a person once that person has claimed it.

### Claiming your voice

1. **In a one-to-one text conversation**, on an account Lane already knows, ask her to learn your voice. She gives you
   **three words**, such as *lantern walrus pinecone*.
2. **Within 10 minutes**, say those words **out loud** somewhere Lane can hear you.

The voice that says the words is bound to you. From the next thing you say, Lane recognises you by voice, with your
memory and your name.

- The words have to be **spoken**. Typing them proves nothing about how you sound, and the words are used up anyway.
  Words are used because a speech recogniser reliably transcribes them, which isn't true of letters and digits.
- If a voice is already bound to someone else, it can't be claimed. It has to be forgotten first.
- A name you chose while you were still an unrecognised voice carries over to you.
- With `RequireProof` turned off, you skip the words: ask one to one, then speak in the room and tell her it's you. If
  more than one person is waiting to be recognised, she refuses to guess and asks you to try again one at a time.

### Forgetting your voice

Ask Lane to forget your voice, either out loud with that voice or from any of your accounts. She deletes every voice
bound to you, straight away. Everything you said stays in the transcript. Only the ability to recognise you by voice is
removed. Like the other identity tools, this only acts on whoever is asking.

---

## API clients

For apps built on the [HTTP API](api.md):

- **Your client is an account** (`api:<clientId>`). If the operator gives it a `GlobalUserId`, every message sent
  without an `author` counts as that person, including their user memory and chosen name.
- **Relayed people** (`author: { id, name }`) become `api:<clientId>:<id>`, shown under `name`. Keep `id` stable for
  each human, because it's the key their memory is stored under.
- **A voice socket's `speaker`** becomes `api:<clientId>:<speaker>`.
- **Current limitation:** relayed authors and voice socket speakers don't go through identity resolution. The operator
  map, links made in conversation and chosen names don't apply to them, so they can't be joined to a person yet. The
  client's own account and diarized voices are resolved normally.

---

## Node identities

The key that a [node](nodes.md#node-identity) connects with is a different kind of identity. It's a public key whose
key id earns credits, signs in to the portal and sponsors targets. It isn't a conversational account, has no memory,
and isn't linked to anyone Lane talks to.
