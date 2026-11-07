using OpenConnect.Navigator;
using Xunit;

namespace OpenConnect.Navigator.Tests.SessionRestore;

public sealed class SessionRestoreTests
{
    [Fact]
    public void Restore_preserves_scope_state_and_revision_without_replaying_transitions()
    {
        var snapshot = Snapshot(State: NavigatorSessionState.Paused, Revision: 17);

        var restored = NavigatorSession.Restore(snapshot);

        Assert.Equal(snapshot.SessionId, restored.SessionId);
        Assert.Equal(snapshot.TenantId, restored.TenantId);
        Assert.Equal(snapshot.PrincipalId, restored.PrincipalId);
        Assert.Equal(snapshot.State, restored.State);
        Assert.Equal(snapshot.ClientInstance, restored.ClientInstance);
        Assert.Equal(snapshot.RouteId, restored.RouteId);
        Assert.Equal(snapshot.Revision, restored.Revision);
    }

    [Fact]
    public void Successful_transition_increments_revision_and_invalid_transition_does_not()
    {
        var restored = NavigatorSession.Restore(Snapshot(State: NavigatorSessionState.Ready, Revision: 4));

        restored.TransitionTo(NavigatorSessionState.Running);

        Assert.Equal(5, restored.Revision);
        Assert.Throws<InvalidOperationException>(() => restored.TransitionTo(NavigatorSessionState.Created));
        Assert.Equal(5, restored.Revision);
    }

    [Fact]
    public void Transition_at_maximum_revision_throws_without_mutating_state_or_revision()
    {
        var restored = NavigatorSession.Restore(Snapshot(State: NavigatorSessionState.Ready, Revision: long.MaxValue));

        Assert.Throws<OverflowException>(() => restored.TransitionTo(NavigatorSessionState.Running));

        Assert.Equal(NavigatorSessionState.Ready, restored.State);
        Assert.Equal(long.MaxValue, restored.Revision);
    }

    [Fact]
    public void Transition_at_maximum_minus_one_revision_succeeds_at_maximum()
    {
        var restored = NavigatorSession.Restore(Snapshot(State: NavigatorSessionState.Ready, Revision: long.MaxValue - 1));

        restored.TransitionTo(NavigatorSessionState.Running);

        Assert.Equal(NavigatorSessionState.Running, restored.State);
        Assert.Equal(long.MaxValue, restored.Revision);
    }

    [Fact]
    public void Restore_rejects_malformed_ids_unknown_states_missing_fields_and_invalid_revision()
    {
        Assert.Throws<ArgumentException>(() => NavigatorSession.Restore(Snapshot(SessionId: " ")));
        Assert.Throws<ArgumentException>(() => NavigatorSession.Restore(Snapshot(TenantId: "bad\nid")));
        Assert.Throws<ArgumentException>(() => NavigatorSession.Restore(Snapshot(State: (NavigatorSessionState)999)));
        Assert.Throws<ArgumentException>(() => NavigatorSession.Restore(Snapshot(ClientInstance: "")));
        Assert.Throws<ArgumentException>(() => NavigatorSession.Restore(Snapshot(RouteId: null!)));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavigatorSession.Restore(Snapshot(Revision: -1)));
    }

    [Fact]
    public void Restore_fails_closed_when_legacy_row_has_no_navigator_snapshot()
    {
        NavigatorSessionSnapshot? missingNavigatorData = null;

        Assert.Throws<ArgumentNullException>(() => NavigatorSession.Restore(missingNavigatorData!));
    }

    private static NavigatorSessionSnapshot Snapshot(
        string SessionId = "session-1",
        string TenantId = "tenant-1",
        string PrincipalId = "principal-1",
        NavigatorSessionState State = NavigatorSessionState.Created,
        string ClientInstance = "client-1",
        string? RouteId = "/home",
        long Revision = 0) =>
        new(SessionId, TenantId, PrincipalId, State, ClientInstance, RouteId!, Revision);
}
