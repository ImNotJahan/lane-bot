namespace Wizard.LLM
{
    public interface ILLM
    {
        public Task<MessageContainer> Prompt(
            List<MessageContainer> context, 
            string                 systemPrompt, 
            string                 cachedDynamicPrompt = "",
            string                 dynamicPrompt       = ""
        );
    }
}