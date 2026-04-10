using Qdrant.Client;
using Wizard.LLM;
using OpenAI.Embeddings;
using Qdrant.Client.Grpc;
using Azure.AI.OpenAI;
using Azure;
using RX;
using Google.Protobuf.Collections;

namespace Wizard.Memory
{
    public sealed class RAG(ulong selectLimit, int writeInterval) : SlidingWindow(writeInterval, false)
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

        private async Task<float[]> CalculateVectors(MessageContainer message)
        {
            OpenAIEmbedding embedding = await embeddingClient.GenerateEmbeddingAsync(message.GetContent());

            return embedding.ToFloats().ToArray();
        }

        public override async Task RememberMessage(MessageContainer message)
        {
            if(message.GetMessageType() == MessageType.Thought) return;

            await base.RememberMessage(message);

            if(memory.Count < maxMessages) return;

            List<PointStruct> points = [];

            foreach(MessageContainer m in memory)
            {
                PointStruct point = new()
                {
                    Id      = new() { Uuid = Guid.NewGuid().ToString() },
                    Vectors = await CalculateVectors(m),
                    Payload =
                    {
                        ["text"]   = m.GetContent(),
                        ["author"] = (int) m.GetAuthor()
                    }
                };

                if (m.GetTime() is DateTime nonNull) point.Payload["time"] = nonNull.ToString();

                points.Add(point);
            }

            await qdrant.UpsertAsync(CollectionName, points);

            memory.Clear();
        }

        public override async Task<List<MessageContainer>> RecallMemory(MessageContainer? message)
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
                DateTime? time = point.Payload.TryGetValue("time", out Value value) 
                               ? DateTime.Parse(value.StringValue) 
                               : null;

                messages.Add(new(
                    point.Payload["text"].StringValue,
                    (Author) point.Payload["author"].IntegerValue,
                    time: time
                ));
            }

            return messages;
        }

        public override bool IsRecent() => false;
    }
}
