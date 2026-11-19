using OpenConnect.Abstractions;
using OpenConnect.Samples.Blazor.Models;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class TodoTenantIsolationTests
{
    [Fact]
    public async Task New_tenant_never_inherits_later_shared_or_other_tenant_changes()
    {
        var store = InMemoryTodoStore.CreateAcceptanceStore();
        var initial = await store.GetAllAsync();
        var original = initial[0];
        var originalStatus = original.IsCompleted;
        var tenantA = new TenantId(Ulid.NewUlid());
        var tenantB = new TenantId(Ulid.NewUlid());
        var tenantAOnly = await store.AddAsync(tenantA, "Tenant A private task");
        var sharedOnly = await store.AddAsync("Shared user private task");
        Assert.True(await store.UpdateStatusAsync(original.Id, !originalStatus));

        var tenantBItems = await store.GetAllAsync(tenantB);

        Assert.DoesNotContain(tenantBItems, item => item.Id == sharedOnly.Id || item.Id == tenantAOnly.Id);
        Assert.Equal(originalStatus, Assert.Single(tenantBItems, item => item.Id == original.Id).IsCompleted);
        Assert.False(await store.UpdateStatusAsync(tenantB, tenantAOnly.Id, true));
        Assert.False(await store.DeleteAsync(tenantB, tenantAOnly.Id));
        Assert.Contains(await store.GetAllAsync(tenantA), item => item.Id == tenantAOnly.Id);
    }
}
