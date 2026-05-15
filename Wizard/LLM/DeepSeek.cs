using Wizard.Utility;

namespace Wizard.LLM
{
    public sealed class DeepSeek(string model) : OpenAIClientBased(
        model,
        "https://api.deepseek.com/v1",
        "DEEPSEEK_API_KEY"
    ) {}
}
