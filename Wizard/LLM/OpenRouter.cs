using Wizard.Utility;

namespace Wizard.LLM
{
    public sealed class OpenRouter : OpenAIClientBased
    {
        public OpenRouter() : base(
            Settings.instance?.Model ?? "openai/gpt-oss-20b",
            "https://openrouter.ai/api/v1",
            "OPENROUTER_API_KEY"
        ) {}
    }
}
