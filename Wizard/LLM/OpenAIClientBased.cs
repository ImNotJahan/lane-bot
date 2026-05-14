using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using Wizard.Utility;

namespace Wizard.LLM
{
    public abstract class OpenAIClientBased : ILLM
    {
        const int MaxTokens = 1024;

        public event ILLM.TokenUsageHandler? TokenUsage;

        private readonly ChatClient client;
        private readonly string     model;

        public OpenAIClientBased(string model, string endpoint, string api_key_env)
        {
            OpenAIClient openAI = new(
                new ApiKeyCredential(DotNetEnv.Env.GetString(api_key_env)),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) }
            );

            client = openAI.GetChatClient(model);

            this.model = model;
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

            Logger.LogTrace($"Prompting {model} with prompt: {system}");

            List<ChatMessage> messages = [new SystemChatMessage(system)];

            foreach (MessageContainer message in context) messages.Add(message.OpenAI());

            // Serialize messages via the SDK so we don't have to replicate its format
            var serializedMessages = messages
                .Select(m => JsonSerializer.Deserialize<JsonElement>(
                    ModelReaderWriter.Write(m, ModelReaderWriterOptions.Json).ToString()))
                .ToList();

            var requestObj = new Dictionary<string, object>
            {
                ["model"]       = model,
                ["messages"]    = serializedMessages,
                ["max_tokens"]  = MaxTokens,
                ["temperature"] = 1,
            };

            if (stopSequences is { Count: > 0 })
                requestObj["stop"] = stopSequences;

            // Use protocol-level method to avoid SDK throwing on unknown finish_reason values
            BinaryContent binaryContent = BinaryContent.Create(BinaryData.FromObjectAsJson(requestObj));
            ClientResult  rawResult     = await client.CompleteChatAsync(binaryContent);

            using JsonDocument doc  = JsonDocument.Parse(rawResult.GetRawResponse().Content);
            JsonElement        root = doc.RootElement;

            if (!root.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0)
            {
                Logger.LogError("Missing content in response");
                return new("", Author.Bot, time: DateTime.UtcNow);
            }

            string formattedResponse = choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            JsonElement usage        = root.GetProperty("usage");
            int         inputTokens  = usage.GetProperty("prompt_tokens").GetInt32();
            int         outputTokens = usage.GetProperty("completion_tokens").GetInt32();
            int         cachedTokens = usage.TryGetProperty("prompt_tokens_details", out JsonElement details)
                                       && details.TryGetProperty("cached_tokens", out JsonElement ct)
                                       ? ct.GetInt32() : 0;

            Logger.LogTrace(
                "Token usage — input: {0}, output: {1}, cached: {2}",
                inputTokens,
                outputTokens,
                cachedTokens
            );

            TokenUsage?.Invoke(inputTokens, outputTokens, cachedTokens);

            return new(formattedResponse, Author.Bot, time: DateTime.UtcNow);
        }
    }
}
