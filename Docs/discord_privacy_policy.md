# Lane — Privacy Policy

*Last updated: September 15, 2026*

---

## 1. Overview

This Privacy Policy describes how Lane ("we", "the bot", "the developer") collects, uses, stores, and protects information obtained through your interactions with the Lane Discord bot. By using Lane, you agree to the practices described in this policy.

---

## 2. Information We Collect

### 2.1 Message Content
When you send a message to Lane, the content of that message is processed by an external AI inference provider or a community node (see Section 4) to generate a response. Messages may also be stored as part of Lane's memory system (see Section 3).

### 2.2 Discord User Data
Lane collects and stores the following Discord-provided identifiers:

- **Username** — used to personalize responses and conversation context.

Lane does **not** collect your email address, IP address, or any information outside of what is made available through Discord's API during normal bot interaction.

### 2.3 Voice Interactions
If you interact with Lane via voice channels:

- Audio from voice channels is **temporarily recorded and transmitted to Azure Cognitive Services** for speech-to-text (STT) transcription. This audio is stored only for the duration required to process the transcription and is deleted immediately afterward.
- Text submitted for TTS output is handled by an external TTS provider (e.g. ElevenLabs or Azure Neural TTS).
- Lane does **not** retain audio recordings beyond the transcription window, and transcribed text may be stored as part of Lane's memory system (see Section 3).

### 2.4 Voiceprints
Where voiceprints are enabled, Lane creates **voiceprints** to tell apart the people speaking on a shared microphone:

- A voiceprint is a numeric representation of the characteristics of a voice, averaged from several utterances. It is computed by Lane itself and is not a recording, but it is derived from your voice and can be used to recognize you.
- Any utterance long enough to measure is compared with the voiceprints Lane already knows. A voice Lane does not recognize is stored as a new voiceprint, with its own account and memory, **without your needing to identify yourself**.
- If you **claim** your voice (by speaking a one-time phrase Lane gives you), the voiceprint is bound to your identity. From then on, what you say by voice is attributed to you and linked to your Discord account, any other accounts you have linked, your memory, and your chosen name.
- A voice is never bound to a person by a match alone, only by a claim. However, recognition can be wrong, so speech may occasionally be attributed to the wrong voice.

---

## 3. Memory System

Lane uses a vector database (RAG) to provide persistent, context-aware conversations. This means:

- Messages you send may be converted into **vector embeddings** and stored in a database.
- These embeddings are used to retrieve relevant past context when you interact with Lane in the future.
- Stored data is associated with your **Discord Username**, and with any accounts or voiceprints you have linked to it.
- Memory persists across sessions until cleared by you or a server administrator.

This memory system is core to Lane's functionality. If you do not want your messages stored, you should not interact with Lane, or you may request memory deletion (see Section 8).

---

## 4. Community Nodes

Lane may route requests to **nodes**: independently operated computers, run by members of the community rather than by the developer, that generate responses on Lane's behalf.

- A request sent to a node can include your message, the surrounding conversation, display names, relevant memory, and Lane's instructions.
- Each response is signed by the node that produced it, so the developer can tell which node handled a request.
- Node operators may process requests with their own models or forward them to AI providers of their choosing.
- Node operators are not bound by this Privacy Policy. The developer does not control, and cannot guarantee, how nodes or their providers handle, log, or retain the data they receive.

---

## 5. How We Use Your Information

Information collected by Lane is used to:

- Generate contextually relevant AI responses.
- Maintain conversational memory across sessions.
- Recognize speakers by voice, where voiceprints are enabled.
- Personalize Lane's behavior and tone based on past interactions.
- Diagnose bugs and improve bot stability.
- Train, fine-tune, and evaluate machine learning models (see Section 5.1).

Your data is **never** used for advertising or sold to third parties, and is shared only with the services and nodes required to operate Lane.

### 5.1 Model Training
Messages and voice transcripts may be used as training and evaluation data for machine learning models. Before this data is used:

- **Names are redacted**, including usernames, display names, and chosen names.
- Voiceprints and audio are **not** included.

Redaction covers names only. Other information in the text of a message may remain, so avoid sharing sensitive details with Lane.

---

## 6. Third-Party Services

Lane relies on the following categories of third-party services to function. Each is subject to their own privacy policy:

| Service | Purpose |
|---|---|
| Anthropic | Generating responses from your messages |
| Community nodes | Generating responses from your messages, using models and providers chosen by each node operator |
| Azure Cognitive Services | Transcribing voice audio |
| ElevenLabs | Synthesizing voice output for TTS features |
| Qdrant (vector database) | Storing and querying memory embeddings |
| Discord | Platform delivery of all interactions |

We encourage you to review the privacy policies of these providers independently.

---

## 7. Your Rights and Data Deletion

You have the right to request deletion of your stored memory data at any time. To do so:

1. Contact a server administrator, or
2. Reach out to the Lane development team at [hiimjahan@gmail.com](mailto:hiimjahan@gmail.com).

Upon a verified request, all vector embeddings associated with your Discord User ID will be permanently deleted from the database. Note that data already processed by third-party inference providers or community nodes may be subject to their own retention policies, and data already used to train a model cannot be removed from that model.

To delete your voiceprints, ask Lane to forget your voice, either out loud or from any of your linked accounts. Every voiceprint bound to you is deleted immediately. Transcripts of what you said are not deleted by this; request memory deletion for those.

---

## 8. Data Retention

- **Memory embeddings** are retained indefinitely until deleted by the user or administrator.
- **Voiceprints** are retained until you ask Lane to forget your voice, or until deleted by an administrator. Unclaimed voiceprints may be retained indefinitely.
- **Message content** sent to the AI inference provider or a community node is subject to that provider's or node operator's own data retention practices and is not stored long-term by Lane itself.
- **Name-redacted training data** may be retained indefinitely.
- **TTS input text** is not retained by Lane after the audio is generated.
- **STT audio recordings** are temporarily stored only for the duration of Azure transcription processing and deleted immediately afterward.

---

## 9. Security

We take reasonable measures to protect stored data, including restricting database access to the Lane application only. However, no system is completely secure. We cannot guarantee the absolute security of your data and are not liable for unauthorized access resulting from circumstances beyond our control, including the handling of data by community nodes.

---

## 10. Children's Privacy

Lane is not intended for users under the age of 13, in accordance with Discord's own Terms of Service. We do not knowingly collect data from minors. If you believe a minor has interacted with Lane and had data stored, please contact us for immediate deletion.

---

## 11. Changes to This Policy

This Privacy Policy may be updated at any time. Significant changes will be announced in the relevant Discord server(s) where Lane is deployed. Continued use of Lane following any update constitutes acceptance of the revised policy.

---

## 12. Contact

For privacy-related inquiries, data deletion requests, or concerns, please contact:

Jahan Rashidi
[hiimjahan@gmail.com](mailto:hiimjahan@gmail.com)
