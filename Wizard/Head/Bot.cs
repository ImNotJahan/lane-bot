using Newtonsoft.Json.Linq;
using Wizard.LLM;
using Wizard.Memory;
using Wizard.Utility;

namespace Wizard.Head
{
    public sealed class Bot(ILLM llm, Dictionary<string, IMemoryHandler> memoryHandlers, int respondToMessage)
    {
        public delegate void     OnEvent(string text);
        public event    OnEvent? OnHadGoodThought;

        public event Action<int>?    TimeUntilThoughtChanged;
        public event Action<string>? OnEmoticonChanged;

        readonly ILLM llm = llm;

        readonly Dictionary<string, IMemoryHandler> memoryHandlers = memoryHandlers;

        int timeUntilThought;

        // any time the bot recieves a message, there is a fixed interval between
        // that message and the next time it has a thought, as set below
        readonly int timeBetweenMessageAndThought = respondToMessage;

        bool recievedMessageRecently = false;
        bool isResponding            = false;

        private async Task<List<MessageContainer>> AssembleContext(
            MessageContainer? message,
            bool recentMessages = true,
            bool nonrecentMessages = true
        )
        {
            List<MessageContainer> context = [];

            foreach(IMemoryHandler handler in memoryHandlers.Values) 
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

            foreach(IMemoryHandler handler in memoryHandlers.Values) await handler.RememberMessage(message);
        }

        public async Task<MessageContainer?> OnMessageCreated(
            string       author,
            string       message,
            List<string> imageUrls
        )
        {
            recievedMessageRecently = true;

            Logger.LogInformation("Recieved message {0}", message);

            foreach(string url in imageUrls) await RememberMessage(new(url, Author.User, MessageType.Image, DateTime.UtcNow));

            MessageContainer       formattedMessage = new($"{author} says: {message}", time: DateTime.UtcNow);

            if(isResponding)
            {
                Logger.LogInformation("Already responding to a message, ignoring {0}", message);

                await RememberMessage(formattedMessage);

                return null;
            }

            isResponding = true;

            try
            {
                List<MessageContainer> recentMessages = await AssembleContext(formattedMessage, true, false);
                (float enthusiasm, string emoticon)   = await Enthusiasm(recentMessages, formattedMessage);

                OnEmoticonChanged?.Invoke(emoticon);

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
            finally
            {
                isResponding = false;
            }
        }

        public void WriteData()
        {
            JObject data = [];

            try
            {
                foreach(KeyValuePair<string, IMemoryHandler> pair in memoryHandlers)
                {
                    data[pair.Key] = pair.Value.Serialize();
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

            Logger.LogTrace("Responding to message with dynamic prompt: " + dynamicPrompt);

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
                formattedContext += "\n\n";
            }

            return formattedContext;
        }

        private async Task<(float enthusiasm, string emoticon)> Enthusiasm(List<MessageContainer> context, MessageContainer message)
        {
            string dynamicPrompt = string.Format(
                Prompts.GetPrompt("Routing_Dynamic"),
                ContextToString(context)
            );

            Logger.LogTrace("Gauging enthusiasm with dynamic prompt: " + dynamicPrompt);

            string result = (await llm.Prompt([message], Prompts.GetPrompt("Routing"), dynamicPrompt)).GetContent();

            JObject data;

            try
            {
                data = JObject.Parse(result);
            } catch
            {
                throw new InvalidRouterValue(result);
            }

            float?  enthusiasm = (float?)  data["enthusiasm"];
            string? emoticon   = (string?) data["emoticon"];

            if(enthusiasm is null || emoticon is null) throw new InvalidRouterValue(result);

            return ((float) enthusiasm, emoticon);
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

            _ = MonologueHandler();
        }

        MessageContainer? lastThought = null;

        private async Task Monologue()
        {
            while(true)
            {
                string memoryContext = ContextToString(await AssembleContext(lastThought, false, true));

                List<MessageContainer> recentContext = await AssembleContext(lastThought, true, false);
                
                string conversationContext = ContextToString([.. recentContext.Where(m => m.GetMessageType() != MessageType.Thought)]);
                string thoughtContext      = ContextToString([.. recentContext.Where(m => m.GetMessageType() == MessageType.Thought)]);

                string cachedDynamicPrompt = string.Format(Prompts.GetPrompt("Monologue_Memory"), memoryContext);
                string dynamicPrompt       = string.Format(
                    Prompts.GetPrompt("Monologue_Dynamic"),
                    conversationContext,
                    MessageContainer.FormatTime(DateTime.UtcNow, false),
                    thoughtContext
                );

                Logger.LogTrace("Monologuing with dynamic prompt: " + dynamicPrompt);

                MessageContainer response = await llm.Prompt(
                    [new("```json", Author.Bot)],
                    Prompts.GetPrompt("Monologue"),
                    cachedDynamicPrompt,
                    dynamicPrompt,
                    ["```"]
                );

                if(response.GetContent() == "") throw new Exception("Response was empty");

                JObject data;

                try
                {
                    data = JObject.Parse(response.GetContent());
                } catch(Exception exception)
                {
                    Logger.LogError("Invalid monologue: " + response.GetContent());
                    throw new InvalidMonologue(exception.ToString());
                }

                timeUntilThought = (int?) data["next_thought_in_seconds"]
                                ?? throw new InvalidMonologue("Did not have next_thought_in_seconds property");

                TimeUntilThoughtChanged?.Invoke(timeUntilThought);

                string emoticon = (string?) data["emoticon"] ?? "( ._.)";
                OnEmoticonChanged?.Invoke(emoticon);

                string thought = (string?) data["thought"]
                              ?? throw new InvalidMonologue("Did not have thought property");

                Logger.LogInformation("[Thought] " + thought);
                Logger.LogDebug($"Will think again in {timeUntilThought} seconds");

                lastThought = new(thought, Author.Bot, MessageType.Thought, DateTime.UtcNow);

                await RememberMessage(lastThought);

                if((bool?) data["speak"] == true)
                {
                    string message = (string?) data["message"] ?? throw new InvalidMonologue("Did not have message");
                    Logger.LogInformation("Will verbalize from monologue: " + message);
                    OnHadGoodThought?.Invoke(message);

                    await RememberMessage(new(message, Author.Bot, time: DateTime.UtcNow));
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
                    
                    TimeUntilThoughtChanged?.Invoke(timeUntilThought);
                }
            }
        }

        private class InvalidRouterValue(string value) : Exception($"Router responded incorrectly: {value}") {}

        private class InvalidMonologue(string value) : Exception($"Monologue response came back ill formed: {value}");
    }
}