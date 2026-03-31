using Newtonsoft.Json.Linq;
using Wizard.LLM;

namespace Wizard.Memory
{
    public interface IMemoryHandler
    {
        public Task RememberMessage(MessageContainer message);

        public Task<List<MessageContainer>> RecallMemory(MessageContainer? message);

        /// <summary>
        /// Some memory handlers, like summary and window memory, recount the current conversation.
        /// Others, like RAG, retrieve memories which could be from far past. It's important that
        /// the bot can differentiate the two, so this method returns if this memory handler returns
        /// recent memories or not.
        /// </summary>
        /// <returns>If this memory handler returns recent memories</returns>
        public bool IsRecent();
        
        public JToken Serialize();
        public void   Deserialize(JToken data);
    }
}