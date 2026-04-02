using Wizard.Head;
using Wizard.LLM;

namespace Wizard.Body
{
    public sealed class Terminal(Bot bot)
    {
        readonly Bot bot = bot;

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