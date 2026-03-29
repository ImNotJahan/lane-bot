using Newtonsoft.Json.Linq;
using Wizard.LLM;
using Wizard.Utility;

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

        public async override Task<List<MessageContainer>> RecallMemory(MessageContainer? message) => [summary];

        private async Task Resummarize()
        {
            List<MessageContainer> snapshot = [..memory];
            memory.Clear();

            string conversation = string.Join("\n", snapshot.Select(m => m.GetContent()));

            summary = await llm.Prompt(
                snapshot,
                string.Format(
                    Prompts.GetPrompt("Summarize"),
                    summary.GetContent(),
                    conversation
                )
            );

            Logger.LogInformation("Summarizing conversation as: " + summary.GetContent());
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