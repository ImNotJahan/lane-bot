using Anthropic.Models.Messages;
using Newtonsoft.Json.Linq;

namespace Wizard.LLM
{
    public sealed class MessageContainer
    {
        // either the text of the message, or a base64 encoding of an image
        readonly string   content;
        readonly Author   author;
        readonly DateTime time;

        readonly MessageType type;

        public MessageContainer(
            string      content,
            Author      author = Author.User,
            MessageType type   = MessageType.Text
        )
        {
            this.content = content;
            this.author  = author;  
            this.time    = DateTime.UtcNow;
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

        public string      GetContent()     => content;
        public Author      GetAuthor()      => author;
        public MessageType GetMessageType() => type;

        public override string ToString()
        {
            string formatted = GetContent();

            formatted = "[" + time.ToString("yyyy/MM/dd HH:mm:ss") + "] " + formatted;

            return formatted;
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