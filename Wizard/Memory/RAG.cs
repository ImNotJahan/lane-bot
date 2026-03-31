using Newtonsoft.Json.Linq;
using Qdrant.Client;
using Wizard.LLM;
using OpenAI.Embeddings;
using Qdrant.Client.Grpc;
using Azure.AI.OpenAI;
using Azure;

namespace Wizard.Memory
{
    public sealed class RAG(ulong selectLimit, int recallInterval) : SlidingWindow(recallInterval)
    {
        readonly QdrantClient qdrant = new(
            host:   DotNetEnv.Env.GetString("QDRANT_ENDPOINT"),
            https:  true,
            port:   6334,
            apiKey: DotNetEnv.Env.GetString("QDRANT_API_KEY")
        );
        readonly EmbeddingClient embeddingClient = new AzureOpenAIClient(
            new Uri(               DotNetEnv.Env.GetString("OPENAI_EMBEDDING_ENDPOINT")),
            new AzureKeyCredential(DotNetEnv.Env.GetString("OPENAI_EMBEDDING_API_KEY"))
        ).GetEmbeddingClient(EmbeddingsModel);

        private const string EmbeddingsModel = "text-embedding-3-small";
        private const string CollectionName  = "lane-rag";

        List<MessageContainer> lastRecall = [];

        private async Task<float[]> CalculateVectors(MessageContainer message)
        {
            OpenAIEmbedding embedding = await embeddingClient.GenerateEmbeddingAsync(message.GetContent());

            return embedding.ToFloats().ToArray();
        }

        public override async Task RememberMessage(MessageContainer message)
        {
            if(message.GetMessageType() == MessageType.Thought) return;

            await base.RememberMessage(message);

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

        public override async Task<List<MessageContainer>> RecallMemory(MessageContainer? message)
        {
            if(message is null) return [];

            if(memory.Count < maxMessages) return lastRecall;

            IReadOnlyList<ScoredPoint> points = await qdrant.QueryAsync(
                collectionName: CollectionName,
                query:          await CalculateVectors(message),
                limit:          selectLimit
            );

            memory.Clear();

            List<MessageContainer> messages = [];

            foreach(ScoredPoint point in points)
            {
                messages.Add(new(
                    point.Payload["text"].StringValue,
                    (Author) point.Payload["author"].IntegerValue
                ));
            }

            lastRecall = messages;

            return messages;
        }

        public override bool IsRecent() => false;

        public override JToken Serialize() => new JObject();
        public override void   Deserialize(JToken data) {}
    }
}
