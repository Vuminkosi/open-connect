using OpenConnect.Navigator.Blazor;
using Xunit;

namespace OpenConnect.Navigator.Blazor.Tests;

public sealed class RegistryTests
{
    [Fact]
    public void Register_ReturnsRevisionOne_ForNewTarget()
    {
        var tour = new NavigatorTourService();
        var target = tour.Register("save", "checkout", "Save", "Saves the form.");
        Assert.Equal(1, target.Revision);
        Assert.Equal("Save", tour.Get("save")?.Label);
    }

    [Fact]
    public void Register_SameId_BumpsRevisionWithoutDuplicating()
    {
        var tour = new NavigatorTourService();
        tour.Register("save", "checkout", "Save", null);
        var second = tour.Register("save", "checkout", "Save now", null);
        Assert.Equal(2, second.Revision);
        Assert.Equal(1, tour.Size);
        Assert.Equal("Save now", tour.Get("save")?.Label);
    }

    [Fact]
    public void Register_EmptyTargetId_ThrowsHumanMessage()
    {
        var tour = new NavigatorTourService();
        var ex = Assert.Throws<ArgumentException>(() => tour.Register("  ", "route", "Label", null));
        Assert.Contains("must not be empty", ex.Message);
        Assert.DoesNotContain("targetId", ex.Message);
    }

    [Fact]
    public void Unregister_RemovesEntry()
    {
        var tour = new NavigatorTourService();
        tour.Register("a", "r", "A", null);
        Assert.True(tour.Unregister("a"));
        Assert.False(tour.Unregister("a"));
        Assert.Null(tour.Get("a"));
    }

    [Fact]
    public void ListForRoute_ReturnsOnlyOwnedTargetsSorted()
    {
        var tour = new NavigatorTourService();
        tour.Register("b", "r1", "B", null);
        tour.Register("a", "r1", "A", null);
        tour.Register("c", "r2", "C", null);
        var listed = tour.ListForRoute("r1").Select(t => t.TargetId).ToArray();
        Assert.Equal(["a", "b"], listed);
    }

    [Fact]
    public void UnregisterRoute_RemovesOnlyOwnedTargets()
    {
        var tour = new NavigatorTourService();
        tour.Register("a", "r1", "A", null);
        tour.Register("b", "r2", "B", null);
        Assert.Equal(1, tour.UnregisterRoute("r1"));
        Assert.Null(tour.Get("a"));
        Assert.NotNull(tour.Get("b"));
    }
}
