using System.Collections.Concurrent;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Tools;

/// <summary>Executable tool metadata registered by the trusted host.</summary>
public sealed record ToolRegistration(ToolDescriptor Descriptor, string JsonSchema, Func<JsonElement, CancellationToken, ValueTask<JsonElement>> InvokeAsync, bool RequiresApproval = false);

/// <summary>Thread-safe tool registry; discovery is always intersected with host policy.</summary>
public sealed class ToolCatalog(ToolAuthorizationService authorization) : IToolCatalog
{
    private readonly ConcurrentDictionary<ToolId, ToolRegistration> _tools = new();
    private readonly ConcurrentDictionary<AgentId, ConcurrentDictionary<ToolId, byte>> _agentAllowLists = new();

    public void Register(ToolRegistration registration, IEnumerable<AgentId> allowedAgents)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(allowedAgents);
        ValidateSchema(registration.JsonSchema);
        if (string.IsNullOrWhiteSpace(registration.Descriptor.Id.Value)) throw new ArgumentException("Tool ID is required.", nameof(registration));
        _tools[registration.Descriptor.Id] = registration;
        foreach (var agent in allowedAgents)
            _agentAllowLists.GetOrAdd(agent, _ => new ConcurrentDictionary<ToolId, byte>())[registration.Descriptor.Id] = 0;
    }

    public ToolDescriptor? Get(ToolId toolId) => _tools.TryGetValue(toolId, out var tool) ? tool.Descriptor : null;

    // This compatibility method returns only the configured agent allowlist, never unfiltered global tools.
    public IReadOnlyList<ToolDescriptor> GetForAgent(AgentId agentId) => _tools.Values
        .Where(t => t.Descriptor.AgentId == agentId && _agentAllowLists.TryGetValue(agentId, out var list) && list.ContainsKey(t.Descriptor.Id))
        .Select(t => t.Descriptor).OrderBy(t => t.Id.Value, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<ToolDescriptor> GetAuthorized(AgentId agentId, PrincipalId principal, TenantId tenant) =>
        Array.Empty<ToolDescriptor>(); // Fail closed: this legacy contract omits TaskId and async policy evaluation.

    public async ValueTask<IReadOnlyList<ToolDescriptor>> DiscoverAsync(AgentId agent, PrincipalId principal, TenantId tenant,
        TaskId task, CancellationToken cancellationToken = default)
    {
        var results = new List<ToolDescriptor>();
        foreach (var tool in GetForAgent(agent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await authorization.IsAllowedAsync(new(agent, tool.Id, principal, tenant, task), cancellationToken).ConfigureAwait(false))
                results.Add(tool);
        }
        return results;
    }

    internal bool TryGetRegistration(ToolId id, out ToolRegistration registration) => _tools.TryGetValue(id, out registration!);

    internal static void ValidateSchema(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("type", out var type) || type.GetString() != "object")
            throw new ArgumentException("Tool argument schema must describe an object.", nameof(schema));
    }
}
