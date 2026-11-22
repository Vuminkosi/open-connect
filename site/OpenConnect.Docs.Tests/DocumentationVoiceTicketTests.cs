using OpenConnect.Docs.Services.Voice;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationVoiceTicketTests
{
    private const string Session = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string OtherSession = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

    [Fact]
    public void TicketIsRandomSingleUseAndReturnsOnlyItsVisitorCredential()
    {
        var tickets = new DocumentationVoiceTickets();
        var first = tickets.Issue(Session, "visitor-one");
        var second = tickets.Issue(Session, "visitor-two");
        Assert.NotEqual(first, second);
        Assert.True(tickets.TryConsume(first, Session, out var credential));
        Assert.Equal("visitor-one", credential);
        Assert.False(tickets.TryConsume(first, Session, out _));
        Assert.True(tickets.TryConsume(second, Session, out credential));
        Assert.Equal("visitor-two", credential);
    }

    [Fact]
    public void WrongSessionConsumesTicketWithoutDisclosingCredential()
    {
        var tickets = new DocumentationVoiceTickets();
        var ticket = tickets.Issue(Session, "visitor-one");
        Assert.False(tickets.TryConsume(ticket, OtherSession, out var credential));
        Assert.Null(credential);
        Assert.False(tickets.TryConsume(ticket, Session, out _));
    }

    [Fact]
    public void ExpiredTicketIsRejected()
    {
        var clock = new TestClock();
        var tickets = new DocumentationVoiceTickets(clock);
        var ticket = tickets.Issue(Session, "visitor-one");
        clock.Now = clock.Now.AddSeconds(30);
        Assert.False(tickets.TryConsume(ticket, Session, out _));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
