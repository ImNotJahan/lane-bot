using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Wizard.Body;
using Wizard.Head;
using Wizard.Head.Mouths;
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

            DotNetEnv.Env.TraversePath().Load();
            
            Body selectedBody;

            if(args.Length < 1)
            {
                if(settings is null) selectedBody = Body.Terminal;
                else
                {
                    selectedBody = settings.Body switch
                    {
                        "Discord"  => Body.Discord,
                        "Terminal" => Body.Terminal,
                        _          => throw new Exception($"Unknown body type {settings.Body}")
                    };
                }
            }
            else
            {
                selectedBody = args[0] switch
                {
                    "terminal" => Body.Terminal,
                    "discord"  => Body.Discord,
                    _          => throw new Exception($"Unknown body type {args[0]}")
                };
            }

            ILLM llm = new Claude();

            Dictionary<string, IMemoryHandler> memoryHandlers = [];

            Bot bot = new(llm, memoryHandlers, Settings.instance is null ? 60 : Settings.instance.RespondToThought);

            if(settings is null)
            {
                Logger.LogWarning("No settings file found");

                memoryHandlers["SlidingWindow"] = new SlidingWindow(10, false);
            }
            else
            {
                foreach(HandlerSettings handler in settings.MemoryHandlers)
                {
                    string id = handler.ID;
                    switch (handler.Handler)
                    {
                        case "RAG":
                            memoryHandlers.Add(id, new RAG(
                                (ulong) handler.Args["SelectLimit"],
                                        handler.Args["WriteInterval"]
                            ));
                            break;
                        
                        case "Summary":
                            memoryHandlers.Add(id, new Summary(
                                handler.Args["UpdateInterval"], 
                                llm
                            ));
                            break;
                        
                        case "SlidingWindow":
                            memoryHandlers.Add(id, new SlidingWindow(
                                handler.Args["MaxMessages"],
                                handler.Args["ForThoughts"] == 1
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
                    IMemoryHandler? handler = memoryHandlers[pair.Key];

                    if(handler is null)    continue;
                    if(pair.Value is null) continue;

                    handler.Deserialize(pair.Value);
                }
            }

            if(selectedBody == Body.Discord)
            {
                ulong                defaultChannel = settings?.DefaultDiscordChannel ?? 0;
                Wizard.Body.Discord  discord        = new(bot, defaultChannel);

                await discord.ConnectAsync();

                Console.WriteLine("Connected");

                await Task.Delay(-1);
            } else if(selectedBody == Body.Terminal)
            {
                Terminal terminal = new(bot);

                await terminal.BeginLoop();
            }
        }

        enum Body
        {
            Discord, Terminal
        }
    }
}