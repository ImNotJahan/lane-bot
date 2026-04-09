# Lane — Privacy Policy

*Last updated: April 8, 2026*

---

## 1. Overview

This Privacy Policy describes how Lane ("we", "the bot", "the developer") collects, uses, stores, and protects information obtained through your interactions with the Lane Discord bot. By using Lane, you agree to the practices described in this policy.

---

## 2. Information We Collect

### 2.1 Message Content
When you send a message to Lane, the content of that message is processed by an external AI inference provider to generate a response. Messages may also be stored as part of Lane's memory system (see Section 3).

### 2.2 Discord User Data
Lane collects and stores the following Discord-provided identifiers:

- **Username** — used to personalize responses and conversation context.

Lane does **not** collect your email address, IP address, or any information outside of what is made available through Discord's API during normal bot interaction.

### 2.3 Voice Interactions
If you interact with Lane via voice channels:

- Audio from voice channels is **temporarily recorded and transmitted to Azure Cognitive Services** for speech-to-text (STT) transcription. This audio is stored only for the duration required to process the transcription and is deleted immediately afterward.
- Text submitted for TTS output is handled by an external TTS provider (e.g. ElevenLabs or Azure Neural TTS).
- Lane does **not** retain audio recordings beyond the transcription window, and transcribed text may be stored as part of Lane's memory system (see Section 3).

---

## 3. Memory System

Lane uses a vector database (RAG) to provide persistent, context-aware conversations. This means:

- Messages you send may be converted into **vector embeddings** and stored in a database.
- These embeddings are used to retrieve relevant past context when you interact with Lane in the future.
- Stored data is associated with your **Discord Username**.
- Memory persists across sessions until cleared by you or a server administrator.

This memory system is core to Lane's functionality. If you do not want your messages stored, you should not interact with Lane, or you may request memory deletion (see Section 6).

---

## 4. How We Use Your Information

Information collected by Lane is used exclusively to:

- Generate contextually relevant AI responses.
- Maintain conversational memory across sessions.
- Personalize Lane's behavior and tone based on past interactions.
- Diagnose bugs and improve bot stability.

Your data is **never** used for advertising, sold to third parties, or shared outside of the services required to operate Lane.

---

## 5. Third-Party Services

Lane relies on the following categories of third-party services to function. Each is subject to their own privacy policy:

| Service | Purpose |
|---|---|
| Anthropic | Generating responses from your messages |
| ElevenLabs | Synthesizing voice output for TTS features |
| Qdrant (vector database) | Storing and querying memory embeddings |
| Discord | Platform delivery of all interactions |

We encourage you to review the privacy policies of these providers independently.

---

## 6. Your Rights and Data Deletion

You have the right to request deletion of your stored memory data at any time. To do so:

1. Contact a server administrator, or
2. Reach out to the Lane development team at [hiimjahan@gmail.com](mailto:hiimjahan@gmail.com).

Upon a verified request, all vector embeddings associated with your Discord User ID will be permanently deleted from the database. Note that data already processed by third-party inference providers may be subject to their own retention policies.

---

## 7. Data Retention

- **Memory embeddings** are retained indefinitely until deleted by the user or administrator.
- **Message content** sent to the AI inference provider is subject to that provider's own data retention policy and is not stored long-term by Lane itself.
- **TTS input text** is not retained by Lane after the audio is generated.
- **STT audio recordings** are temporarily stored only for the duration of Azure transcription processing and deleted immediately afterward.

---

## 8. Security

We take reasonable measures to protect stored data, including restricting database access to the Lane application only. However, no system is completely secure. We cannot guarantee the absolute security of your data and are not liable for unauthorized access resulting from circumstances beyond our control.

---

## 9. Children's Privacy

Lane is not intended for users under the age of 13, in accordance with Discord's own Terms of Service. We do not knowingly collect data from minors. If you believe a minor has interacted with Lane and had data stored, please contact us for immediate deletion.

---

## 10. Changes to This Policy

This Privacy Policy may be updated at any time. Significant changes will be announced in the relevant Discord server(s) where Lane is deployed. Continued use of Lane following any update constitutes acceptance of the revised policy.

---

## 11. Contact

For privacy-related inquiries, data deletion requests, or concerns, please contact:

Jahan Rashidi
[hiimjahan@gmail.com](mailto:hiimjahan@gmail.com)