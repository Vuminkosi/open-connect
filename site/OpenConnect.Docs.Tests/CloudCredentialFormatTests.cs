using System.Reflection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public class CloudCredentialFormatTests
{
    [Fact]
    public void CredentialIdParserAcceptsUnderscoresInBase64UrlSecret()
    {
        var id = Guid.NewGuid();
        var token = $"oc_dev_{id:N}_{new string('a', 20)}_{new string('b', 22)}";
        var parser = typeof(CloudCredentialService).GetMethod("TryReadCredentialId", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] arguments = [token, Guid.Empty];

        Assert.True((bool)parser.Invoke(null, arguments)!);
        Assert.Equal(id, arguments[1]);
    }
}
