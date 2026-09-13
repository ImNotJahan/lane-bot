# Lane Mobile

Lane on an iPhone. A thin client over `Lane.Surfaces.Api` — it owns no model, no memory and
no tools, exactly like every other surface. Provider keys stay on the server; this app holds
only an address and its own client key.

Built with Expo. The chat tab alone runs in Expo Go; the voice tab does not — live PCM
capture is a native module, so voice needs a development build. See **Running** below.

---

## 1. Let the app in

Two changes on the Lane side, both configuration. Put them in
`Lane.Host/appsettings.local.json` so the committed defaults stay loopback-only.

**Bind somewhere the phone can reach.** The API surface listens on `127.0.0.1` by default —
deliberately, since it speaks for Lane. Add the tailnet address alongside it, keeping
loopback so `curl` and the existing companion client are unaffected:

```jsonc
"Urls": "http://127.0.0.1:5080;http://100.x.y.z:5080"
```

**Add a client.** Its key is its identity — sessions and participants are namespaced by
client id, so this app can never reach into another client's conversations:

```jsonc
{
  "Id": "ios",
  "Name": "Lane iOS",
  "KeyRef": "env:LANE_IOS_KEY",
  "GlobalUserId": "jahan"
}
```

`GlobalUserId` is what makes her profile memory of you follow the app, rather than treating
the phone as a stranger. Then in `.env`:

```sh
LANE_IOS_KEY=$(openssl rand -base64 32)
```

Check it before touching the app, so a failure later is the app's fault and not the server's:

```sh
curl -H "Authorization: Bearer $LANE_IOS_KEY" http://100.x.y.z:5080/v1/sessions
```

## 2. Run it

```sh
npm install
npx expo start
```

**Chat only** — install **Expo Go** on the phone and scan the QR code. The voice tab will
fail to load, because `react-native-audio-api` is not in the Expo Go binary.

**With voice** — you need a development build, which is a one-time cost:

```sh
npx expo run:ios --device          # needs Xcode locally
eas build --profile development --platform ios   # or build it in the cloud
```

Install that once, then `npx expo start --dev-client` and it behaves like Expo Go, with the
native audio module present. A paid Apple Developer account matters here — free provisioning
expires every seven days.

Either way, the phone and this machine need to be on the same network for the dev server;
Lane herself is reached over the tailnet.

On first launch the app asks for the address, the client key and a conversation name. The
default conversation is `shared:pocket` — the `shared:` prefix puts it outside any one
client's namespace, so a second client (a watch app later) can join the same conversation
rather than starting its own.

## 3. What it does

### Chat

- Loads history by memory group, so a voice session and a text one read as one conversation
- Streams the reply as it is written, with `delta` frames appended live
- Shows what she is doing when she uses a tool
- Stops a turn in flight — `POST /cancel`, the same barge-in the voice socket sends
- Says so when she chooses not to answer, rather than leaving a message that vanishes

### Voice

An open microphone, running until **End session** is pressed or the tab is left. It streams
PCM up the socket and plays her reply back as it arrives — server-side Azure recognition and
ElevenLabs synthesis, with the barge-in already in `Lane.Audio`, so talking over her works.

**Tell speakers apart** sets `?diarize=true`, which registers the socket through
`AudioRouter.RegisterShared` instead of `Register` — `SpeakerAttribution.Diarized` rather
than `Known` — and puts the audio through the voiceprints layer. Leave it off for a phone in
your hand: the turn is then attributed to the client's own participant, the one carrying
`GlobalUserId`. Turn it on for a phone on a table with several people round it.

The session deliberately does **not** pass `?speaker=`. Naming a speaker mints a fresh
participant under `{client}:{name}` with no `GlobalUserId`, which would quietly detach the
conversation from everything Lane knows about you.

Credentials live in the iOS keychain via `expo-secure-store`.

## Layout

| Path | Holds |
|---|---|
| `src/api/types.ts` | Mirrors `Lane.Surfaces.Api/Contracts.cs`. camelCase, nulls omitted. |
| `src/api/sse.ts` | `SseDecoder`, and the XHR transport that feeds it. |
| `src/api/client.ts` | `LaneClient` — sessions, history, cancel, streamed send. |
| `src/api/voice.ts` | `VoiceSocket` — binary PCM both ways, JSON control frames. |
| `src/audio/pcm.ts` | Float ↔ 16-bit PCM, and a level meter. |
| `src/audio/session.ts` | `VoiceSession` — microphone, socket and playback as one thing. |
| `src/screens/` | Setup, chat and voice. |
| `src/components/` | Bubbles, composer, tab bar. |

**Why XHR and not `fetch`.** React Native's `fetch` cannot stream a response body, and the
endpoint is a POST with a JSON body, which rules out `EventSource` as well. Incremental
delivery has to come from XHR's growing `responseText`, which is what `openSse` reads.

**Why the decoder is separate from the transport.** A frame can be split across any number of
reads, and that is the part worth testing without a socket. `npm run check:sse` drives it
with byte-for-byte `SseWriter` output chopped one character at a time.

**Why the sample rate is learned, not set.** `onAudioReady` is documented to deliver
whatever the hardware could actually provide, which may not be what was requested, and
`ApiAudioSource.IsSupported` accepts only 16 kHz mono or 48 kHz mono/stereo. So the socket
opens *after* the first buffer arrives, at that buffer's real rate, and an unsupported rate
is reported rather than resampled. Guessing wrong would not fail — it would just make her
mishear, which is much harder to diagnose than a refused handshake.

**Why playback calls `context.resume()`.** Creating an `AudioContext`, building the graph and
starting a source node is not enough to make sound: in `react-native-audio-api` the output
device is opened by `tryStartDriver`, which is only reachable through `resume()` or `start()`,
and a source node starting does not reach it. Without that call the graph renders perfectly
into a device that was never opened — silence, with no error raised anywhere.

**Why `voiceChat` mode.** It is what enables the platform's echo cancellation. Without it the
microphone hears Lane through the speaker and she interrupts herself.

## Checks

```sh
npm run typecheck   # tsc
npm run check:sse   # frame decoding, against real SseWriter output
npx expo export --platform ios   # proves it bundles
```

## Notes

- `NSAllowsArbitraryLoads` is set, because a tailnet address is plain HTTP over a WireGuard
  tunnel and ATS has no notion of that being safe. Traffic is still encrypted by Tailscale.
- Nothing arrives while the app is closed. When Lane volunteers a remark through the
  monologue it reaches this session's queue, but with no stream open there is nowhere for it
  to land. Seeing those would need APNs, which is a server-side addition.
- Expo Go is for development. A permanent home-screen install means an EAS build or Xcode,
  and needs a paid Apple Developer account — free provisioning expires every seven days.
  None of the code changes for that.
- `react-native-gesture-handler` and `react-native-reanimated` are dependencies of
  `react-native-audio-api`'s player UI, which this app never renders. They are not declared
  as peer dependencies in 0.13.3, but Metro resolves the whole module graph, so the bundle
  fails without them. They can go if that is ever fixed upstream.
- Background audio is enabled, so a voice session survives the app being backgrounded. It
  ends on **End session**, on leaving the tab, or when the app is terminated.
