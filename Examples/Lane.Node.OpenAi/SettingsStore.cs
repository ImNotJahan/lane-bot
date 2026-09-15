using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lane.Node.OpenAi;

/// <summary>The last settings started with, minus the API key, kept in the user's application data folder.</summary>
public sealed class SettingsStore
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lane");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() }
    };

    private readonly string _path = Path.Combine(Folder, "node-settings.json");
    private readonly Lock   _gate = new();

    private NodeSettings? _cached;

    public NodeSettings Load()
    {
        lock (_gate)
        {
            if (_cached is not null) return _cached;

            try
            {
                if (File.Exists(_path))
                    _cached = JsonSerializer.Deserialize<NodeSettings>(File.ReadAllText(_path), Json);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
            }

            return _cached ??= new NodeSettings();
        }
    }

    public void Save(NodeSettings settings)
    {
        NodeSettings clean = settings with { ApiKey = null };

        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(_path, JsonSerializer.Serialize(clean, Json));
            _cached = clean;
        }
    }
}
