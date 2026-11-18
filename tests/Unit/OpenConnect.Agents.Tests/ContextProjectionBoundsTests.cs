using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ContextProjectionBoundsTests
{
    [Theory]
    [InlineData(AgentContextSharingMode.TaskSummary, 4000, 10)]
    [InlineData(AgentContextSharingMode.RelevantHistory, 8000, 30)]
    [InlineData(AgentContextSharingMode.ApprovedArtifacts, 16000, 60)]
    [InlineData(AgentContextSharingMode.ScopedMemory, 4000, 15)]
    public void ApprovedProjectionTypes_WithinBounds_Passes(AgentContextSharingMode mode, int budget, int ttlMinutes)
    {
        ContextProjectionBounds.ValidateBounds(mode, budget, TimeSpan.FromMinutes(ttlMinutes));
    }

    [Fact]
    public void ExceedingMaxBudget_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContextProjectionBounds.ValidateBounds(AgentContextSharingMode.TaskSummary, 9000, TimeSpan.FromMinutes(5)));
        Assert.Contains("exceeds maximum allowable budget", ex.Message);
    }

    [Fact]
    public void NegativeBudget_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContextProjectionBounds.ValidateBounds(AgentContextSharingMode.TaskSummary, -1, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ModeNone_WithNonZeroBudget_ThrowsUnauthorizedAccessException()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            ContextProjectionBounds.ValidateBounds(AgentContextSharingMode.None, 100, TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(25 * 60)] // > 24 hours
    public void InvalidTtl_ThrowsArgumentOutOfRangeException(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContextProjectionBounds.ValidateBounds(AgentContextSharingMode.TaskSummary, 500, TimeSpan.FromMinutes(minutes)));
    }
}
