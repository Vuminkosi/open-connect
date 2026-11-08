using OpenConnect.Navigator.Persistence.Postgres;
using Xunit;

namespace OpenConnect.Navigator.Persistence.Tests;

public sealed class NavigatorPersistenceMappingTests
{
    [Theory]
    [InlineData("f47ac10b-58cc-4372-a567-0e02b2c3d479")]
    [InlineData("f47ac10b58cc4372a5670e02b2c3d479")]
    public void TryUuid_AcceptsDAndNFormats(string value)
    {
        Assert.True(NavigatorPersistenceMapping.TryUuid(value, out var id));
        Assert.Equal(Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479"), id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tenant-not-uuid")]
    public void TryUuid_RejectsNonUuid(string? value) => Assert.False(NavigatorPersistenceMapping.TryUuid(value, out _));

    [Fact]
    public void ReceiptTaskId_IsStableAndScopeSensitive()
    {
        var tenant = Guid.NewGuid();
        var principal = Guid.NewGuid();
        Assert.Equal(NavigatorPersistenceMapping.ReceiptTaskId(tenant, principal, "key"), NavigatorPersistenceMapping.ReceiptTaskId(tenant, principal, "key"));
        Assert.NotEqual(NavigatorPersistenceMapping.ReceiptTaskId(tenant, principal, "key"), NavigatorPersistenceMapping.ReceiptTaskId(tenant, principal, "other"));
    }

    [Fact]
    public void ClientTaskKeys_UseApprovedPrefixes()
    {
        Assert.Equal("navigator:receipt:key", NavigatorPersistenceMapping.ReceiptClientTaskId("key"));
        Assert.StartsWith("navigator:", NavigatorPersistenceMapping.SessionClientTaskId(Guid.Empty));
    }
}
