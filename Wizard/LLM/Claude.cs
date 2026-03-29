using Anthropic;
using Anthropic.Models.Messages;

namespace Wizard.LLM
{
    public sealed class Claude : ILLM
    {
        const string Model     = "claude-haiku-4-5-20251001";
        const int    MaxTokens = 1024;
        
        readonly AnthropicClient client;

        public Claude()
        {
            client = new()
            {
                ApiKey = DotNetEnv.Env.GetString("ANTHROPIC_API_KEY")
            };
        }

        private static MessageCreateParams CreateParams(List<MessageContainer> context, string prompt)
        {
            List<MessageParam> messages = [];

            foreach(MessageContainer message in context) messages.Add(message.Anthropic());

            return new()
            {
                MaxTokens   = MaxTokens,
                Model       = Model,
                Temperature = 1,
                System      = prompt,
                Messages    = messages
            };
        }
    
        public async Task<MessageContainer> Prompt(List<MessageContainer> context, string systemPrompt)
        {
            Message response = await client.Messages.Create(CreateParams(context, systemPrompt));

            string formattedResponse = "";

            foreach(ContentBlock block in response.Content)
            {
                if(block.TryPickText(out TextBlock? text))
                {
                    formattedResponse += text.Text;
                }
            }

            return new(formattedResponse, Author.Bot);
        }
    }
}