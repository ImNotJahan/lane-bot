using Wizard.Utility;

namespace Wizard.LLM
{
    public sealed class OpenRouter(string model) : OpenAIClientBased(
        model,
        "https://openrouter.ai/api/v1",
        "OPENROUTER_API_KEY"
    ) {}
}
