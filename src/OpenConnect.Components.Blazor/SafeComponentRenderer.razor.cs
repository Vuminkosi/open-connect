using Microsoft.AspNetCore.Components;
using OpenConnect.Components.Contracts;

namespace OpenConnect.Components.Blazor;

/// <summary>
/// Renders only catalogue-registered components from a validated
/// <see cref="ComponentProposal"/>. Unregistered names, invalid JSON, nested
/// objects, arrays, and markup-bearing values are rejected with a neutral
/// message. Model output is never rendered as HTML, Razor, or JavaScript.
/// </summary>
public partial class SafeComponentRenderer : ComponentBase
{
    private Type? _componentType;
    private string? _error;
    private IDictionary<string, object> _parameters = new Dictionary<string, object>(StringComparer.Ordinal);

    [Inject] private OpenConnectComponentRegistry Registry { get; set; } = default!;

    [Parameter, EditorRequired] public ComponentProposal Proposal { get; set; } = default!;

    protected override void OnParametersSet()
    {
        _componentType = null;
        _parameters = new Dictionary<string, object>(StringComparer.Ordinal);
        _error = null;

        if (Proposal is null)
        {
            _error = "This suggestion isn't available.";
            return;
        }

        var validation = Proposal.Validate(Registry.IsRegistered);
        if (validation is not null)
        {
            _error = "This suggestion isn't available.";
            return;
        }

        if (!Registry.TryResolve(Proposal.Name, out var type, out _) || type is null)
        {
            _error = "This suggestion isn't available.";
            return;
        }

        _componentType = type;
        _parameters = ParseParameters(Proposal.ParametersJson);
    }

    private static IDictionary<string, object> ParseParameters(string? json)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            object value = property.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => (object)(property.Value.GetString() ?? string.Empty),
                System.Text.Json.JsonValueKind.Number => property.Value.TryGetInt64(out var i) ? i : property.Value.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => string.Empty,
            };
            result[property.Name] = value;
        }

        return result;
    }
}
