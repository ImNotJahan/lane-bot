using Newtonsoft.Json.Linq;
using Wizard.Body;
using Wizard.LLM;
using Wizard.Memory;
using Wizard.Utility;

namespace Wizard
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Body selectedBody;

            if(args.Length < 1) selectedBody = Body.Terminal;
            else
            {
                selectedBody = args[0] switch
                {
                    "terminal" => Body.Terminal,
                    "discord"  => Body.Discord,
                    _          => throw new Exception($"Unknown body type {args[0]}")
                };
            }

            DotNetEnv.Env.TraversePath().Load();

            ILLM llm = new Claude();

            List<IMemoryHandler> memoryHandlers = [
                new Summary(10, llm),
                new RAG(10),
                new SlidingWindow(10)
            ];

            if (JSONWriter.HasData())
            {
                JObject? data =  JSONWriter.ReadData() as JObject 
                              ?? throw new Exception("Data is null");
                
                foreach (KeyValuePair<string, JToken?> pair in data)
                {
                    IMemoryHandler? handler = memoryHandlers.Find((x) => x.GetType().ToString() == pair.Key);

                    if(handler is null)    continue;
                    if(pair.Value is null) continue;

                    handler.Deserialize(pair.Value);
                }
            }

            if(selectedBody == Body.Discord)
            {
                Discord discord = new(llm, memoryHandlers);

                await discord.ConnectAsync();

                Console.WriteLine("Connected");

                await Task.Delay(-1);
            } else if(selectedBody == Body.Terminal)
            {
                Terminal terminal = new(llm, memoryHandlers);

                await terminal.BeginLoop();
            }
        }

        enum Body
        {
            Discord, Terminal
        }
    }
}