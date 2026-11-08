using OpenConnect.Navigator.Blazor;
using Xunit;

namespace OpenConnect.Navigator.Blazor.Tests;

public sealed class TourValidationTests
{
    [Fact]
    public void ValidateTour_EmptySteps_FailsWithHumanMessage()
    {
        var tour = new NavigatorTourService();
        var result = tour.ValidateTour([], "home");
        Assert.False(result.Ok);
        Assert.Single(result.Problems);
        Assert.Equal("The tour has no steps to show.", result.Problems[0].Message);
    }

    [Fact]
    public void ValidateTour_TooManySteps_Fails()
    {
        var tour = new NavigatorTourService();
        var steps = Enumerable.Range(0, 26)
            .Select(i => new NavigatorTourStep($"Title {i}", "Body."))
            .ToArray();
        var result = tour.ValidateTour(steps);
        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.StepIndex == -1);
    }

    [Fact]
    public void ValidateTour_UnknownTarget_Fails()
    {
        var tour = new NavigatorTourService();
        var result = tour.ValidateTour([new NavigatorTourStep("T", "B.", "missing")], "home");
        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Message.Contains("missing"));
    }

    [Fact]
    public void ValidateTour_CrossRouteTarget_Fails()
    {
        var tour = new NavigatorTourService();
        tour.Register("save", "checkout", "Save", null);
        var result = tour.ValidateTour([new NavigatorTourStep("T", "B.", "save")], "home");
        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Message.Contains("another screen"));
    }

    [Fact]
    public void ValidateTour_MatchingRoute_Passes()
    {
        var tour = new NavigatorTourService();
        tour.Register("save", "home", "Save", null);
        var result = tour.ValidateTour([new NavigatorTourStep("T", "B.", "save")], "home");
        Assert.True(result.Ok);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void StartTour_InvalidSteps_LeavesStateUntouched()
    {
        var tour = new NavigatorTourService();
        Assert.False(tour.StartTour([]));
        Assert.Equal(NavigatorTourStatus.Idle, tour.Status);
        Assert.Null(tour.CurrentStep);
        Assert.NotEmpty(tour.LastProblems);
    }
}
