using Newtonsoft.Json.Linq;
using Wizard.LLM;

namespace Wizard.Memory
{
    public sealed class Summary(int updateInterval, ILLM llm) : SlidingWindow(updateInterval)
    {
        private MessageContainer summary = new("", Author.Bot);

        readonly ILLM llm = llm;

        public override async Task RememberMessage(MessageContainer message)
        {
            await base.RememberMessage(message);

            if(memory.Count == maxMessages) await Resummarize();
        }

        public override List<MessageContainer> RecallMemory(MessageContainer message) => [summary];

        private async Task Resummarize()
        {
            string conversation = "";

            foreach(MessageContainer message in memory) conversation += message.GetContent() + "\n";

            summary = await llm.Prompt(
                memory,
                string.Format(
                    Prompts.GetPrompt("Summarize"),
                    summary.GetContent(),
                    conversation
                )
            );

            memory.Clear();
        }

        public override JToken Serialize()
        {
            return new JObject()
            {
                ["summary"] = summary.GetContent(),
                ["window"]  = base.Serialize()
            };
        }

        public override void Deserialize(JToken data)
        {
            base.Deserialize(data["window"] ?? throw new Exception("Data in invalid format"));
            string? content = (string?) data["summary"] ?? throw new Exception("Data in invalid format");

            summary = new(content, Author.Bot);
        }
    }
}