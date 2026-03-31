using Newtonsoft.Json.Linq;
using Wizard.LLM;
using Wizard.Memory;
using Wizard.Utility;

namespace Wizard.Head
{
    public sealed class Bot(ILLM llm, List<IMemoryHandler> memoryHandlers)
    {
        public delegate void     OnEvent(string text);
        public event    OnEvent? OnHadGoodThought;

        readonly ILLM llm = llm;

        readonly List<IMemoryHandler> memoryHandlers = memoryHandlers;

        int timeUntilThought = 10;

        // any time the bot recieves a message, there is a fixed interval between
        // that message and the next time it has a thought, as set below
        const int TimeBetweenMessageAndThought = 60;

        bool recievedMessageRecently = false;

        private async Task<List<MessageContainer>> AssembleContext(MessageContainer? message)
        {
            List<MessageContainer> context = [];

            foreach(IMemoryHandler handler in memoryHandlers) context.AddRange(await handler.RecallMemory(message));

            if(message is not null) context.Add(message);

            return context;
        }

        private async Task RememberMessage(MessageContainer message)
        {
            Logger.LogDebug("Remembering message {0}", message.GetContent());

            foreach(IMemoryHandler handler in memoryHandlers) await handler.RememberMessage(message);
        }

        public async Task<MessageContainer?> OnMessageCreated(
            string       author,
            string       message,
            List<string> imageUrls
        )
        {
            recievedMessageRecently = true;

            Logger.LogInformation("Recieved message {0}", message);

            foreach(string url in imageUrls) await RememberMessage(new(url, Author.User, MessageType.Image));

            MessageContainer       formattedMessage = new($"{author} says: {message}");
            List<MessageContainer> context          = await AssembleContext(formattedMessage);
            float                  enthusiasm       = await Enthusiasm(context);

            if(enthusiasm <= 0.2f)
            {
                Logger.LogInformation($"Decided not to respond to message (enthusiasm {enthusiasm})");

                await RememberMessage(formattedMessage);
                return null;
            }

            Logger.LogInformation($"Decided to respond to message (enthusiasm {enthusiasm})");

            MessageContainer response = await RespondToMessage(context, enthusiasm);

            Logger.LogInformation("Will respond with {0}", response.GetContent());

            await RememberMessage(formattedMessage);
            await RememberMessage(response);

            WriteData();
            
            return response;
        }

        public void WriteData()
        {
            JObject data = [];

            foreach(IMemoryHandler handler in memoryHandlers)
            {
                data[handler.GetType().ToString()] = handler.Serialize();
            }

            JSONWriter.WriteData(data);
        }

        private async Task<MessageContainer> RespondToMessage(List<MessageContainer> context, float enthusiasm)
        {
            string enthusiasmContext = enthusiasm switch
            {
                >= 0.8f => "high",
                >= 0.5f => "neutral",
                _       => "low"
            };
            
            return await llm.Prompt(
                context,
                string.Format(Prompts.GetPrompt("Respond"), enthusiasmContext)
            );
        }

        private static string ContextToString(List<MessageContainer> context)
        {
            string formattedContext = "";

            foreach(MessageContainer message in context)
            {
                if(message.GetAuthor() == Author.Bot)
                {
                    if(message.GetMessageType() == MessageType.Text)    formattedContext += "Lane says: ";
                    if(message.GetMessageType() == MessageType.Thought) formattedContext += "Lane thinks: ";
                }
                formattedContext += message.GetContent();
            }

            return string.Join("\n", context.Select(m => m.GetContent()));
        }

        private async Task<float> Enthusiasm(List<MessageContainer> context)
        {
            string formattedContext = ContextToString(context);
            string prompt           = string.Format(Prompts.GetPrompt("Routing"), formattedContext);

            Logger.LogDebug("Gauging enthusiasm with prompt: " + formattedContext);

            string result = (await llm.Prompt([context[^1]], prompt)).GetContent();

            if(!float.TryParse(result, out float enthusiasm)) throw new InvalidRouterValue(result);

            return enthusiasm;
        }

        bool monologueRunning = false;

        public void StartMonologue()
        {
            if(monologueRunning) return;

            monologueRunning = true;

            _ = MonologueHandler();
        }
        
        private async Task MonologueHandler()
        {
            try
            {
                Logger.LogInformation("Starting monologue...");
                
                await Monologue();
            } catch(Exception exception)
            {
                Logger.LogError(exception.ToString());
            } finally
            {
                Logger.LogWarning("Monologue stopped running");

                monologueRunning = false;
            }
        }

        MessageContainer? lastThought = null;

        private async Task Monologue()
        {
            while(true)
            {
                string formattedContext = ContextToString(await AssembleContext(lastThought));

                string prompt = string.Format(
                    Prompts.GetPrompt("Monologue"),
                    formattedContext,
                    DateTime.UtcNow.ToString("yyyy/MM/dd HH:mm:ss")
                );

                MessageContainer response = await llm.Prompt([new(prompt)], "");

                if(response.GetContent() == "") throw new Exception("Response was empty");

                JObject data;

                try
                {
                    string toParse = response.GetContent();

                    // since we tell llm to respond in json, sometimes it will wrap
                    // response in these, which we don't want
                    toParse = toParse.Replace("```json", "").Replace("```", "");

                    data = JObject.Parse(toParse);
                } catch(Exception exception)
                {
                    throw new InvalidMonologue(exception.ToString());
                }

                timeUntilThought = (int?) data["next_thought_in_seconds"]
                                ?? throw new InvalidMonologue("Did not have next_thought_in_seconds property");

                string thought = (string?) data["thought"]
                              ?? throw new InvalidMonologue("Did not have thought property");

                Logger.LogInformation("Thought " + thought);
                Logger.LogInformation($"Will think again in {timeUntilThought} seconds");

                lastThought = new(thought, Author.Bot, MessageType.Thought);

                await RememberMessage(lastThought);

                if((bool?) data["speak"] == true)
                {
                    Logger.LogInformation("Will verbalize from monologue: " + (string?) data["message"]);
                    OnHadGoodThought?.Invoke((string?) data["message"] ?? throw new InvalidMonologue("Did not have message"));
                }

                while(timeUntilThought > 0)
                {
                    await Task.Delay(1000);

                    timeUntilThought--;

                    if(recievedMessageRecently)
                    {
                        recievedMessageRecently = false;
                        
                        timeUntilThought = TimeBetweenMessageAndThought; 
                    }
                }

                WriteData();
            }
        }

        private class InvalidRouterValue(string value) : Exception($"Router responded incorrectly: {value}") {}

        private class InvalidMonologue(string value) : Exception($"Monologue response came back ill formed: {value}");
    }
}