using Newtonsoft.Json.Linq;
using Qdrant.Client;
using Wizard.LLM;
using OpenAI.Embeddings;
using Qdrant.Client.Grpc;
using Azure.AI.OpenAI;
using Azure;

namespace Wizard.Memory
{
    public sealed class RAG : IMemoryHandler
    {
        readonly QdrantClient    qdrant;
        readonly EmbeddingClient embeddingClient;

        private const string EmbeddingsModel = "text-embedding-3-small";
        private const string CollectionName  = "lane-rag";

        readonly ulong selectLimit;

        public RAG(ulong selectLimit)
        {
            this.selectLimit = selectLimit;
            
            qdrant = new(
                host:   DotNetEnv.Env.GetString("QDRANT_ENDPOINT"),
                https:  true,
                port:   6334,
                apiKey: DotNetEnv.Env.GetString("QDRANT_API_KEY")
            );

            AzureOpenAIClient azureClient = new(
                new Uri(DotNetEnv.Env.GetString("OPENAI_EMBEDDING_ENDPOINT")),
                new AzureKeyCredential(DotNetEnv.Env.GetString("OPENAI_EMBEDDING_API_KEY"))
            );

            embeddingClient = azureClient.GetEmbeddingClient(EmbeddingsModel);
        }

        private async Task<float[]> CalculateVectors(MessageContainer message)
        {
            OpenAIEmbedding embedding = await embeddingClient.GenerateEmbeddingAsync(message.GetContent());

            return embedding.ToFloats().ToArray();
        }
        
        public async Task RememberMessage(MessageContainer message)
        {
            if(message.GetMessageType() == MessageType.Thought) return;
            
            await qdrant.UpsertAsync(
                collectionName: CollectionName,
                [
                    new()
                    {
                        Id = new() { Uuid = Guid.NewGuid().ToString() },
                        Vectors = await CalculateVectors(message),
                        Payload = {
                            ["text"]   = message.GetContent(),
                            ["author"] = (int) message.GetAuthor()
                        }
                    }
                ]
            );
        }

        public async Task<List<MessageContainer>> RecallMemory(MessageContainer? message)
        {
            if(message is null) return [];

            IReadOnlyList<ScoredPoint> points = await qdrant.QueryAsync(
                collectionName: CollectionName,
                query:          await CalculateVectors(message),
                limit:          selectLimit
            );

            List<MessageContainer> messages = [];

            foreach(ScoredPoint point in points)
            {
                messages.Add(new(
                    point.Payload["text"].StringValue,
                    (Author) point.Payload["author"].IntegerValue
                ));
            }

            return messages;
        }

        public JToken Serialize()
        {
            return new JObject();
        }

        public void Deserialize(JToken data)
        {
            
        }
    }
}