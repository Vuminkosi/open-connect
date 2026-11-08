using OpenConnect.Navigator.Blazor;
using Xunit;

namespace OpenConnect.Navigator.Blazor.Tests;

public sealed class TourStateTests
{
    private static NavigatorTourService Started()
    {
        var tour = new NavigatorTourService();
        tour.Register("a", "home", "First target", null);
        Assert.True(tour.StartTour([
            new NavigatorTourStep("One", "First.", "a") { },
            new NavigatorTourStep("Two", "Second."),
        ], "home"));
        return tour;
    }

    [Fact]
    public void StartTour_SetsActiveAtFirstStep()
    {
        var tour = Started();
        Assert.Equal(NavigatorTourStatus.Active, tour.Status);
        Assert.Equal(0, tour.Index);
        Assert.Equal("One", tour.CurrentStep?.Title);
    }

    [Fact]
    public void Next_Advances_And_FinishesOnLast()
    {
        var tour = Started();
        tour.Next();
        Assert.Equal(1, tour.Index);
        Assert.Equal("Two", tour.CurrentStep?.Title);
        tour.Next();
        Assert.Equal(NavigatorTourStatus.Finished, tour.Status);
        Assert.Null(tour.CurrentStep);
    }

    [Fact]
    public void Back_ClampsAtFirst()
    {
        var tour = Started();
        tour.Back();
        Assert.Equal(0, tour.Index);
        tour.Next();
        tour.Back();
        Assert.Equal(0, tour.Index);
    }

    [Fact]
    public void GoTo_Clamps_OutOfRange()
    {
        var tour = Started();
        tour.GoTo(99);
        Assert.Equal(1, tour.Index);
        tour.GoTo(-5);
        Assert.Equal(0, tour.Index);
    }

    [Fact]
    public void Pause_Resume_Stop_Lifecycle()
    {
        var tour = Started();
        var notifications = 0;
        tour.Changed += () => notifications++;

        tour.Pause();
        Assert.Equal(NavigatorTourStatus.Paused, tour.Status);
        Assert.NotNull(tour.CurrentStep);

        tour.Resume();
        Assert.Equal(NavigatorTourStatus.Active, tour.Status);

        tour.Stop();
        Assert.Equal(NavigatorTourStatus.Skipped, tour.Status);
        Assert.Null(tour.CurrentStep);
        Assert.True(notifications >= 3);
    }

    [Fact]
    public void Reset_ReturnsToIdle()
    {
        var tour = Started();
        tour.Reset();
        Assert.Equal(NavigatorTourStatus.Idle, tour.Status);
        Assert.Empty(tour.Steps);
        Assert.Null(tour.CurrentStep);
    }

    [Fact]
    public void RenderTextTour_FormatsSteps()
    {
        var text = NavigatorTourService.RenderTextTour([
            new NavigatorTourStep("Hello", "World."),
            new NavigatorTourStep("Bye", "Done."),
        ]);
        Assert.Contains("App tour — 2 steps", text);
        Assert.Contains("Step 1 of 2: Hello", text);
        Assert.Contains("Step 2 of 2: Bye", text);
    }

    [Fact]
    public void RenderTextTour_Empty_ReturnsFallback()
    {
        Assert.Equal("No tour steps available.", NavigatorTourService.RenderTextTour([]));
    }

    [Fact]
    public void ProgressText_FormatsCaption()
    {
        Assert.Equal("Step 2 of 5", NavigatorTourService.ProgressText(1, 5));
    }
}
