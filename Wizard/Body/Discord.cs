using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Wizard.Head;
using Wizard.LLM;
using Wizard.Memory;
using Wizard.Utility;

namespace Wizard.Body
{
    public sealed class Discord
    {
        readonly DiscordClient client;
        readonly Bot           bot;

        DiscordChannel? recentChannel = null;

        readonly ulong defaultChannel;
        readonly bool  exclusiveToChannel;

        public Discord(ILLM llm, List<IMemoryHandler> memoryHandlers, ulong defaultChannel)
        {
            client = new DiscordClient(new DiscordConfiguration()
            {
                Token     = DotNetEnv.Env.GetString("DISCORD_API_KEY"),
                TokenType = TokenType.Bot,
                Intents   = DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents
            });

            this.defaultChannel = defaultChannel;

            exclusiveToChannel = Settings.instance is not null && Settings.instance.ExclusiveToChannel == true;

            bot = new(llm, memoryHandlers, Settings.instance is null ? 60 : Settings.instance.RespondToThought);

            bot.OnHadGoodThought += OnHadGoodThought;

            client.Ready          += OnReady;
            client.MessageCreated += OnMessageCreated;
        }

        private static string FormatMessage(MessageCreateEventArgs args)
        {
            string formatted = ResolveMentions(args);

            if(args.Message.ReferencedMessage is not null)
            {
                // is replying to a message
                DiscordMessage replied = args.Message.ReferencedMessage;

                formatted += $" in response to {replied.Author.Username}: {replied.Content}";
            }

            return formatted;
        }

        private static string ResolveMentions(MessageCreateEventArgs args)
        {
            string content = args.Message.Content;

            foreach (DiscordUser user in args.MentionedUsers)
                content = content.Replace($"<@{user.Id}>", $"@{user.Username}")
                                 .Replace($"<@!{user.Id}>", $"@{user.Username}");

            return content;
        }

        private async Task OnReady(DiscordClient client, ReadyEventArgs args)
        {
            bot.StartMonologue();            
        }

        private async void OnHadGoodThought(string thought)
        {
            if(recentChannel is not null) await client.SendMessageAsync(recentChannel, thought);
            else await client.SendMessageAsync(await client.GetChannelAsync(defaultChannel), thought);
        }

        private async Task OnMessageCreated(DiscordClient client, MessageCreateEventArgs args)
        {
            if(exclusiveToChannel && args.Channel.Id != defaultChannel) return;

            recentChannel = args.Channel;

            if(args.Author.IsBot) return;

            List<string> imageUrls = [];

            foreach (DiscordAttachment attachment in args.Message.Attachments)
            {
                if (attachment.MediaType?.StartsWith("image/") == true)
                {
                    imageUrls.Add(attachment.Url);
                }
            }

            MessageContainer? response = await bot.OnMessageCreated(args.Author.Username, FormatMessage(args), imageUrls);

            if(response is null) return;

            await client.SendMessageAsync(args.Channel, response.GetContent());
        }

        public async Task ConnectAsync() => await client.ConnectAsync();
    }
}