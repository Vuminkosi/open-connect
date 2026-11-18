using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ModelHistoryOwnershipTests
{
    [Fact]
    public void NativeMafSessionPolicy_ValidatesSuccessfully()
    {
        var policy = new ModelHistoryProviderPolicy("openai-gpt4", ModelHistoryOwner.NativeMafSession);
        ModelHistoryOwnershipValidator.Validate(policy);
        Assert.True(ModelHistoryOwnershipValidator.IsNativeMafSession(policy));
        Assert.False(ModelHistoryOwnershipValidator.IsServiceManaged(policy));
    }

    [Fact]
    public void ServiceManagedPolicy_ValidatesSuccessfully()
    {
        var policy = new ModelHistoryProviderPolicy("openai-assistants", ModelHistoryOwner.ServiceManaged);
        ModelHistoryOwnershipValidator.Validate(policy);
        Assert.True(ModelHistoryOwnershipValidator.IsServiceManaged(policy));
        Assert.False(ModelHistoryOwnershipValidator.IsNativeMafSession(policy));
    }

    [Fact]
    public void NullOrEmptyProviderId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ModelHistoryProviderPolicy("", ModelHistoryOwner.NativeMafSession));
        Assert.Throws<ArgumentException>(() => new ModelHistoryProviderPolicy("   ", ModelHistoryOwner.ServiceManaged));
    }

    [Fact]
    public void UndefinedOrBothHistoryOwner_ThrowsInvalidOperationException()
    {
        var invalidPolicy = new ModelHistoryProviderPolicy("test-provider", (ModelHistoryOwner)999);
        var ex = Assert.Throws<InvalidOperationException>(() => ModelHistoryOwnershipValidator.Validate(invalidPolicy));
        Assert.Contains("never both or neither", ex.Message);
    }
}
