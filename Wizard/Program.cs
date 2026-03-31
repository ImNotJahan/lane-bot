using Microsoft.Extensions.Configuration;
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
            IConfigurationRoot config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .Build();
            
            Settings? settings = config.GetRequiredSection("Settings").Get<Settings>();

            Settings.instance = settings;

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

            List<IMemoryHandler> memoryHandlers = [];

            if(settings is null)
            {
                memoryHandlers = [
                    new Summary(10, llm),
                    new RAG(10, 10),
                    new SlidingWindow(10)
                ];
            }
            else
            {
                foreach(HandlerSettings handler in settings.MemoryHandlers)
                {
                    switch (handler.Handler)
                    {
                        case "RAG":
                            memoryHandlers.Add(new RAG(
                                (ulong) handler.Args["SelectLimit"],
                                        handler.Args["WriteInterval"]
                            ));
                            break;
                        
                        case "Summary":
                            memoryHandlers.Add(new Summary(
                                handler.Args["UpdateInterval"], 
                                llm
                            ));
                            break;
                        
                        case "SlidingWindow":
                            memoryHandlers.Add(new SlidingWindow(
                                handler.Args["MaxMessages"]
                            ));
                            break;
                        
                        default:
                            throw new Exception($"Invalid handler type {handler.Handler}");
                    }
                }
            }

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
                ulong   defaultChannel = settings?.DefaultDiscordChannel ?? 0;
                Discord discord        = new(llm, memoryHandlers, defaultChannel);

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