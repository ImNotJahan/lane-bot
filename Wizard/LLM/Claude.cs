using Anthropic;
using Anthropic.Models.Messages;
using Wizard.Utility;

namespace Wizard.LLM
{
    public sealed class Claude : ILLM
    {
        const string Model     = "claude-haiku-4-5-20251001";
        const int    MaxTokens = 1024;
        
        readonly AnthropicClient client;

        public event ILLM.TokenUsageHandler? TokenUsage;

        public Claude()
        {
            client = new()
            {
                ApiKey = DotNetEnv.Env.GetString("ANTHROPIC_API_KEY")
            };
        }

        private static MessageCreateParams CreateParams(
            List<MessageContainer> context, 
            string                 prompt, 
            string                 cachedDynamicPrompt, 
            string                 dynamicPrompt,
            List<string>           stopSequences
        )
        {
            List<MessageParam>    messages      = [];
            List<TextBlockParam>  systemBlocks  = [
                new TextBlockParam
                {
                    Text         = prompt,
                    CacheControl = new CacheControlEphemeral()
                }
            ];

            if(cachedDynamicPrompt != "")
            {
                systemBlocks.Add(new TextBlockParam
                {
                    Text         = cachedDynamicPrompt,
                    CacheControl = new CacheControlEphemeral()
                });
            }

            if(dynamicPrompt != "")
            {
                systemBlocks.Add(new TextBlockParam { Text = dynamicPrompt });
            }

            foreach(MessageContainer message in context) messages.Add(message.Anthropic());

            return new()
            {
                MaxTokens     = MaxTokens,
                Model         = Model,
                Temperature   = 1,
                System        = systemBlocks,
                Messages      = messages,
                StopSequences = stopSequences
            };
        }

        public async Task<MessageContainer> Prompt(
            List<MessageContainer> context,
            string                 systemPrompt,
            string                 cachedDynamicPrompt = "",
            string                 dynamicPrompt       = "",
            List<string>?          stopSequences       = null
        )
        {
            Logger.LogTrace("Prompting Claude with cached dynamic prompt:" + cachedDynamicPrompt);
            Logger.LogTrace("Prompting Claude with dynamic prompt:" + dynamicPrompt);

            Message response = await client.Messages.Create(CreateParams(
                context, systemPrompt, cachedDynamicPrompt, dynamicPrompt, stopSequences ?? []
            ));

            string formattedResponse = "";

            foreach(ContentBlock block in response.Content)
            {
                if(block.TryPickText(out TextBlock? text))
                {
                    formattedResponse += text.Text;
                }
            }

            Logger.LogTrace(
                "Token usage — input: {0}, output: {1}, cache write: {2}, cache read: {3}",
                response.Usage.InputTokens,
                response.Usage.OutputTokens,
                response.Usage.CacheCreationInputTokens ?? 0,
                response.Usage.CacheReadInputTokens     ?? 0
            );

            TokenUsage?.Invoke(
                (int) response.Usage.InputTokens,
                (int) response.Usage.OutputTokens,
                (int) (response.Usage.CacheReadInputTokens ?? 0)
            );

            return new(formattedResponse, Author.Bot, time: DateTime.UtcNow);
        }
    }
}