using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Wizard.Head;
using Wizard.LLM;
using Wizard.Memory;

namespace Wizard.Body
{
    public sealed class Discord
    {
        readonly DiscordClient client;
        readonly Bot           bot;

        DiscordChannel? recentChannel = null;

        public Discord(ILLM llm, List<IMemoryHandler> memoryHandlers)
        {
            client = new DiscordClient(new DiscordConfiguration()
            {
                Token     = DotNetEnv.Env.GetString("DISCORD_API_KEY"),
                TokenType = TokenType.Bot,
                Intents   = DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents
            });

            bot = new(llm, memoryHandlers);

            bot.OnHadGoodThought += OnHadGoodThought;

            client.Ready          += OnReady;
            client.MessageCreated += OnMessageCreated;
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
            else await client.SendMessageAsync(await client.GetChannelAsync(1458989331518853253), thought);
        }

        private async Task OnMessageCreated(DiscordClient client, MessageCreateEventArgs args)
        {
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

            MessageContainer? response = await bot.OnMessageCreated(args.Author.Username, ResolveMentions(args), imageUrls);

            if(response is null) return;

            await client.SendMessageAsync(args.Channel, response.GetContent());
        }

        public async Task ConnectAsync() => await client.ConnectAsync();
    }
}