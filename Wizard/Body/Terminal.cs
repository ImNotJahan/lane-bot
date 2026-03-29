using Newtonsoft.Json.Linq;
using Wizard.Head;
using Wizard.LLM;
using Wizard.Memory;
using Wizard.Utility;

namespace Wizard.Body
{
    public sealed class Terminal(ILLM llm, List<IMemoryHandler> memoryHandlers)
    {
        readonly Bot bot = new(llm, memoryHandlers);

        public async Task BeginLoop()
        {
            while (true)
            {
                Console.Write("> ");
                
                string? input = Console.ReadLine();

                if(input is null) continue;

                MessageContainer? response = await bot.OnMessageCreated("User", input, []);

                if(response is null) continue;

                Console.WriteLine(response.GetContent());
            }
        }
    }
}