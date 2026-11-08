using OpenConnect.Navigator.Blazor;
using Xunit;

namespace OpenConnect.Navigator.Blazor.Tests;

/// <summary>
/// NAV-REM-BLZ-02: Blazor coexistence lifecycle — scoped disposal, focus
/// restoration, Escape=pause, and the nullable highlight-rect contract.
/// </summary>
public sealed class LifecycleFixTests
{
    private static NavigatorTourService StartedWithFocus(string? priorHandle = "trigger-btn")
    {
        var tour = new NavigatorTourService();
        tour.Register("a", "home", "First target", null);
        Assert.True(tour.StartTour(
            [
                new NavigatorTourStep("One", "First.", "a"),
                new NavigatorTourStep("Two", "Second."),
            ],
            "home",
            priorHandle));
        return tour;
    }

    // ---- Scoped disposal (S2-1) ----

    [Fact]
    public void DuplicateBareId_AcrossRoutes_AreDistinctEntries()
    {
        var tour = new NavigatorTourService();
        var first = tour.Register("save", "r1", "Save r1", null);
        var second = tour.Register("save", "r2", "Save r2", null);

        Assert.Equal(2, tour.Size);
        Assert.Equal(1, first.Revision);
        Assert.Equal(1, second.Revision); // independent revision sequences
        Assert.Equal("Save r1", tour.Get("save", "r1")?.Label);
        Assert.Equal("Save r2", tour.Get("save", "r2")?.Label);
    }

    [Fact]
    public void ReRegister_SameComposite_BumpsRevision_OtherRouteUntouched()
    {
        var tour = new NavigatorTourService();
        tour.Register("save", "r1", "Save r1", null);
        tour.Register("save", "r2", "Save r2", null);

        var bumped = tour.Register("save", "r1", "Save r1 now", null);

        Assert.Equal(2, bumped.Revision);
        Assert.Equal(2, tour.Size);
        Assert.Equal("Save r1 now", tour.Get("save", "r1")?.Label);
        Assert.Equal(1, tour.Get("save", "r2")?.Revision);
    }

    [Fact]
    public void ScopedUnregister_RemovesOnlyOwnRoute()
    {
        var tour = new NavigatorTourService();
        tour.Register("save", "r1", "Save r1", null);
        tour.Register("save", "r2", "Save r2", null);

        Assert.True(tour.Unregister("save", "r1"));
        Assert.Null(tour.Get("save", "r1"));
        Assert.NotNull(tour.Get("save", "r2"));
        Assert.Equal(1, tour.Size);
    }

    [Fact]
    public void UnregisterRoute_RemovesOnlyOwnRoute_WithDuplicateIds()
    {
        var tour = new NavigatorTourService();
        tour.Register("save", "r1", "Save r1", null);
        tour.Register("save", "r2", "Save r2", null);

        Assert.Equal(1, tour.UnregisterRoute("r1"));
        Assert.Null(tour.Get("save", "r1"));
        Assert.NotNull(tour.Get("save", "r2"));
    }

    [Fact]
    public void ValidateTour_ResolvesScopedComposite_NotBareId()
    {
        var tour = new NavigatorTourService();
        tour.Register("save", "checkout", "Save", null);
        tour.Register("save", "home", "Save home", null);

        // Each route validates against its own composite.
        Assert.True(tour.ValidateTour([new NavigatorTourStep("T", "B.", "save")], "home").Ok);
        Assert.True(tour.ValidateTour([new NavigatorTourStep("T", "B.", "save")], "checkout").Ok);

        // A route with no registration fails with the cross-screen message.
        var result = tour.ValidateTour([new NavigatorTourStep("T", "B.", "save")], "other");
        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Message.Contains("another screen"));
    }

    // ---- Focus restoration (S2-2) ----

    [Fact]
    public void Finish_RestoresPriorFocus_ViaLogSeam()
    {
        var tour = StartedWithFocus("trigger-btn");
        var restored = new List<string?>();
        tour.FocusRestore = h => restored.Add(h);

        tour.Next(); // to step 2
        Assert.Empty(tour.FocusRestoreLog);

        tour.Next(); // finishes
        Assert.Equal(NavigatorTourStatus.Finished, tour.Status);
        Assert.Equal(["trigger-btn"], tour.FocusRestoreLog);
        Assert.Equal(["trigger-btn"], restored);
    }

    [Fact]
    public void Stop_RestoresPriorFocus_SkipSemantics()
    {
        var tour = StartedWithFocus("trigger-btn");
        var restored = new List<string?>();
        tour.FocusRestore = h => restored.Add(h);

        tour.Stop();

        Assert.Equal(NavigatorTourStatus.Skipped, tour.Status);
        Assert.Null(tour.CurrentStep);
        Assert.Equal(["trigger-btn"], restored);
    }

    [Fact]
    public void Stop_FromPaused_RestoresFocus_PauseExit()
    {
        var tour = StartedWithFocus("trigger-btn");
        var restored = new List<string?>();
        tour.FocusRestore = h => restored.Add(h);

        tour.Pause();
        Assert.Empty(restored); // pause alone must not restore

        tour.Stop();
        Assert.Equal(NavigatorTourStatus.Skipped, tour.Status);
        Assert.Equal(["trigger-btn"], restored);
    }

    [Fact]
    public void Pause_Alone_DoesNotRestoreFocus()
    {
        var tour = StartedWithFocus();
        tour.Pause();
        Assert.Equal(NavigatorTourStatus.Paused, tour.Status);
        Assert.Empty(tour.FocusRestoreLog);
        Assert.NotNull(tour.CurrentStep);
    }

    [Fact]
    public void FocusRestore_ThrowingHook_IsSwallowed_StateStillTransitions()
    {
        var tour = StartedWithFocus("h");
        tour.FocusRestore = _ => throw new InvalidOperationException("host focus blew up");

        tour.Stop(); // must not throw
        Assert.Equal(NavigatorTourStatus.Skipped, tour.Status);
        Assert.Equal(["h"], tour.FocusRestoreLog);
    }

    // ---- Escape = pause (S2-3) ----

    [Fact]
    public void EscapeSemantics_Pause_KeepsProgress_Stop_EndsTour()
    {
        var tour = StartedWithFocus();

        // Escape path (overlay HandleKeyDown calls Pause): keep progress.
        tour.Pause();
        Assert.Equal(NavigatorTourStatus.Paused, tour.Status);
        Assert.Equal(0, tour.Index);
        Assert.NotNull(tour.CurrentStep); // progress kept

        // Resume continues where it left off.
        tour.Resume();
        Assert.Equal(NavigatorTourStatus.Active, tour.Status);
        Assert.Equal(0, tour.Index);

        // Separate Stop ends the tour and discards the session.
        tour.Stop();
        Assert.Equal(NavigatorTourStatus.Skipped, tour.Status);
        Assert.Null(tour.CurrentStep);
    }

    // ---- Highlight rect contract (S2 highlight ring) ----

    [Fact]
    public void GetTargetRect_UnknownTarget_ReturnsNull()
    {
        var tour = new NavigatorTourService();
        Assert.Null(tour.GetTargetRect("missing", "home"));
        Assert.Null(tour.GetTargetRect("missing", null));
    }

    [Fact]
    public void GetTargetRect_RegisteredWithoutMeasurement_ReturnsNull()
    {
        var tour = new NavigatorTourService();
        tour.Register("a", "home", "A", null);
        Assert.Null(tour.GetTargetRect("a", "home"));
    }

    [Fact]
    public void GetTargetRect_AfterMeasurement_ReturnsRect_WrongRouteNull()
    {
        var tour = new NavigatorTourService();
        tour.Register("a", "home", "A", null);
        tour.SetTargetRect("a", "home", new NavigatorTargetRect(10, 20, 100, 40));

        var rect = tour.GetTargetRect("a", "home");
        Assert.NotNull(rect);
        Assert.Equal(10, rect.X);
        Assert.Equal(20, rect.Y);
        Assert.Equal(100, rect.Width);
        Assert.Equal(40, rect.Height);

        // Scoped: another route's same bare id has no measurement.
        Assert.Null(tour.GetTargetRect("a", "checkout"));
    }

    [Fact]
    public void GetTargetRect_ScopedPerRoute_DuplicateIds()
    {
        var tour = new NavigatorTourService();
        tour.Register("save", "r1", "Save r1", null);
        tour.Register("save", "r2", "Save r2", null);
        tour.SetTargetRect("save", "r2", new NavigatorTargetRect(1, 2, 3, 4));

        Assert.Null(tour.GetTargetRect("save", "r1"));
        Assert.NotNull(tour.GetTargetRect("save", "r2"));
    }

    [Fact]
    public void GetTargetRect_AfterScopedUnregister_ReturnsNull()
    {
        var tour = new NavigatorTourService();
        tour.Register("a", "home", "A", null);
        tour.SetTargetRect("a", "home", new NavigatorTargetRect(1, 2, 3, 4));
        Assert.True(tour.Unregister("a", "home"));
        Assert.Null(tour.GetTargetRect("a", "home"));
    }

    [Fact]
    public void SetTargetRect_UnknownTarget_ThrowsHumanMessage()
    {
        var tour = new NavigatorTourService();
        var ex = Assert.Throws<ArgumentException>(
            () => tour.SetTargetRect("ghost", "home", new NavigatorTargetRect(0, 0, 1, 1)));
        Assert.Contains("Unknown target", ex.Message);
    }
}
