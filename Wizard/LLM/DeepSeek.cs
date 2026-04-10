using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using Wizard.Utility;

namespace Wizard.LLM
{
    public sealed class DeepSeek : ILLM
    {
        const string Model     = "deepseek-chat";
        const int    MaxTokens = 1024;

        public event ILLM.TokenUsageHandler? TokenUsage;

        private readonly ChatClient client;

        public DeepSeek()
        {
            OpenAIClient openAI = new(
                new ApiKeyCredential(DotNetEnv.Env.GetString("DEEPSEEK_API_KEY")),
                new OpenAIClientOptions { Endpoint = new Uri("https://api.deepseek.com/v1") }
            );

            client = openAI.GetChatClient(Model);
        }

        public async Task<MessageContainer> Prompt(
            List<MessageContainer> context,
            string                 systemPrompt,
            string                 cachedDynamicPrompt = "",
            string                 dynamicPrompt       = "",
            List<string>?          stopSequences       = null
        )
        {
            string system = systemPrompt;
            if (cachedDynamicPrompt != "") system += "\n\n" + cachedDynamicPrompt;
            if (dynamicPrompt       != "") system += "\n\n" + dynamicPrompt;

            Logger.LogDebug("Prompting DeepSeek with prompt:" + system);

            List<ChatMessage> messages = [new SystemChatMessage(system)];

            foreach (MessageContainer message in context) messages.Add(message.OpenAI());

            ChatCompletionOptions options = new()
            {
                MaxOutputTokenCount = MaxTokens,
                Temperature         = 1,
            };

            if (stopSequences is not null) foreach (string s in stopSequences) options.StopSequences.Add(s);

            ChatCompletion response = await client.CompleteChatAsync(messages, options);

            string formattedResponse = response.Content[0].Text;

            Logger.LogDebug(
                "Token usage — input: {0}, output: {1}, cached: {2}",
                response.Usage.InputTokenCount,
                response.Usage.OutputTokenCount,
                response.Usage.InputTokenDetails.CachedTokenCount
            );

            TokenUsage?.Invoke(
                response.Usage.InputTokenCount,
                response.Usage.OutputTokenCount,
                response.Usage.InputTokenDetails.CachedTokenCount
            );

            return new(formattedResponse, Author.Bot, time: DateTime.Now);
        }
    }
}
