using Newtonsoft.Json.Linq;
using Wizard.LLM;
using Wizard.Memory;
using Wizard.Utility;

namespace Wizard.Head
{
    public sealed class Bot(ILLM llm, List<IMemoryHandler> memoryHandlers)
    {
        readonly ILLM llm = llm;

        readonly List<IMemoryHandler> memoryHandlers = memoryHandlers;

        private async Task<List<MessageContainer>> AssembleContext(MessageContainer message)
        {
            List<MessageContainer> context = [];

            foreach(IMemoryHandler handler in memoryHandlers) context.AddRange(await handler.RecallMemory(message));

            context.Add(message);

            return context;
        }

        private async Task RememberMessage(MessageContainer message)
        {
            Logger.LogDebug("Remembering message {0}", message.GetContent());

            foreach(IMemoryHandler handler in memoryHandlers) await handler.RememberMessage(message);
        }

        public async Task<MessageContainer?> OnMessageCreated(
            string       author,
            string       message,
            List<string> imageUrls
        )
        {
            Logger.LogInformation("Recieved message {0}", message);

            foreach(string url in imageUrls) await RememberMessage(new(url, Author.User, MessageType.Image));

            MessageContainer       formattedMessage = new($"{author} says: {message}");
            List<MessageContainer> context          = await AssembleContext(formattedMessage);
            float                  enthusiasm       = await Enthusiasm(context);

            if(enthusiasm < 0.1f)
            {
                Logger.LogInformation($"Decided not to respond to message (enthusiasm {enthusiasm})");

                await RememberMessage(formattedMessage);
                return null;
            }

            Logger.LogInformation($"Decided to respond to message (enthusiasm {enthusiasm})");

            MessageContainer response = await RespondToMessage(context, enthusiasm);

            Logger.LogInformation("Will respond with {0}", response.GetContent());

            await RememberMessage(formattedMessage);
            await RememberMessage(response);

            WriteData();
            
            return response;
        }

        public void WriteData()
        {
            JObject data = [];

            foreach(IMemoryHandler handler in memoryHandlers)
            {
                data[handler.GetType().ToString()] = handler.Serialize();
            }

            JSONWriter.WriteData(data);
        }

        private async Task<MessageContainer> RespondToMessage(List<MessageContainer> context, float enthusiasm)
        {
            string enthusiasmContext = enthusiasm switch
            {
                >= 0.8f => "high",
                >= 0.5f => "neutral",
                _       => "low"
            };
            
            return await llm.Prompt(
                context,
                string.Format(Prompts.GetPrompt("Respond"), enthusiasmContext)
            );
        }

        private static string ContextToString(List<MessageContainer> context)
        {
            return string.Join("\n", context.Select(m => m.GetContent()));
        }

        private async Task<float> Enthusiasm(List<MessageContainer> context)
        {
            string formattedContext = ContextToString(context);
            string prompt           = string.Format(Prompts.GetPrompt("Routing"), formattedContext);

            Logger.LogDebug("Gauging enthusiasm with prompt: " + formattedContext);

            string result = (await llm.Prompt([context[^1]], prompt)).GetContent();

            if(!float.TryParse(result, out float enthusiasm)) throw new InvalidRouterValue(result);

            return enthusiasm;
        }

        private class InvalidRouterValue(string value) : Exception($"Router responded incorrectly: {value}") {}
    }
}