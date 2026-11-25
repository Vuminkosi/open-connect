using OpenConnect.Abstractions;

namespace OpenConnect.Samples.Blazor.Models;

/// <summary>
/// Development-only two-tenant directory seed used for P0 End-to-End acceptance testing.
/// Provides real canonical ULIDs, roles, and strict tenant membership boundaries.
/// </summary>
public static class TodoDirectorySeed
{
    public const string ApplicationId = "openconnect.samples.todo";

    // Tenant 1: Acme Corp (matches existing SameHostGateway default tenant ULID)
    public static readonly TenantId AcmeTenantId = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV"));
    public static readonly PrincipalId AcmeEmployeeAId = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FB0"));
    public static readonly PrincipalId AcmeEmployeeBId = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FB1"));
    public static readonly PrincipalId AcmeSupervisorAId = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FB2"));

    // Tenant 2: Globex Industries (completely isolated tenant)
    public static readonly TenantId GlobexTenantId = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAW"));
    public static readonly PrincipalId GlobexEmployee1Id = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FC0"));
    public static readonly PrincipalId GlobexSupervisor1Id = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FC1"));

    public static readonly IReadOnlyList<SeedTenant> Tenants =
    [
        new SeedTenant("tenant-acme", AcmeTenantId, "Acme Corp"),
        new SeedTenant("tenant-globex", GlobexTenantId, "Globex Industries")
    ];

    public static readonly IReadOnlyList<SeedPrincipal> Principals =
    [
        new SeedPrincipal("emp-a", "tenant-acme", AcmeTenantId, AcmeEmployeeAId, "Alex Rivera (Acme Employee A)", "Employee", AcmeSupervisorAId),
        new SeedPrincipal("emp-b", "tenant-acme", AcmeTenantId, AcmeEmployeeBId, "Blake Chen (Acme Employee B)", "Employee", AcmeSupervisorAId),
        new SeedPrincipal("sup-a", "tenant-acme", AcmeTenantId, AcmeSupervisorAId, "Morgan Patel (Acme Supervisor)", "Supervisor", null),
        new SeedPrincipal("emp-g1", "tenant-globex", GlobexTenantId, GlobexEmployee1Id, "Jordan Lee (Globex Employee)", "Employee", GlobexSupervisor1Id),
        new SeedPrincipal("sup-g1", "tenant-globex", GlobexTenantId, GlobexSupervisor1Id, "Taylor Kim (Globex Supervisor)", "Supervisor", null)
    ];

    public static SeedPrincipal? FindPrincipal(TenantId tenantId, PrincipalId principalId) =>
        Principals.FirstOrDefault(p => p.TenantId == tenantId && p.PrincipalId == principalId);

    public static SeedPrincipal? FindByKey(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : Principals.FirstOrDefault(p => string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    public static SeedTenant? FindTenant(TenantId tenantId) =>
        Tenants.FirstOrDefault(t => t.TenantId == tenantId);

    public static SeedTenant? FindTenantByKey(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : Tenants.FirstOrDefault(t => string.Equals(t.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    public static SeedPrincipal DefaultPrincipal => Principals[0];
}

public sealed record SeedTenant(string Key, TenantId TenantId, string DisplayName);

public sealed record SeedPrincipal(
    string Key,
    string TenantKey,
    TenantId TenantId,
    PrincipalId PrincipalId,
    string DisplayName,
    string Role,
    PrincipalId? SupervisorPrincipalId);

/// <summary>
/// Scoped development persona switcher state so Blazor UI components and acceptance testers
/// can switch authenticated desktop persona honestly within a Blazor circuit.
/// </summary>
public sealed class TodoDevelopmentPersonaState
{
    private SeedPrincipal _current = TodoDirectorySeed.DefaultPrincipal;

    public SeedPrincipal Current => _current;

    public event Action? Changed;

    public bool TrySelectByKey(string key)
    {
        var found = TodoDirectorySeed.FindByKey(key);
        if (found is null)
            return false;

        _current = found;
        Changed?.Invoke();
        return true;
    }

    public bool TrySelect(TenantId tenantId, PrincipalId principalId)
    {
        var found = TodoDirectorySeed.FindPrincipal(tenantId, principalId);
        if (found is null)
            return false;

        _current = found;
        Changed?.Invoke();
        return true;
    }
}
