using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using Wizard.Head;
using Wizard.Head.Mouths;
using Wizard.LLM;
using Wizard.Utility;

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
        readonly IMouth mouth = new ElevenlabsTTS();

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

            bot.OnHadGoodThought   += OnHadGoodThought;
            client.Ready           += OnReady;
            client.GuildCreate     += OnGuildCreate;
            client.MessageCreate   += OnMessageCreate;
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

            VoiceGuildChannel? voiceChannel = guild.Channels.Values
                .OfType<VoiceGuildChannel>()
                .FirstOrDefault();

            if (voiceChannel is null)
            {
                Logger.LogError("ConnectVoiceAsync: no voice channel in guild {Guild}", guild.Name);
                return;
            }

            try
            {
                voiceClient = await client.JoinVoiceChannelAsync(guild.Id, voiceChannel.Id);
                voiceClient.Disconnect += _ => { voiceClient = null; return default; };
                await voiceClient.StartAsync();
                Logger.LogInformation("Voice connected to {Channel}", voiceChannel.Name);
            }
            catch (Exception ex)
            {
                Logger.LogError("Voice connect failed: {Error}", ex.Message);
            }
        }

        private async void OnHadGoodThought(string thought)
        {
            ulong channelId = recentChannelId ?? defaultChannel;

            try
            {
                await client.Rest.SendMessageAsync(channelId, new() { Content = thought });
            }
            catch (Exception ex)
            {
                Logger.LogError("SendMessage failed: {Error}", ex.Message);
            }

            if (voiceClient is not null)
                await SpeakAsync(thought, voiceClient);
        }

        private ValueTask OnMessageCreate(Message message)
        {
            if (message.Author.IsBot) return default;
            if (exclusiveToChannel && message.ChannelId != defaultChannel) return default;

            recentChannelId    = message.ChannelId;
            VoiceClient? audio = voiceClient;

            _ = Task.Run(async () =>
            {
                List<string> imageUrls = [];
                foreach (Attachment attachment in message.Attachments)
                {
                    if (attachment.ContentType?.StartsWith("image/") == true)
                        imageUrls.Add(attachment.Url);
                }

                MessageContainer? response = await bot.OnMessageCreated(
                    message.Author.Username,
                    FormatMessage(message),
                    imageUrls
                );

                if (response is null) return;

                await client.Rest.SendMessageAsync(message.ChannelId, new() { Content = response.GetContent() });

                if (audio is not null)
                    await SpeakAsync(response.GetContent(), audio);
            });

            return default;
        }

        private async Task SpeakAsync(string text, VoiceClient vc)
        {
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

        private static string FormatMessage(Message message)
        {
            string content = ResolveMentions(message);

            if (message.ReferencedMessage is not null)
                content += $" in response to {message.ReferencedMessage.Author.Username}: {message.ReferencedMessage.Content}";

            return content;
        }

        private static string ResolveMentions(Message message)
        {
            string content = message.Content;
            foreach (User user in message.MentionedUsers)
                content = content.Replace($"<@{user.Id}>", $"@{user.Username}")
                                 .Replace($"<@!{user.Id}>", $"@{user.Username}");
            return content;
        }
    }
}
