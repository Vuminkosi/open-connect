namespace OpenConnect.Samples.McpMicroservices.ServiceB;

/// <summary>
/// Fixed demo identity keys for the cross-service isolation demo. Intentionally
/// duplicated (owned copy) from service A: the services share no code or database,
/// only these documented demo constants. Values are synthetic ULIDs reserved for
/// the demo; they carry no production meaning.
/// </summary>
public static class ServiceBDemoKeys
{
    /// <summary>
    /// Demo session ULID (synthetic, fixed for determinism; must match service A's seed).
    /// </summary>
    public const string SessionUlid = "00000000000000000000000001";

    /// <summary>
    /// Demo tenant ULID (synthetic, fixed for determinism; must match service A's seed).
    /// </summary>
    public const string TenantUlid = "00000000000000000000000002";

    /// <summary>
    /// Demo principal ULID (synthetic, fixed for determinism; must match service A's seed).
    /// </summary>
    public const string PrincipalUlid = "00000000000000000000000003";
}
