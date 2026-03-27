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

        private List<MessageContainer> AssembleContext(MessageContainer message)
        {
            List<MessageContainer> context = [];

            foreach(IMemoryHandler handler in memoryHandlers) context.AddRange(handler.RecallMemory(message));

            context.Add(message);

            return context;
        }

        private async Task RememberMessage(MessageContainer message)
        {
            foreach(IMemoryHandler handler in memoryHandlers) await handler.RememberMessage(message);
        }

        public async Task<MessageContainer?> OnMessageCreated(string author, string message)
        {
            MessageContainer formattedMessage = new($"{author} says: {message}");

            if(!await llm.WantsToRespond(AssembleContext(formattedMessage)))
            {
                await RememberMessage(formattedMessage);
                return null;
            }

            MessageContainer response = await llm.RespondToMessage(AssembleContext(formattedMessage));

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