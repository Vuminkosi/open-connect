using System.Reflection;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ExecutionContextSnapshotTests
{
    [Fact]
    public void Context_has_no_credential_material_properties()
    {
        string[] prohibited = ["Token", "Secret", "Password", "Cookie", "Bearer", "Credential", "Authorization", "ApiKey"];

        var names = typeof(AgentExecutionContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name);

        // CancellationToken is the required cancellation primitive, not credential material.
        Assert.DoesNotContain(names, name => name != nameof(AgentExecutionContext.CancellationToken) &&
            prohibited.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Context_exposes_required_authorization_snapshot_members()
    {
        AssertProperty<string>(nameof(AgentExecutionContext.TenantScope));
        AssertProperty<string>(nameof(AgentExecutionContext.PrincipalOrWorkloadScope));
        AssertProperty<AgentIdentityMode>(nameof(AgentExecutionContext.IdentityKind));
        AssertProperty<SessionId>(nameof(AgentExecutionContext.SessionId));
        AssertProperty<TaskId>(nameof(AgentExecutionContext.TaskId));
        AssertProperty<ExecutionId>(nameof(AgentExecutionContext.AgentExecutionId));
        AssertProperty<DateTimeOffset>(nameof(AgentExecutionContext.Deadline));
        AssertProperty<long>(nameof(AgentExecutionContext.PolicyRevision));
        AssertProperty<long>(nameof(AgentExecutionContext.AuthRevision));
        AssertProperty<long>(nameof(AgentExecutionContext.CatalogRevision));
        AssertProperty<System.Collections.Immutable.ImmutableDictionary<string, long>>(nameof(AgentExecutionContext.RemainingBudgets));
        AssertProperty<CancellationToken>(nameof(AgentExecutionContext.CancellationToken));
    }

    private static void AssertProperty<T>(string name)
    {
        var property = typeof(AgentExecutionContext).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        Assert.Equal(typeof(T), property.PropertyType);
    }
}
