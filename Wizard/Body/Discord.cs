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

        public Discord(ILLM llm, List<IMemoryHandler> memoryHandlers)
        {
            bot = new(llm, memoryHandlers);

            client = new DiscordClient(new DiscordConfiguration()
            {
                Token     = DotNetEnv.Env.GetString("DISCORD_API_KEY"),
                TokenType = TokenType.Bot,
                Intents   = DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents
            });

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


        private async Task OnMessageCreated(DiscordClient client, MessageCreateEventArgs args)
        {
            if(args.Author.IsBot) return;

            MessageContainer? response = await bot.OnMessageCreated(args.Author.Username, ResolveMentions(args));

            if(response is null) return;

            await client.SendMessageAsync(args.Channel, response.GetContent());
        }

        public async Task ConnectAsync() => await client.ConnectAsync();
    }
}