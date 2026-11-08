using OpenConnect.Navigator.AspNetCore;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests;

public sealed class DurableBoundaryTests
{
    [Fact]
    public void New_store_instance_is_not_a_recovery_source()
    {
        INavigatorSessionStore beforeRestart = new NavigatorMemoryStore();
        INavigatorSessionStore afterRestart = new NavigatorMemoryStore();

        Assert.False(beforeRestart.IsDurable);
        Assert.False(afterRestart.IsDurable);
        Assert.NotSame(beforeRestart, afterRestart);
    }
}
