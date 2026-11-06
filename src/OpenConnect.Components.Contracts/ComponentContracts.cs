namespace OpenConnect.Components.Contracts;

/// <summary>
/// Metadata describing a Blazor component that the assistant is allowed to propose.
/// The catalogue is the single allow-list: model output may only reference
/// registered names, never arbitrary Razor, HTML, or JavaScript.
/// </summary>
public sealed record OpenConnectComponentDescriptor(
    string Name,
    string Title,
    string Description,
    string? Category = null,
    IReadOnlyList<string>? Tags = null)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(Title)
        && IsValidName(Name);

    /// <summary>
    /// Component names are restricted to a safe slug so they can never carry markup.
    /// </summary>
    public static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            {
                return false;
            }
        }

        return char.IsLetterOrDigit(name[0]) && char.IsLetterOrDigit(name[^1]);
    }
}

/// <summary>
/// Metadata contract for a safe, pre-registered UI component.
/// Implementations live in the Blazor package; this contract must not reference Blazor.
/// </summary>
public interface IOpenConnectComponent
{
    OpenConnectComponentDescriptor Descriptor { get; }
}

/// <summary>
/// A model-proposed UI block. Schema-validated before rendering:
/// only catalogue-registered <see cref="Name"/> values are accepted and
/// parameters are limited to a flat JSON object of primitives.
/// </summary>
public sealed record ComponentProposal(string Name, string? ParametersJson = null)
{
    /// <summary>
    /// Validates the proposal against the allow-list. Returns an error message or null when valid.
    /// </summary>
    public string? Validate(Func<string, bool> isRegistered)
    {
        ArgumentNullException.ThrowIfNull(isRegistered);
        if (!OpenConnectComponentDescriptor.IsValidName(Name))
        {
            return $"Component '{Name}' is not a valid registered component name.";
        }

        if (!isRegistered(Name))
        {
            return $"Component '{Name}' is not registered and cannot be rendered.";
        }

        if (string.IsNullOrWhiteSpace(ParametersJson))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ParametersJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return "Component parameters must be a JSON object.";
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var kind = property.Value.ValueKind;
                var allowed = kind is System.Text.Json.JsonValueKind.String
                    or System.Text.Json.JsonValueKind.Number
                    or System.Text.Json.JsonValueKind.True
                    or System.Text.Json.JsonValueKind.False
                    or System.Text.Json.JsonValueKind.Null;
                if (!allowed)
                {
                    return $"Component parameter '{property.Name}' must be a string, number, boolean, or null.";
                }

                if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var text = property.Value.GetString() ?? string.Empty;
                    if (text.Length > 2000 || text.Contains('<') || text.Contains('>'))
                    {
                        return $"Component parameter '{property.Name}' contains unsupported markup.";
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return "Component parameters are not valid JSON.";
        }

        return null;
    }
}
