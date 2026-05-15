using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Terminal.Gui.App;
using Wizard.Head;
using Wizard.LLM;
using Wizard.Memory;
using Wizard.UI;
using Wizard.Utility;

namespace Wizard
{
    internal class Program
    {
        public const string DEFAULT_MODEL = "claude-haiku-4-5-20251001";

        static async Task Main(string[] args)
        {
            IConfigurationRoot config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .Build();
            
            Settings? settings = config.GetRequiredSection("Settings").Get<Settings>();

            Settings.instance = settings;

            DotNetEnv.Env.TraversePath().Load();

            using IApplication app = Application.Create();
            
            app.Init();
            
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

            static ILLM ParseLLM(LLMSettings? llmSettings) => llmSettings?.LLM switch
            {
                "Claude"     => new Claude    (llmSettings.Model),
                "DeepSeek"   => new DeepSeek  (llmSettings.Model),
                "OpenRouter" => new OpenRouter(llmSettings.Model),
                null         => new Claude    (DEFAULT_MODEL),
                _            => throw new Exception($"Invalid LLM {llmSettings.LLM}")
            };
            

            Dictionary<string, IMemoryHandler> memoryHandlers = [];

            ILLM respondLLM   = ParseLLM(settings?.LLMs.Respond);
            ILLM routingLLM   = ParseLLM(settings?.LLMs.Routing);
            ILLM monologueLLM = ParseLLM(settings?.LLMs.Monologue);
            
            ILLM? summarizeLLM = null;

            Bot bot = new(
                respondLLM,
                routingLLM,
                monologueLLM,
                memoryHandlers, 
                Settings.instance?.RespondToThought ?? 60
            );

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
                            summarizeLLM = ParseLLM(settings?.LLMs.Summarize);

                            memoryHandlers.Add(id, new Summary(
                                handler.Args["UpdateInterval"], 
                                summarizeLLM
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
                    IMemoryHandler? handler = memoryHandlers.GetValueOrDefault(pair.Key);

                    if(handler is null)    continue;
                    if(pair.Value is null) continue;

                    handler.Deserialize(pair.Value);
                }

                bot.LoadBookPositions(data);
            }

            if (settings is not null && settings.Face is FaceSettings face && face.Enabled)
            {
                Wizard.Body.EmoticonServer emoticonServer = new(face.Port, face.DefaultFace);

                emoticonServer.Start();
                
                bot.OnEmoticonChanged += emoticonServer.SetEmoticon;
            }

            if(selectedBody == Body.Discord)
            {
                ulong                defaultChannel = settings?.DefaultDiscordChannel ?? 0;
                Wizard.Body.Discord  discord        = new(bot, defaultChannel);

                _ = discord.ConnectAsync();

                Console.WriteLine("Connected");
            } else if(selectedBody == Body.Terminal)
            {
                Wizard.Body.Terminal terminal = new(bot);

                await terminal.BeginLoop();
                
                return;
            }

            app.Run(new DashboardView(
                bot, 
                summarizeLLM is null ? [respondLLM, routingLLM, monologueLLM] 
                                     : [respondLLM, routingLLM, monologueLLM, summarizeLLM]
            ));
        }

        enum Body
        {
            Discord, Terminal
        }
    }
}