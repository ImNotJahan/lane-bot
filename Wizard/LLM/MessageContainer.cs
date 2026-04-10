using Anthropic.Models.Messages;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using Wizard.Utility;

namespace Wizard.LLM
{
    public sealed class MessageContainer
    {
        // either the text of the message, or a base64 encoding of an image
        readonly string    content;
        readonly Author    author;
        readonly DateTime? time;

        readonly MessageType type;

        public MessageContainer(
            string      content,
            Author      author = Author.User,
            MessageType type   = MessageType.Text,
            DateTime?   time   = null
        )
        {
            this.content = content;
            this.author  = author;  
            this.time    = time;
            this.type    = type;
        }

        public MessageContainer(JToken data)
        {
            string? content = (string?) data["content"];
            int?    author  = (int?)    data["author"];
            int?    type    = (int?)    data["type"];

            if(content is null) throw new Exception("Content is null");
            if(author  is null) throw new Exception("Author is null");

            DateTime? time = (DateTime?) data["time"];

            if(time is not null) this.time = (DateTime) time;

            if(!Enum.IsDefined(typeof(Author), author)) throw new Exception($"Invalid author type {author}");

            if(type is not null)
            {
                if(!Enum.IsDefined(typeof(MessageType), type)) throw new Exception($"Invalid MessageType {type}");

                this.type = (MessageType) type;
            }
            
            this.author  = (Author) author;
            this.content = content;
        }

        public MessageParam Anthropic()
        {
            Role role = author switch
            {
                Author.User => Role.User,
                Author.Bot  => Role.Assistant,
                _           => throw new Exception($"Unexpected author type {author}")
            };

            if(type == MessageType.Text)
            {
                return new()
                {
                    Role    = role,
                    Content = ToString()
                };
            } else if(type == MessageType.Image)
            {
                return new()
                {
                    Role    = role,
                    Content = new([
                        new ImageBlockParam()
                        {
                            Source = new(new UrlImageSource(content))
                        }
                    ])
                };
            } else if(type == MessageType.Thought)
            {
                return new()
                {
                    Role    = role,
                    Content = $"<thought>{content}</thought>"
                };
            }

            throw new Exception("Unknown MessageType " + type);
        }

        public ChatMessage OpenAI()
        {
            string text = type == MessageType.Thought ? $"<thought>{content}</thought>" : ToString();

            return author switch
            {
                Author.User => new UserChatMessage(text),
                Author.Bot  => new AssistantChatMessage(text),
                _           => throw new Exception($"Unexpected author type {author}")
            };
        }

        public string      GetContent()     => content;
        public Author      GetAuthor()      => author;
        public MessageType GetMessageType() => type;
        public DateTime?   GetTime()        => time;

        public override string ToString()
        {
            string formatted = GetContent();

            if (time is not null) formatted = $"[{FormatTime((DateTime) time)}] {formatted}";

            return formatted;
        }

        /// <summary>
        /// Formats time in the way that appears when a message is converted to
        /// a string. Changes timezone as specified in settings
        /// </summary>
        /// <param name="time">The time to format</param>
        /// <returns>The time formatted</returns>
        public static string FormatTime(DateTime time, bool includeTimeSince = true)
        {
            DateTime shiftedTime = time.AddHours(Settings.instance is null ? 0 : Settings.instance.TimezoneSettings.HourShift)
                                       .AddMinutes(Settings.instance is null ? 0 : Settings.instance.TimezoneSettings.MinuteShift);

            return shiftedTime.ToString("yyyy/MM/dd HH:mm:ss") + (includeTimeSince ? $" ({TimeSince(time)})" : "");
        }

        public static string TimeSince(DateTime time)
        {
            return (DateTime.Now - time) switch
            {
                { TotalHours: < 1 } ts => $"{ts.Minutes} minutes ago",
                { TotalDays: < 1 }  ts => $"{ts.Hours} hours ago",
                { TotalDays: < 2 }     => $"yesterday",
                { TotalDays: < 5 }     => $"on {time.DayOfWeek}",
                TimeSpan ts            => $"{ts.Days} days ago",
            };
        }

        public JToken Serialize()
        {
            return new JObject()
            {
                ["content"] = content,
                ["author"]  = (int) author,
                ["time"]    = time,
                ["type"]    = (int) type
            };
        }
    }

    public enum Author
    {
        User,
        Bot
    }

    public enum MessageType
    {
        Text,
        Image,
        Thought
    }
}