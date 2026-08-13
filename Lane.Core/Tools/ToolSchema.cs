using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Lane.Core.Tools;

/// <summary>
/// Generates a tool's JSON Schema from the C# record that its arguments deserialise into.
///
/// The record is the single source of truth. Hand-written schemas drift from the type they
/// describe — the drift is silent, and it surfaces as a model confidently passing a field
/// the tool then ignores.
/// </summary>
public static class ToolSchema
{
    private static readonly ConcurrentDictionary<Type, JsonElement> Cache = new();

    /// <summary>
    /// Used to *describe* arguments. Strict number handling so a parameter advertises
    /// <c>integer</c> rather than "integer or a string that looks like one".
    /// </summary>
    public static JsonSerializerOptions SchemaOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        NumberHandling   = JsonNumberHandling.Strict,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    /// Used to *read* arguments. Deliberately more forgiving than the advertised schema:
    /// a model that sends "5" for an integer should be understood, not rejected.
    /// </summary>
    public static JsonSerializerOptions ArgumentOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        NumberHandling   = JsonNumberHandling.AllowReadingFromString,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly JsonSchemaExporterOptions Exporter = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = static (context, node) =>
        {
            DescriptionAttribute? description = context.PropertyInfo?.AttributeProvider?
                .GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
                .OfType<DescriptionAttribute>()
                .FirstOrDefault();

            if (description is not null && node is JsonObject obj) obj["description"] = description.Description;

            return node;
        }
    };

    public static JsonElement For<TArgs>() => For(typeof(TArgs));

    public static JsonElement For(Type type) => Cache.GetOrAdd(type, static t =>
    {
        JsonNode node = SchemaOptions.GetJsonSchemaAsNode(t, Exporter);

        // Providers expect an object schema at the top level. A tool whose arguments are
        // not an object is a mistake worth catching at startup rather than mid-turn.
        if (node is not JsonObject obj || (string?)obj["type"] != "object")
            throw new InvalidOperationException(
                $"Tool arguments must be an object type; '{t.Name}' produced: {node.ToJsonString()}");

        obj["properties"] ??= new JsonObject();

        return JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString());
    });

    /// <summary>A schema for a tool that takes no arguments.</summary>
    public static JsonElement Empty { get; } =
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""");
}
