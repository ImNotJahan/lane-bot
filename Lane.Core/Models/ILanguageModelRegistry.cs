using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Models;

/// <summary>A job a model is bound to: "respond", "monologue", "routing", "summarize".</summary>
public readonly record struct ModelRole(string Value)
{
    public static ModelRole Respond   { get; } = new("respond");
    public static ModelRole Monologue { get; } = new("monologue");
    public static ModelRole Routing   { get; } = new("routing");
    public static ModelRole Summarize { get; } = new("summarize");

    public override string ToString() => Value;
}

public interface ILanguageModelRegistry
{
    ILanguageModel Get(ModelRole role);

    /// <summary>Honours per-surface overrides — the API can answer on a different model than Discord.</summary>
    ILanguageModel Get(ModelRole role, SessionDescriptor? session);

    IReadOnlyCollection<ILanguageModel> All { get; }

    /// <summary>Which roles resolve to which instance, for the dashboard and for diagnostics.</summary>
    IReadOnlyDictionary<string, string> RoleBindings { get; }
}

/// <summary>Thrown at startup rather than at request time — a misbound role should never
/// be discovered halfway through a conversation.</summary>
public sealed class ModelBindingException(string message) : Exception(message);

public sealed class LanguageModelRegistry : ILanguageModelRegistry
{
    private readonly Dictionary<string, ILanguageModel> _instances;
    private readonly Dictionary<string, string>         _roles;
    private readonly Dictionary<(string Surface, string Role), string> _overrides;

    public LanguageModelRegistry(
        IEnumerable<ILanguageModel> instances,
        IReadOnlyDictionary<string, string> roles,
        IReadOnlyDictionary<(string Surface, string Role), string>? overrides = null,
        ILogger<LanguageModelRegistry>? log = null)
    {
        _instances = instances.ToDictionary(m => m.Descriptor.InstanceId, StringComparer.OrdinalIgnoreCase);
        _roles     = new Dictionary<string, string>(roles, StringComparer.OrdinalIgnoreCase);
        _overrides = overrides?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];

        if (_instances.Count == 0)
            throw new ModelBindingException("No language model instances were configured.");

        foreach ((string role, string instance) in _roles)
        {
            if (!_instances.ContainsKey(instance))
                throw new ModelBindingException(
                    $"Role '{role}' is bound to model instance '{instance}', which is not configured. " +
                    $"Known instances: {string.Join(", ", _instances.Keys)}.");
        }

        foreach (((string surface, string role), string instance) in _overrides)
        {
            if (!_instances.ContainsKey(instance))
                throw new ModelBindingException(
                    $"Override for surface '{surface}' role '{role}' names unknown model instance '{instance}'.");
        }

        log?.LogInformation("Model roles bound: {Bindings}",
            string.Join(", ", _roles.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    public IReadOnlyCollection<ILanguageModel> All => _instances.Values;

    public IReadOnlyDictionary<string, string> RoleBindings => _roles;

    public ILanguageModel Get(ModelRole role) => Get(role, null);

    public ILanguageModel Get(ModelRole role, SessionDescriptor? session)
    {
        if (session is not null &&
            _overrides.TryGetValue((session.Id.Surface.Value, role.Value), out string? overridden))
            return _instances[overridden];

        if (!_roles.TryGetValue(role.Value, out string? instanceId))
            throw new ModelBindingException(
                $"No model is bound to role '{role}'. Configured roles: {string.Join(", ", _roles.Keys)}.");

        return _instances[instanceId];
    }

    /// <summary>
    /// Fails fast when a role that will be asked to call tools is bound to a model that
    /// cannot. With no prompt-JSON fallback, this has to be loud.
    /// </summary>
    public void ValidateCapabilities(IEnumerable<ModelRole> toolUsingRoles)
    {
        List<string> problems = [];

        foreach (ModelRole role in toolUsingRoles)
        {
            if (!_roles.ContainsKey(role.Value)) continue;

            ILanguageModel model = Get(role);

            if (!model.Descriptor.Supports(ModelCapabilities.Tools))
                problems.Add($"role '{role}' → {model.Descriptor} does not support tool calling");
        }

        if (problems.Count > 0)
            throw new ModelBindingException(
                "Tool-calling is required but unavailable: " + string.Join("; ", problems) +
                ". Bind these roles to tool-capable models.");
    }
}
