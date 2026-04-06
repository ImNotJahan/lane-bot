namespace Wizard.LLM
{
    public interface ILLM
    {
        public delegate void TokenUsageHandler(int input, int output, int cached);
        public event         TokenUsageHandler? TokenUsage;

        public Task<MessageContainer> Prompt(
            List<MessageContainer> context, 
            string                 systemPrompt, 
            string                 cachedDynamicPrompt = "",
            string                 dynamicPrompt       = ""
        );
    }
}