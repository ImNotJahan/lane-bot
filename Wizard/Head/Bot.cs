using Newtonsoft.Json.Linq;
using Wizard.LLM;
using Wizard.Memory;
using Wizard.Utility;

namespace Wizard.Head
{
    public sealed class Bot(ILLM llm, List<IMemoryHandler> memoryHandlers, int respondToMessage)
    {
        public delegate void     OnEvent(string text);
        public event    OnEvent? OnHadGoodThought;

        readonly ILLM llm = llm;

        readonly List<IMemoryHandler> memoryHandlers = memoryHandlers;

        int timeUntilThought;

        // any time the bot recieves a message, there is a fixed interval between
        // that message and the next time it has a thought, as set below
        readonly int timeBetweenMessageAndThought = respondToMessage;

        bool recievedMessageRecently = false;

        private async Task<List<MessageContainer>> AssembleContext(
            MessageContainer? message,
            bool recentMessages = true,
            bool nonrecentMessages = true
        )
        {
            List<MessageContainer> context = [];

            foreach(IMemoryHandler handler in memoryHandlers) 
            {
                // we only add a handler's recall to the context if it matches
                // the criteria (recent, nonrecent) specified by header
                if((handler.IsRecent() && !recentMessages) || (!handler.IsRecent() && !nonrecentMessages)) continue;

                context.AddRange(await handler.RecallMemory(message));
            }

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
            List<MessageContainer> recentMessages   = await AssembleContext(formattedMessage, true, false);
            float                  enthusiasm       = await Enthusiasm(recentMessages, formattedMessage);

            if(enthusiasm <= 0.2f)
            {
                Logger.LogInformation($"Decided not to respond to message (enthusiasm {enthusiasm})");

                await RememberMessage(formattedMessage);
                WriteData();

                return null;
            }

            Logger.LogInformation($"Decided to respond to message (enthusiasm {enthusiasm})");

            MessageContainer response = await RespondToMessage(formattedMessage, enthusiasm);

            Logger.LogInformation("Will respond with {0}", response.GetContent());

            await RememberMessage(formattedMessage);
            await RememberMessage(response);

            WriteData();
            
            return response;
        }

        public void WriteData()
        {
            JObject data = [];

            try
            {
                foreach(IMemoryHandler handler in memoryHandlers)
                {
                    data[handler.GetType().ToString()] = handler.Serialize();
                }

                JSONWriter.WriteData(data);
            } catch(Exception exception)
            {
                Logger.LogError(exception.ToString());
            }
        }

        private async Task<MessageContainer> RespondToMessage(MessageContainer message, float enthusiasm)
        {
            string memoryContext       = ContextToString(await AssembleContext(message, false, true));
            string conversationContext = ContextToString(await AssembleContext(message, true,  false));

            string enthusiasmContext = enthusiasm switch
            {
                >= 0.8f => "high",
                >= 0.5f => "neutral",
                _       => "low"
            };

            string cachedDynamicPrompt = string.Format(Prompts.GetPrompt("Respond_Memory"), memoryContext);
            string dynamicPrompt       = string.Format(
                Prompts.GetPrompt("Respond_Dynamic"),
                conversationContext,
                enthusiasmContext
            );

            Logger.LogDebug("Responding to message with dynamic prompt: " + dynamicPrompt);

            return await llm.Prompt(
                [message],
                Prompts.GetPrompt("Respond"),
                cachedDynamicPrompt,
                dynamicPrompt
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

                formattedContext += message.ToString();
                formattedContext += "\n";
            }

            return formattedContext;
        }

        private async Task<float> Enthusiasm(List<MessageContainer> context, MessageContainer message)
        {
            string dynamicPrompt = string.Format(
                Prompts.GetPrompt("Routing_Dynamic"),
                ContextToString(context)
            );

            Logger.LogDebug("Gauging enthusiasm with dynamic prompt: " + dynamicPrompt);

            string result = (await llm.Prompt([message], Prompts.GetPrompt("Routing"), dynamicPrompt)).GetContent();

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
                string memoryContext       = ContextToString(await AssembleContext(lastThought, false, true));
                string conversationContext = ContextToString(await AssembleContext(lastThought, true,  false));

                string cachedDynamicPrompt = string.Format(Prompts.GetPrompt("Monologue_Memory"), memoryContext);
                string dynamicPrompt       = string.Format(
                    Prompts.GetPrompt("Monologue_Dynamic"),
                    conversationContext,
                    MessageContainer.FormatTime(DateTime.UtcNow)
                );

                Logger.LogDebug("Monologuing with dynamic prompt: " + dynamicPrompt);

                MessageContainer response = await llm.Prompt(
                    [new(dynamicPrompt, Author.Bot)],
                    Prompts.GetPrompt("Monologue"),
                    cachedDynamicPrompt
                );

                if(response.GetContent() == "") throw new Exception("Response was empty");

                JObject data;

                try
                {
                    string toParse = response.GetContent();

                    // sometimes the LLM will include extra stuff in the response,
                    // so we will look for and extract just the JSON
                    int start = toParse.IndexOf('{');
                    int end   = toParse.LastIndexOf('}');

                    if(start == -1 || end == -1 || end < start) throw new InvalidMonologue("No JSON object found in response");

                    data = JObject.Parse(toParse[start..(end + 1)]);
                } catch(Exception exception)
                {
                    Logger.LogError("Invalid monologue: " + response.GetContent());
                    throw new InvalidMonologue(exception.ToString());
                }

                timeUntilThought = (int?) data["next_thought_in_seconds"]
                                ?? throw new InvalidMonologue("Did not have next_thought_in_seconds property");

                string thought = (string?) data["thought"]
                              ?? throw new InvalidMonologue("Did not have thought property");

                Logger.LogInformation("[Thought] " + thought);
                Logger.LogInformation($"Will think again in {timeUntilThought} seconds");

                lastThought = new(thought, Author.Bot, MessageType.Thought);

                await RememberMessage(lastThought);

                if((bool?) data["speak"] == true)
                {
                    Logger.LogInformation("Will verbalize from monologue: " + (string?) data["message"]);
                    OnHadGoodThought?.Invoke((string?) data["message"] ?? throw new InvalidMonologue("Did not have message"));
                }

                WriteData();

                while(timeUntilThought > 0)
                {
                    await Task.Delay(1000);

                    timeUntilThought--;

                    if(recievedMessageRecently)
                    {
                        recievedMessageRecently = false;
                        
                        timeUntilThought = timeBetweenMessageAndThought; 
                    }
                }
            }
        }

        private class InvalidRouterValue(string value) : Exception($"Router responded incorrectly: {value}") {}

        private class InvalidMonologue(string value) : Exception($"Monologue response came back ill formed: {value}");
    }
}