using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Components.Blazor;
using OpenConnect.Components.Contracts;
using Xunit;

namespace OpenConnect.Blazor.Tests;

/// <summary>
/// Plain component-logic tests for the provider/composer state machine and the
/// safe renderer allow-list. No bUnit is required: <see cref="ComposerSession"/>
/// is UI-agnostic by design and the Blazor components are thin views over it.
/// </summary>
public sealed class ComposerSessionTests
{
    private static EventRecord Event(long sequence, string type, string data)
        => new(new SessionId(Ulid.NewUlid()), sequence, type, data);

    [Fact]
    public void AttachSession_resets_cursor_messages_and_proposals()
    {
        var session = new ComposerSession();
        session.AttachSession("ALPHA");
        session.BeginSend("hello");
        session.ApplyEvent(Event(1, "message.delta", "Hi"));

        session.AttachSession("BETA");

        Assert.Equal("BETA", session.SessionId);
        Assert.Equal(0, session.LastSequence);
        Assert.Empty(session.Messages);
        Assert.Empty(session.Proposals);
        Assert.Equal(string.Empty, session.StreamingText);
    }

    [Fact]
    public void AttachSession_preserves_a_pending_first_user_turn_until_the_gateway_reply_arrives()
    {
        var session = new ComposerSession();
        session.BeginSend("Explain what I am looking at.");

        session.AttachSession("FIRST-SESSION");

        Assert.Equal(ComposerConnectionState.Streaming, session.Connection);
        Assert.Equal("Explain what I am looking at.", Assert.Single(session.Messages).Text);
        session.ApplyEvent(Event(1, "message", "The task list is visible."));
        session.ApplyEvent(Event(2, "task.completed", "{}"));

        Assert.Equal(ComposerConnectionState.Idle, session.Connection);
        Assert.Collection(session.Messages,
            message => Assert.Equal(("user", "Explain what I am looking at."), (message.Role, message.Text)),
            message => Assert.Equal(("assistant", "The task list is visible."), (message.Role, message.Text)));
    }

    [Fact]
    public void SpeechPlayback_completion_is_epoch_fenced_and_returns_to_on_state()
    {
        var session = new ComposerSession();

        var completedEpoch = session.BeginSpeechPlayback();
        Assert.True(session.EndSpeechPlayback(completedEpoch));
        Assert.Equal(ComposerSpeechState.On, session.SpeechState);
        Assert.False(session.EndSpeechPlayback(completedEpoch));

        var interruptedEpoch = session.BeginSpeechPlayback();
        session.TriggerBargeIn();
        Assert.False(session.EndSpeechPlayback(interruptedEpoch));
        Assert.Equal(ComposerSpeechState.On, session.SpeechState);
    }

    [Fact]
    public void Client_interaction_cancellation_is_correlated_and_cleared_on_new_session()
    {
        var session = new ComposerSession();
        session.AttachSession("ALPHA");
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "highlight", "todo.list");
        session.ApplyEvent(Event(1, ClientInteractionEventTypes.Request,
            System.Text.Json.JsonSerializer.Serialize(request, OpenConnectHttpJson.CreateOptions())));

        session.ApplyEvent(Event(2, ClientInteractionEventTypes.Cancellation,
            System.Text.Json.JsonSerializer.Serialize(new ClientInteractionCancellation(request.CorrelationId), OpenConnectHttpJson.CreateOptions())));

        Assert.Equal(request.CorrelationId, session.PendingClientInteractionCancellationId);
        session.AttachSession("BETA");
        Assert.Null(session.PendingClientInteractionCancellationId);
    }

    [Fact]
    public void Deltas_stream_then_complete_into_single_message()
    {
        var session = new ComposerSession();
        session.AttachSession("S1");
        session.BeginSend("question");

        Assert.Equal(ComposerConnectionState.Streaming, session.Connection);
        session.ApplyEvent(Event(1, "message.delta", "Hel"));
        session.ApplyEvent(Event(2, "message.delta", "lo"));
        Assert.Equal("Hello", session.StreamingText);

        session.ApplyEvent(Event(3, "message.completed", string.Empty));

        Assert.Equal(string.Empty, session.StreamingText);
        Assert.Equal(2, session.Messages.Count);
        Assert.Equal("Hello", session.Messages[1].Text);
        Assert.Equal(ComposerMessageState.Received, session.Messages[1].State);
        Assert.Equal(3, session.LastSequence);
    }

    [Fact]
    public void Failure_records_error_and_failed_message()
    {
        var session = new ComposerSession();
        session.AttachSession("S1");
        session.BeginSend("question");
        session.ApplyEvent(Event(1, "message.delta", "part"));

        session.ApplyEvent(Event(2, "task.failed", "Model unavailable"));

        Assert.Equal(ComposerConnectionState.Error, session.Connection);
        Assert.Equal("Model unavailable", session.LastError);
        Assert.Contains(session.Messages, static m => m.State == ComposerMessageState.Failed);
    }

    [Fact]
    public void CancelStreaming_marks_partial_text_cancelled()
    {
        var session = new ComposerSession();
        session.AttachSession("S1");
        session.BeginSend("question");
        session.ApplyEvent(Event(1, "message.delta", "partial"));

        session.CancelStreaming();

        Assert.Equal(ComposerConnectionState.Idle, session.Connection);
        Assert.Contains(session.Messages, static m => m.State == ComposerMessageState.Cancelled);
        Assert.Equal(string.Empty, session.StreamingText);
    }

    [Fact]
    public void Unknown_event_types_are_ignored_without_render_change()
    {
        var session = new ComposerSession();
        session.AttachSession("S1");

        var changed = session.ApplyEvent(Event(1, "telemetry.heartbeat", "{}"));

        Assert.False(changed);
    }

    [Fact]
    public void LiveUI_request_event_is_retained_for_the_blazor_client_bridge()
    {
        var session = new ComposerSession();
        session.AttachSession("S1");
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "execute", "todo.completed", "SelectTab");

        Assert.True(session.ApplyEvent(Event(1, ClientInteractionEventTypes.Request,
            System.Text.Json.JsonSerializer.Serialize(request))));

        Assert.Equal(request, session.PendingClientInteraction);
        Assert.Empty(session.Messages);
        Assert.Equal(string.Empty, session.StreamingText);
    }

    [Fact]
    public void Valid_proposal_is_kept_and_invalid_name_is_dropped()
    {
        var session = new ComposerSession();
        session.AttachSession("S1");

        session.ApplyEvent(Event(1, "component.proposal", """{"name":"order-card","parameters":{"id":"7"}}"""));
        session.ApplyEvent(Event(2, "component.proposal", """{"name":"<script>","parameters":{}}"""));

        var proposal = Assert.Single(session.Proposals);
        Assert.Equal("order-card", proposal.Name);
    }

    [Fact]
    public void Replay_of_seen_sequences_is_idempotent()
    {
        var session = new ComposerSession();
        session.AttachSession("S1");
        session.BeginSend("question");
        Assert.True(session.ApplyEvent(Event(1, "message.delta", "Hel")));
        Assert.True(session.ApplyEvent(Event(2, "message.delta", "lo")));
        Assert.Equal("Hello", session.StreamingText);

        // Reconnect replay of already-seen sequences must not duplicate text.
        Assert.False(session.ApplyEvent(Event(2, "message.delta", "lo")));
        Assert.False(session.ApplyEvent(Event(1, "message.delta", "Hel")));
        Assert.Equal("Hello", session.StreamingText);
        Assert.Equal(2, session.LastSequence);
        Assert.Single(session.Messages); // only the optimistic user message

        // A stale completion must not materialize a duplicate assistant message.
        Assert.True(session.ApplyEvent(Event(3, "message.completed", string.Empty)));
        Assert.Equal(2, session.Messages.Count);
        Assert.False(session.ApplyEvent(Event(3, "message.completed", string.Empty)));
        Assert.Equal(2, session.Messages.Count);
        Assert.Equal(3, session.LastSequence);
    }

    [Fact]
    public void Sequence_zero_is_processed_as_live_event_without_moving_cursor()
    {
        var session = new ComposerSession();
        session.AttachSession("S1");
        session.BeginSend("question");

        Assert.True(session.ApplyEvent(Event(0, "message.delta", "Hi")));
        Assert.Equal("Hi", session.StreamingText);
        Assert.Equal(0, session.LastSequence);
    }

    [Fact]
    public void MarkReconnecting_and_MarkError_track_connection()
    {
        var session = new ComposerSession();
        session.AttachSession("S1");

        session.MarkReconnecting("interrupted");
        Assert.Equal(ComposerConnectionState.Reconnecting, session.Connection);

        session.MarkError("down");
        Assert.Equal(ComposerConnectionState.Error, session.Connection);
        Assert.Equal("down", session.LastError);
    }
}

public sealed class ComponentProposalValidationTests
{
    private static bool Registered(string name) => name == "order-card";

    [Fact]
    public void Accepts_registered_name_with_primitive_parameters()
    {
        var proposal = new ComponentProposal("order-card", """{"id":"7","count":3,"active":true}""");
        Assert.Null(proposal.Validate(Registered));
    }

    [Fact]
    public void Rejects_unregistered_names()
    {
        var proposal = new ComponentProposal("unknown-widget", null);
        Assert.NotNull(proposal.Validate(Registered));
    }

    [Fact]
    public void Rejects_markup_and_nested_json()
    {
        Assert.NotNull(new ComponentProposal("order-card", """{"label":"<b>hi</b>"}""").Validate(Registered));
        Assert.NotNull(new ComponentProposal("order-card", """{"nested":{"a":1}}""").Validate(Registered));
        Assert.NotNull(new ComponentProposal("order-card", """[1,2]""").Validate(Registered));
        Assert.NotNull(new ComponentProposal("order-card", "not-json").Validate(Registered));
    }

    [Fact]
    public void Registry_resolves_only_registered_types()
    {
        var registry = new OpenConnectComponentRegistry();
        var descriptor = new OpenConnectComponentDescriptor("order-card", "Order", "Shows an order.");
        registry.Register(typeof(FakeComponent), descriptor);

        Assert.True(registry.IsRegistered("order-card"));
        Assert.False(registry.IsRegistered("other"));
        Assert.True(registry.TryResolve("order-card", out var type, out var resolved));
        Assert.Equal(typeof(FakeComponent), type);
        Assert.Equal(descriptor, resolved);
    }

    [Fact]
    public void Descriptor_rejects_markup_names()
    {
        Assert.False(OpenConnectComponentDescriptor.IsValidName("<script>"));
        Assert.False(OpenConnectComponentDescriptor.IsValidName(string.Empty));
        Assert.True(OpenConnectComponentDescriptor.IsValidName("order-card_2"));
    }

    private sealed class FakeComponent : Microsoft.AspNetCore.Components.ComponentBase;
}
