using Newtonsoft.Json.Linq;
using Wizard.LLM;

namespace Wizard.Memory
{
    public class SlidingWindow(int maxMessages) : IMemoryHandler
    {
        protected readonly List<MessageContainer> memory = [];
        
        protected readonly int maxMessages = maxMessages;

        public virtual List<MessageContainer> RecallMemory(MessageContainer message) => memory;

        public virtual async Task RememberMessage(MessageContainer message)
        {
            memory.Add(message);

            if(memory.Count > maxMessages) memory.RemoveAt(0);
        }

        public virtual JToken Serialize()
        {
            JArray serializedMemory = [];

            foreach(MessageContainer message in memory) serializedMemory.Add(message.Serialize());

            return serializedMemory;
        }

        public virtual void Deserialize(JToken data)
        {
            memory.Clear();

            JArray? array = data as JArray ?? throw new Exception("Data is in invalid format");
            
            foreach (JToken message in array) memory.Add(new(message));

            while (memory.Count > maxMessages) memory.RemoveAt(0);
        }
    }
}