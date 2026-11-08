namespace OpenConnect.Navigator;

/// <summary>A durable, scoped snapshot used to restore a Navigator session without replaying transitions.</summary>
public sealed record NavigatorSessionSnapshot(
    string SessionId,
    string TenantId,
    string PrincipalId,
    NavigatorSessionState State,
    string ClientInstance,
    string RouteId,
    long Revision)
{
    /// <summary>Rejects snapshots that cannot represent a valid persisted session.</summary>
    public void Validate()
    {
        ValidateIdentifier(SessionId, nameof(SessionId));
        ValidateIdentifier(TenantId, nameof(TenantId));
        ValidateIdentifier(PrincipalId, nameof(PrincipalId));
        ValidateIdentifier(ClientInstance, nameof(ClientInstance));
        ValidateIdentifier(RouteId, nameof(RouteId));

        if (!Enum.IsDefined(State))
            throw new ArgumentException("Snapshot contains an unknown session state.", nameof(State));

        if (Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(Revision), "Revision cannot be negative.");
    }

    private static void ValidateIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Snapshot contains a missing or malformed identifier.", name);
    }
}
