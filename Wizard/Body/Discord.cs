using System.Collections.Concurrent;
using Microsoft.CognitiveServices.Speech.Audio;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Logging;
using Wizard.Head;
using Wizard.Head.Ears;
using Wizard.Head.Mouths;
using Wizard.LLM;
using Wizard.Utility;
using ConcentusDecoder = Concentus.IOpusDecoder;

namespace Wizard.Body
{
    public sealed class Discord
    {
        readonly GatewayClient client;
        readonly Bot           bot;

        ulong?       recentChannelId = null;
        VoiceClient? voiceClient     = null;

        readonly ulong  defaultChannel;
        readonly bool   exclusiveToChannel;

        IMouth? mouth = null;

        bool lastMessageInVC = false;

        private readonly ConcurrentDictionary<ulong, DiscordAudioStream>      _userStreams  = new();
        private readonly ConcurrentDictionary<ulong, string>                  _ssrcToUser   = new();
        private readonly ConcurrentDictionary<ulong, ulong>                   _userIdToSsrc = new();
        private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _listenerCts  = new();

        Guild? guild;

        private readonly ConcurrentDictionary<ulong, ConcentusDecoder> _decoders = new();

        public Discord(Bot bot, ulong defaultChannel)
        {
            client = new GatewayClient(
                new BotToken(DotNetEnv.Env.GetString("DISCORD_API_KEY")),
                new GatewayClientConfiguration
                {
                    Intents = GatewayIntents.AllNonPrivileged | GatewayIntents.MessageContent
                }
            );

            this.defaultChannel    = defaultChannel;
            exclusiveToChannel     = Settings.instance?.ExclusiveToChannel == true;
            this.bot               = bot;

            bot.OnHadGoodThought    += OnHadGoodThought;
            client.Ready            += OnReady;
            client.GuildCreate      += OnGuildCreate;
            client.MessageCreate    += OnMessageCreate;
            client.VoiceStateUpdate += OnVoiceStateUpdate;
        }

        public async Task ConnectAsync()
        {
            await client.StartAsync();
        }

        private ValueTask OnReady(ReadyEventArgs args)
        {
            bot.StartMonologue();
            return default;
        }

        private ValueTask OnGuildCreate(GuildCreateEventArgs args)
        {
            Guild? guild = args.Guild;

            if (guild is null || !guild.Channels.ContainsKey(defaultChannel)) return default;

            _ = Task.Run(() => ConnectVoiceAsync(guild));
            return default;
        }

        private async Task ConnectVoiceAsync(Guild guild)
        {
            if (voiceClient is not null) return;

            this.guild = guild;

            VoiceGuildChannel? voiceChannel = guild.Channels.Values
                                                   .OfType<VoiceGuildChannel>()
                                                   .FirstOrDefault();

            if (voiceChannel is null)
            {
                Logger.LogError("ConnectVoiceAsync: no voice channel in guild {Guild}", guild.Name);
                return;
            }

            if(Settings.instance is not null)
            {
                if(Settings.instance.Speech is not null)
                {
                    mouth = Settings.instance.Speech.Mouth switch
                    {
                        "ElevenLabs" => new ElevenlabsTTS(),
                        "Azure"      => new AzureTTS(),
                        _            => throw new Exception("Invalid mouth " + Settings.instance.Speech.Mouth)
                    };
                }
                else
                {
                    Logger.LogWarning("No speech settings set");
                }

                if(Settings.instance.Hearing is null)
                {
                    Logger.LogWarning("No hearing settings set");
                }
            }

            try
            {
                voiceClient = await client.JoinVoiceChannelAsync(
                    guild.Id,
                    voiceChannel.Id,
                    new()
                    {
                        ReceiveHandler = new VoiceReceiveHandler(),
                        Logger         = new ConsoleLogger()
                    }
                );

                voiceClient.Disconnect   += OnDisconnect;
                voiceClient.Speaking     += OnSpeaking;
                voiceClient.VoiceReceive += OnVoiceReceieve;
                
                await voiceClient.StartAsync();

                Logger.LogInformation("Voice connected to {Channel}", voiceChannel.Name);
            }
            catch (Exception exception)
            {
                Logger.LogError("Voice connect failed: {Error}", exception.Message);
            }

            Logger.LogInformation("Beginning to listen");
        }

        private ValueTask OnDisconnect(DisconnectEventArgs args)
        {
            voiceClient = null;

            return default;
        }

        private ValueTask OnVoiceReceieve(VoiceReceiveEventArgs args)
        {
            // If the timestamp is null, the packet was lost.
            // We skip it, which mirrors the packet loss to the echo recipients.
            if (args.Timestamp is not { } timestamp) return default;

            ulong speakerKey = args.Ssrc;

            const int OpusChannels = 1;

            var decoder = _decoders.GetOrAdd(
                speakerKey,
                _ => DiscordSpeechAudio.CreateDecoder(OpusChannels)
            );

            float[] pcm48k = DiscordSpeechAudio.DecodeOpusToPcm(
                decoder,
                args.Frame.ToArray(),
                OpusChannels
            );

            if (pcm48k.Length == 0)
            {
                Logger.LogDebug("OnVoiceReceieve [{Ssrc}]: decoded 0 samples, skipping", args.Ssrc);
                return default;
            }

            byte[] pcm16kMono = DiscordSpeechAudio.Convert48kTo16kMonoPcm16(
                pcm48k,
                OpusChannels
            );

            float peak = 0f;
            foreach (float s in pcm48k) { float a = Math.Abs(s); if (a > peak) peak = a; }

            bool hasListener = _listenerCts.ContainsKey(args.Ssrc);

            _userStreams.GetOrAdd(args.Ssrc, _ => new DiscordAudioStream()).Write(pcm16kMono);

            return default;
        }

        private ValueTask OnSpeaking(SpeakingEventArgs args)
        {
            _ = Task.Run(async () => {
                if(args.UserId == client.Id)           return;
                if(guild is null)                      return;
                if(Settings.instance?.Hearing is null) return;

                string username = (await guild.GetUserAsync(args.UserId)).Username;

                _ssrcToUser[args.Ssrc]    = username;
                _userIdToSsrc[args.UserId] = args.Ssrc;
                lastMessageInVC            = true;

                var cts = new CancellationTokenSource();
                if (_listenerCts.TryAdd(args.Ssrc, cts))
                    _ = Task.Run(() => UserListenLoop(args.Ssrc, cts.Token));
                else
                    cts.Dispose();
            });

            return default;
        }

        private static IEar CreateEar(DiscordAudioStream stream) 
        {
            return Settings.instance!.Hearing!.Ear switch
            {
                "Azure" => new AzureSTT(stream),
                _       => throw new Exception("Invalid ear " + Settings.instance.Hearing.Ear)
            };
        }

        private async Task UserListenLoop(ulong ssrc, CancellationToken ct)
        {
            DiscordAudioStream userStream = _userStreams.GetOrAdd(ssrc, _ => new DiscordAudioStream());
            IEar ear                      = CreateEar(userStream);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string spoken = await ear.Listen();

                    if (ct.IsCancellationRequested) break;
                    if (spoken.IsWhiteSpace()) continue;

                    string username = _ssrcToUser.TryGetValue(ssrc, out string? u) ? u : "User";

                    Logger.LogInformation("[{User}] Heard: {Spoken}", username, spoken);

                    _ = RespondToMessage(username, "[Voice chat] " + spoken, [], true);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError("UserListenLoop [{Ssrc}]: {Error}", ssrc, ex.Message);
                    try   { await Task.Delay(1000, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private ValueTask OnVoiceStateUpdate(VoiceState state)
        {
            if (state.UserId == client.Id)  return default;
            if (state.ChannelId is not null) return default;

            if (_userIdToSsrc.TryRemove(state.UserId, out ulong ssrc))
                CleanupUser(ssrc);

            if (_userIdToSsrc.IsEmpty)
                lastMessageInVC = false;

            return default;
        }

        private void CleanupUser(ulong ssrc)
        {
            if (_listenerCts.TryRemove(ssrc, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            if (_userStreams.TryRemove(ssrc, out var stream))
                stream.Close();

            _ssrcToUser.TryRemove(ssrc, out _);
            _decoders.TryRemove(ssrc, out _);
        }

        private async void OnHadGoodThought(string thought) 
        {
            if(lastMessageInVC) await SayMessage(thought);
            else                await SendMessage(thought);
        }

        private ValueTask OnMessageCreate(Message message)
        {
            if (message.Author.Id  == client.Id)                           return default;
            if (exclusiveToChannel && message.ChannelId != defaultChannel) return default;

            recentChannelId = message.ChannelId;

            lastMessageInVC = false;

            _ = Task.Run(async () =>
            {
                List<string> imageUrls = [];
                foreach (Attachment attachment in message.Attachments)
                {
                    if (attachment.ContentType?.StartsWith("image/") == true) imageUrls.Add(attachment.Url);
                }

                await RespondToMessage(message.Author.Username, await FormatMessage(message), imageUrls);
            });

            return default;
        }

        private async Task RespondToMessage(string author, string message, List<string> imageUrls, bool inVC = false)
        {
            MessageContainer? response = await bot.OnMessageCreated(
                author,
                message,
                imageUrls
            );

            if (response is null) return;

            if(inVC) await SayMessage(response.GetContent());
            else     await SendMessage(response.GetContent());
        }

        private async Task SendMessage(string message)
        {
            ulong channelId = recentChannelId ?? defaultChannel;

            try
            {
                await client.Rest.SendMessageAsync(channelId, new() { Content = message });
            }
            catch (Exception ex)
            {
                Logger.LogError("SendMessage failed: {Error}", ex.Message);
            }
        }

        private async Task SayMessage(string message)
        {
            if (voiceClient is not null) await SpeakAsync(message, voiceClient);
        }

        private async Task SpeakAsync(string text, VoiceClient vc)
        {
            if (mouth is null) return;

            try
            {
                byte[] pcm = await mouth.Speak(text);

                await vc.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));

                Stream voiceStream = vc.CreateVoiceStream();
                using OpusEncodeStream opusStream = new(
                    voiceStream,
                    PcmFormat.Short,
                    VoiceChannels.Stereo,
                    OpusApplication.Voip
                );
                await opusStream.WriteAsync(pcm);
            }
            catch (Exception ex)
            {
                Logger.LogError("SpeakAsync failed: {Error}", ex.Message);
            }
        }

        private async Task<string> FormatMessage(Message message)
        {
            string content = await ResolveMentions(message);

            if (message.ReferencedMessage is not null)
                content += $" in response to {message.ReferencedMessage.Author.Username}: {message.ReferencedMessage.Content}";

            return content;
        }

        private async Task<string> ResolveMentions(Message message)
        {
            string content = message.Content;

            // resolve user mentions
            foreach (User user in message.MentionedUsers)
            {
                content = content.Replace($"<@{user.Id}>", $"@{user.Username}")
                                 .Replace($"<@!{user.Id}>", $"@{user.Username}");
            }
            
            // resolve role mentions
            foreach (ulong role in message.MentionedRoleIds)
            {
                string roleName = guild is null ? "role" : (await guild.GetRoleAsync(role)).Name;
                content = content.Replace($"<@&{role}>", $"@{roleName}");
            }

            return content;
        }
    }

    public class DiscordAudioStream
    {
        // Bounded at 50 frames (~1 s). DropOldest evicts stale audio if the
        // recognizer falls behind, keeping the queue current.
        private readonly System.Threading.Channels.Channel<byte[]> _queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new System.Threading.Channels.BoundedChannelOptions(50) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest }
        );

        private readonly PushAudioInputStream    _pushStream;
        private readonly CancellationTokenSource _cts = new();

        // 20 ms of silence at 16 kHz, 16-bit mono
        private static readonly byte[] Silence = new byte[640];

        // 25 x 20 ms = 500 ms - enough to exceed the 300 ms segmentation timeout
        // without flooding the push stream between utterances.
        private const int SilenceFrameLimit = 25;

        public DiscordAudioStream()
        {
            _pushStream = AudioInputStream.CreatePushStream(
                AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));

            _ = Task.Run(() => PumpAsync(_cts.Token));
        }

        internal PushAudioInputStream PushStream => _pushStream;

        public void Write(byte[] bytes) => _queue.Writer.TryWrite(bytes);

        public void Close()
        {
            _cts.Cancel();
            _queue.Writer.TryComplete();
            _pushStream.Close();
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            int silenceFramesSent = 0;

            while (!ct.IsCancellationRequested)
            {
                if (_queue.Reader.TryRead(out byte[]? chunk))
                {
                    silenceFramesSent = 0;
                    try   { _pushStream.Write(chunk); }
                    catch (Exception ex) { Logger.LogError("DiscordAudioStream pump failed: {Error}", ex.Message); break; }
                }
                else if (silenceFramesSent < SilenceFrameLimit)
                {
                    try   { _pushStream.Write(Silence); }
                    catch (Exception ex) { Logger.LogError("DiscordAudioStream silence pump failed: {Error}", ex.Message); break; }
                    silenceFramesSent++;
                }

                // Pace all writes to real-time so the push stream is never flooded.
                try   { await Task.Delay(20, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
