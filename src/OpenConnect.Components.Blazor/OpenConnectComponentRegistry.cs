using OpenConnect.Components.Contracts;

namespace OpenConnect.Components.Blazor;

/// <summary>
/// Thread-safe catalogue of components the assistant may propose.
/// Only registered names can be rendered; model output can never inject
/// arbitrary Razor, HTML, or JavaScript.
/// </summary>
public sealed class OpenConnectComponentRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RegistryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public OpenConnectComponentRegistry Register(Type componentType, OpenConnectComponentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.IsValid)
        {
            throw new ArgumentException("Component descriptor is invalid.", nameof(descriptor));
        }

        lock (_gate)
        {
            _entries[descriptor.Name] = new RegistryEntry(componentType, descriptor);
        }

        return this;
    }

    public bool IsRegistered(string name)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(name);
        }
    }

    public bool TryResolve(string name, out Type? componentType, out OpenConnectComponentDescriptor? descriptor)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(name, out var entry))
            {
                componentType = entry.ComponentType;
                descriptor = entry.Descriptor;
                return true;
            }
        }

        componentType = null;
        descriptor = null;
        return false;
    }

    public IReadOnlyList<OpenConnectComponentDescriptor> List()
    {
        lock (_gate)
        {
            return _entries.Values.Select(static e => e.Descriptor).ToList();
        }
    }

    private sealed record RegistryEntry(Type ComponentType, OpenConnectComponentDescriptor Descriptor);
}
