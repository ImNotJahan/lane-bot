# Lane

Lane is a personality-driven agent that lives on several surfaces at once: Discord, a terminal, and an HTTP API. Each
conversation is its own session, so she never answers one person in the middle of someone else's conversation. Memory
is set up separately, which means she can remember a person everywhere and still reply only where she was spoken to.

These pages are for people who use a running Lane rather than operate one:

1. [HTTP API](api.md): talk to Lane from your own app, stream her replies, watch what she is doing, and speak to
   her over a voice socket.
    - [Authentication](api.md#authentication)
    - [Sessions](api.md#sessions)
    - [Sending messages](api.md#sending-messages)
    - [Event feed](api.md#event-feed)
    - [Voice socket](api.md#voice-socket)
2. [Nodes](nodes.md): lend Lane a model, earn credits for it, and spend them to sponsor where she listens.
    - [The node SDK](nodes.md#the-node-sdk)
    - [Node identity](nodes.md#node-identity)
    - [Credits, sponsorship and the portal](nodes.md#credits-sponsorship-and-the-portal)
    - [Wire protocol](nodes.md#wire-protocol)
3. [Identities](identities.md): how Lane decides who is speaking, and how to make her recognise you across surfaces
   and by voice.
    - [Accounts and people](identities.md#accounts-and-people)
    - [Linking your accounts](identities.md#linking-your-accounts)
    - [Choosing your name](identities.md#choosing-your-name)
    - [Voices](identities.md#voices)

## Other pages

- [Discord Terms of Service](discord_tos.md)
- [Discord Privacy Policy](discord_privacy_policy.md)

## Archive

The v2 documentation covers the single-surface bot that came before this rewrite. It is kept for reference, but it
does not describe how Lane works now: [v2 docs](v2/index.md).
