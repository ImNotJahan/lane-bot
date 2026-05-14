using Wizard.Utility;

namespace Wizard.LLM
{
    public sealed class DeepSeek : OpenAIClientBased
    {
        public DeepSeek() : base(
            Settings.instance?.Model ?? "deepseek-chat",
            "https://api.deepseek.com/v1",
            "DEEPSEEK_API_KEY"
        ) {}
    }
}
