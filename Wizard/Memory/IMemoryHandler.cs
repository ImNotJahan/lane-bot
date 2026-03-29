using Newtonsoft.Json.Linq;
using Wizard.LLM;

namespace Wizard.Memory
{
    public interface IMemoryHandler
    {
        public Task RememberMessage(MessageContainer message);

        public Task<List<MessageContainer>> RecallMemory(MessageContainer? message);
        
        public JToken Serialize();
        public void   Deserialize(JToken data);
    }
}