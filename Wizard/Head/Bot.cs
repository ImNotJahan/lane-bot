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

        public async Task<MessageContainer?> OnMessageCreated(string author, string message)
        {
            Logger.LogInformation("Recieved message {0}", message);

            MessageContainer formattedMessage = new($"{author} says: {message}");

            if(!await llm.WantsToRespond(await AssembleContext(formattedMessage)))
            {
                Logger.LogInformation("Decided not to respond to message");

                await RememberMessage(formattedMessage);
                return null;
            }

            Logger.LogInformation("Decided to respond to message");

            MessageContainer response = await llm.RespondToMessage(await AssembleContext(formattedMessage));

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
    }
}