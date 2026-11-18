using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
using OpenConnect.Samples.Workers.Host;
using Xunit;

namespace OpenConnect.Samples.Workers.TestKit;

public sealed class MetaTodoPrivacyEgressTests
{
    [Theory]
    [InlineData("Highlight the Add Task button.")]
    [InlineData("Show me how to delete a task.")]
    [InlineData("Can I complete a task later?")]
    public async Task Initial_acknowledgement_does_not_promise_a_mutation(string message)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var options = Options.Create(new MetaCloudOptions { TodoTenant = tenant.Value.ToString() });
        var todo = new TodoServiceClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), new CapturingChatClient(), gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            message, "acknowledgement")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" },
            CancellationToken.None);

        Assert.Equal("I’ll check your request. ", sessions.GetEvents(session.Value, 0).First(item => item.Type == "message").Data);
    }

    [Theory]
    [InlineData(HostTaskOrigin.MiniComposer, true)]
    [InlineData(HostTaskOrigin.NonInteractive, false)]
    public async Task Interactive_tools_follow_trusted_origin_not_keywords(HostTaskOrigin origin, bool expected)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var chat = new CapturingChatClient();
        var options = Options.Create(new MetaCloudOptions { TodoTenant = tenant.Value.ToString() });
        var todo = new TodoServiceClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            "Navigate to the Completed tab.", "interactive-origin")
        { Origin = origin, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.Equal(expected, chat.ToolNames.Contains("liveui_infer_plan"));
        Assert.DoesNotContain(chat.ToolNames, name => name.StartsWith("navigator.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Todo_agent_uses_task_scope_and_tokenizes_canaries_before_chat_provider_egress()
    {
        const string email = "todo.canary@example.test";
        const string phone = "+1-555-867-5309";
        const string card = "4532015112830366";
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var detector = new RulesSecurityDetector(vault);
        var gate = new EgressGate(new PrivacyFirewall(
            Options.Create(new PrivacyOptions()), new PolicyEngine(detector)));
        var chatClient = new CapturingChatClient();
        var options = Options.Create(new MetaCloudOptions { TodoTenant = tenant.Value.ToString() });
        var todo = new TodoServiceClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") },
            options);
        var executor = new MetaTodoTaskExecutor(
            new TestAgentRegistry(), chatClient, gate, todo, sessions, new NoopInteractionBroker(),
            options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(
            new TaskId(Ulid.NewUlid()), tenant, principal, session,
            $"Handle this contact: {email}, {phone}, payment card {card}.", "privacy-canary"),
            CancellationToken.None);

        Assert.NotEmpty(chatClient.OutboundMessages);
        var outbound = string.Join("\n", chatClient.OutboundMessages.Select(message => message.Text));
        Assert.DoesNotContain(email, outbound, StringComparison.Ordinal);
        Assert.DoesNotContain(phone, outbound, StringComparison.Ordinal);
        Assert.DoesNotContain(card, outbound, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B1-", outbound, StringComparison.OrdinalIgnoreCase);

        var tokens = System.Text.RegularExpressions.Regex.Matches(outbound, @"\[OC_[A-F0-9]{32}\]")
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(3, tokens.Length);
        var scope = new PrivacyScope(tenant, principal, session, "redaction");
        var restored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            var value = await vault.ResolveAsync(token, scope);
            Assert.NotNull(value);
            restored.Add(value);
        }
        Assert.True(new[] { email, phone, card }.ToHashSet(StringComparer.Ordinal).SetEquals(restored));
    }

    [Fact]
    public async Task Follow_up_receives_previous_turn_from_the_same_interactive_session()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var chat = new CapturingChatClient();
        var options = Options.Create(new MetaCloudOptions { TodoTenant = tenant.Value.ToString() });
        var todo = new TodoServiceClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            "Highlight the task list.", "first")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);
        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            "Highlight it again.", "follow-up")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.Contains(chat.OutboundMessages, message => message.Role == ChatRole.User && message.Text == "Highlight the task list.");
        Assert.Contains(chat.OutboundMessages, message => message.Role == ChatRole.Assistant && message.Text == "Handled safely.");
        Assert.Contains(chat.OutboundMessages, message => message.Role == ChatRole.User && message.Text == "Highlight it again.");
    }

    [Fact]
    public async Task Task_follow_up_can_read_todos_without_enabling_mutation_tools()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var chat = new CapturingChatClient();
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        async Task Send(string text) => await executor.ExecuteAsync(
            new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session, text, Guid.NewGuid().ToString())
            { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);
        await Send("Which one should I do next?");
        Assert.DoesNotContain("todo.list", chat.ToolNames);
        await Send("Show my active tasks.");
        var otherSession = sessions.Create(tenant, principal).Id;
        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, otherSession,
            "Which one should I do next?", "other-session")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "other-client" }, CancellationToken.None);
        Assert.DoesNotContain("todo.list", chat.ToolNames);
        await Send("Which one should I do next?");
        Assert.Contains("todo.list", chat.ToolNames);
        Assert.DoesNotContain(chat.ToolNames, name => name is "todo.create" or "todo.complete" or "todo.update" or "todo.delete");
        await Send("Highlight it.");
        Assert.Contains("todo.list", chat.ToolNames);

        await Send("Tell me a joke.");
        Assert.DoesNotContain("todo.list", chat.ToolNames);
        await Send("Which one should I do next?");
        Assert.DoesNotContain("todo.list", chat.ToolNames);
    }

    [Theory]
    [InlineData("Show my tasks.")]
    [InlineData("How do I delete a task?")]
    [InlineData("Do not complete this task.")]
    [InlineData("Delete a task?")]
    [InlineData("Complete the task but do not do it.")]
    public async Task Read_only_task_requests_do_not_expose_mutations_even_when_enabled(string message)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var chat = new CapturingChatClient();
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            message, Guid.NewGuid().ToString())
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.Contains("todo.list", chat.ToolNames);
        Assert.DoesNotContain(chat.ToolNames, name => name is "todo.create" or "todo.complete" or "todo.update" or "todo.delete");
    }

    [Theory]
    [InlineData("Create a task called report", "todo.create", true, true, true)]
    [InlineData("Complete the task report", "todo.complete", true, true, true)]
    [InlineData("Delete the task report", "todo.delete", true, true, true)]
    [InlineData("Update the task \"report\" to completed", "todo.update", true, true, true)]
    [InlineData("Update the task report", "todo.update", true, true, false)]
    [InlineData("Delete the task report", "todo.delete", false, true, false)]
    [InlineData("Delete the task report", "todo.delete", true, false, false)]
    public async Task Explicit_task_commands_expose_only_the_requested_mutation(string message, string expectedTool,
        bool allowMutations, bool approved, bool exposed)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var chat = new CapturingChatClient();
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), TodoInternalToken = "test-token", AllowMutations = allowMutations, MutationApprovalGranted = approved });
        var handler = new RecordingTodoHandler(new TodoServiceRecord(Guid.NewGuid(), "report", null, false, 1, DateTime.UtcNow, null));
        var todo = new TodoServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            message, Guid.NewGuid().ToString())
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.Equal(exposed ? new[] { expectedTool } : [],
            chat.ToolNames.Where(name => name is "todo.create" or "todo.complete" or "todo.update" or "todo.delete"));
    }

    [Theory]
    [InlineData("todo.delete", "Delete the task")]
    [InlineData("todo.complete", "Complete the task")]
    [InlineData("todo.update", "Update the task")]
    public async Task Model_cannot_mutate_a_different_todo_than_the_user_specified(string tool, string command)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        var handler = new RecordingTodoHandler();
        var chat = new InvokingChatClient(tool, other, true);
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), TodoInternalToken = "test-token", AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            $"{command} {target:D}{(tool == "todo.update" ? " to completed" : "")}", "bound-mutation")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.True(chat.Invoked);
        Assert.NotNull(chat.InvocationError);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Model_cannot_change_the_explicitly_requested_completion_state()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var target = Guid.NewGuid();
        var handler = new RecordingTodoHandler();
        var chat = new InvokingChatClient("todo.update", target, false);
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), TodoInternalToken = "test-token", AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            $"Update the task {target:D} to completed", "bound-status")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.True(chat.Invoked);
        Assert.NotNull(chat.InvocationError);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Unique_requested_title_allows_only_its_resolved_record()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var target = Guid.NewGuid();
        var handler = new RecordingTodoHandler(new TodoServiceRecord(target, "report", null, false, 1, DateTime.UtcNow, null));
        var chat = new InvokingChatClient("todo.delete", target);
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), TodoInternalToken = "test-token", AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            "Delete the task report", "bound-title")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.True(chat.Invoked);
        Assert.Null(chat.InvocationError);
        Assert.EndsWith(target.ToString("D"), Assert.Single(handler.Writes)!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("todo.complete", "Complete the task")]
    [InlineData("todo.update", "Update the task")]
    public async Task Explicit_record_and_status_allow_the_requested_write(string tool, string command)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var target = Guid.NewGuid();
        var handler = new RecordingTodoHandler();
        var chat = new InvokingChatClient(tool, target, true);
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), TodoInternalToken = "test-token", AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            $"{command} {target:D}{(tool == "todo.update" ? " to completed" : "")}", "authorized-write")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.True(chat.Invoked);
        Assert.Null(chat.InvocationError);
        Assert.EndsWith(target.ToString("D"), Assert.Single(handler.Writes)!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("todo.delete", "Delete the task report")]
    [InlineData("todo.complete", "Complete the task report")]
    [InlineData("todo.update", "Update the task \"report\" to completed")]
    public async Task Model_cannot_substitute_another_id_for_a_named_task(string tool, string message)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        var handler = new RecordingTodoHandler(
            new TodoServiceRecord(target, "report", null, false, 1, DateTime.UtcNow, null),
            new TodoServiceRecord(other, "other", null, false, 1, DateTime.UtcNow, null));
        var chat = new InvokingChatClient(tool, other, true);
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), TodoInternalToken = "test-token", AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            message, "bound-name")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.True(chat.Invoked);
        Assert.NotNull(chat.InvocationError);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Ambiguous_requested_title_exposes_no_mutation_tool()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var handler = new RecordingTodoHandler(
            new TodoServiceRecord(Guid.NewGuid(), "report", null, false, 1, DateTime.UtcNow, null),
            new TodoServiceRecord(Guid.NewGuid(), "report", null, false, 1, DateTime.UtcNow, null));
        var chat = new CapturingChatClient();
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), TodoInternalToken = "test-token", AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            "Delete the task report", "ambiguous-title")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.DoesNotContain("todo.delete", chat.ToolNames);
        Assert.Empty(handler.Writes);
        Assert.Contains(sessions.GetEvents(session.Value, 0), item => item.Type == "message" &&
            item.Data.Contains("could not identify a unique task", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("different", null, "Medium", false)]
    [InlineData("report", "invented description", "Medium", false)]
    [InlineData("report", null, "Urgent", false)]
    [InlineData("report", null, "Medium", true)]
    public async Task Model_cannot_change_or_invent_task_creation_fields(string title, string? description, string priority, bool includeDueDate)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var handler = new RecordingTodoHandler();
        var chat = new CreateInvokingChatClient(title, description, priority,
            includeDueDate ? new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null);
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), TodoInternalToken = "test-token", AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            "Create a task called report", "bound-create")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.True(chat.Invoked);
        Assert.NotNull(chat.InvocationError);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData("Create a task called report", "Medium")]
    [InlineData("Create a task called \"report\" with urgent priority", "Urgent")]
    public async Task Explicit_creation_fields_are_preserved_in_the_write(string message, string priority)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var handler = new RecordingTodoHandler();
        var chat = new CreateInvokingChatClient("report", null, priority, null);
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), TodoInternalToken = "test-token", AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            message, "authorized-create")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.True(chat.Invoked);
        Assert.Null(chat.InvocationError);
        Assert.Single(handler.Writes);
        using var payload = System.Text.Json.JsonDocument.Parse(Assert.IsType<string>(handler.LastWriteBody));
        Assert.Equal("report", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal(priority, payload.RootElement.GetProperty("priority").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, payload.RootElement.GetProperty("description").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, payload.RootElement.GetProperty("dueDateUtc").ValueKind);
    }

    [Theory]
    [InlineData("Create a task")]
    [InlineData("Create a task called report with urgent priority")]
    [InlineData("Create a task called report due tomorrow")]
    public async Task Ambiguous_creation_requests_clarify_without_a_write(string message)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var handler = new RecordingTodoHandler();
        var chat = new CreateInvokingChatClient("report", null, "Medium", null);
        var options = Options.Create(new MetaCloudOptions
        { TodoTenant = tenant.Value.ToString(), TodoInternalToken = "test-token", AllowMutations = true, MutationApprovalGranted = true });
        var todo = new TodoServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
            message, "ambiguous-create")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        Assert.False(chat.Invoked);
        Assert.Empty(handler.Writes);
        Assert.Contains(sessions.GetEvents(session.Value, 0), item => item.Type == "message" &&
            item.Data.Contains("Specify the task title", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Conversation_context_is_session_scoped_and_passes_through_the_privacy_gate()
    {
        const string email = "history.canary@example.test";
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var firstSession = sessions.Create(tenant, principal).Id;
        var secondSession = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var chat = new CapturingChatClient();
        var options = Options.Create(new MetaCloudOptions { TodoTenant = tenant.Value.ToString() });
        var todo = new TodoServiceClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        async Task Send(SessionId session, string message) => await executor.ExecuteAsync(
            new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session, message, Guid.NewGuid().ToString())
            { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        await Send(firstSession, $"Find tasks for {email}.");
        await Send(firstSession, "Show those again.");
        var outbound = string.Join("\n", chat.OutboundMessages.Select(message => message.Text));
        Assert.DoesNotContain(email, outbound, StringComparison.Ordinal);
        Assert.Contains("[OC_", outbound, StringComparison.Ordinal);
        Assert.Contains("Show those again.", outbound, StringComparison.Ordinal);

        await Send(secondSession, "Show my tasks.");
        Assert.DoesNotContain(chat.OutboundMessages, message => message.Text?.Contains("[OC_", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(chat.OutboundMessages, message => message.Text == "Show those again.");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(
            new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, new PrincipalId(Ulid.NewUlid()), firstSession,
                "Show those again.", "other-principal")
            { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None));
    }

    [Fact]
    public async Task Conversation_history_keeps_only_the_most_recent_four_completed_turns()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var chat = new CapturingChatClient();
        var options = Options.Create(new MetaCloudOptions { TodoTenant = tenant.Value.ToString() });
        var todo = new TodoServiceClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        for (var index = 0; index < 6; index++)
            await executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal, session,
                $"Message number {index}", $"turn-{index}")
            { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);

        var userMessages = chat.OutboundMessages.Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToArray();
        Assert.Equal(new[] { "Message number 1", "Message number 2", "Message number 3", "Message number 4", "Message number 5" }, userMessages);
    }

    [Fact]
    public async Task Overlapping_turns_for_one_session_are_serialized_and_include_the_first_reply()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(tenant, principal).Id;
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var chat = new BlockingChatClient();
        var options = Options.Create(new MetaCloudOptions { TodoTenant = tenant.Value.ToString() });
        var todo = new TodoServiceClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") }, options);
        var executor = new MetaTodoTaskExecutor(new TestAgentRegistry(), chat, gate, todo, sessions,
            new NoopInteractionBroker(), options, NullLogger<MetaTodoTaskExecutor>.Instance);

        Task Send(string text) => executor.ExecuteAsync(new HostTaskExecution(new TaskId(Ulid.NewUlid()), tenant, principal,
            session, text, Guid.NewGuid().ToString())
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "bound-client" }, CancellationToken.None);
        var first = Send("First request");
        await chat.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Send("Follow up");
        Assert.Equal(1, chat.Calls);
        chat.ReleaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, chat.Calls);
        Assert.Contains(chat.LastMessages, message => message.Role == ChatRole.User && message.Text == "First request");
        Assert.Contains(chat.LastMessages, message => message.Role == ChatRole.Assistant && message.Text == "First response");
    }

    private sealed class TestAgentRegistry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("worker-sample-direct"), "OpenConnect assistant");
        public AgentDefinition? Get(AgentId id) => id == Definition.Id ? Definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class NoopInteractionBroker : IClientInteractionBroker
    {
        public Task<ClientInteractionResponse?> RequestAsync(SessionId sessionId, ClientInteractionRequest request,
            string expectedResultKind, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<ClientInteractionResponse?>(null);
    }

    private sealed class RecordingTodoHandler(params TodoServiceRecord[] records) : HttpMessageHandler
    {
        public List<Uri?> Writes { get; } = [];
        public string? LastWriteBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                Writes.Add(request.RequestUri);
                if (request.Content is not null)
                    LastWriteBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            if (request.Method == HttpMethod.Put)
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(new TodoServiceRecord(
                    Guid.Parse(request.RequestUri!.Segments[^1]), "report", null, true, 1, DateTime.UtcNow, null))
                };
            if (request.Method == HttpMethod.Post)
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(new TodoServiceRecord(
                    Guid.NewGuid(), "report", null, false, 1, DateTime.UtcNow, null))
                };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = System.Net.Http.Json.JsonContent.Create(records) };
        }
    }

    private sealed class InvokingChatClient(string toolName, Guid target, bool? completed = null) : IChatClient
    {
        public bool Invoked { get; private set; }
        public Exception? InvocationError { get; private set; }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var function = options?.Tools?.OfType<AIFunction>().Single(tool => tool.Name == toolName);
            if (function is not null)
            {
                Invoked = true;
                var arguments = new Dictionary<string, object?> { ["todoId"] = target.ToString("D") };
                if (toolName == "todo.update")
                    arguments["isCompleted"] = completed;
                try
                { await function.InvokeAsync(new AIFunctionArguments(arguments), cancellationToken); }
                catch (Exception exception) { InvocationError = exception; }
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Handled safely."));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CreateInvokingChatClient(string title, string? description, string priority, DateTime? dueDate) : IChatClient
    {
        public bool Invoked { get; private set; }
        public Exception? InvocationError { get; private set; }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var function = options?.Tools?.OfType<AIFunction>().SingleOrDefault(tool => tool.Name == "todo.create");
            if (function is not null)
            {
                Invoked = true;
                var arguments = new AIFunctionArguments(new Dictionary<string, object?>
                { ["title"] = title, ["description"] = description, ["priority"] = priority, ["dueDateUtc"] = dueDate });
                try
                { await function.InvokeAsync(arguments, cancellationToken); }
                catch (Exception exception) { InvocationError = exception; }
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Handled safely."));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatMessage> OutboundMessages { get; private set; } = [];
        public List<string> ToolNames { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutboundMessages = messages.ToList();
            ToolNames = options?.Tools?.OfType<AIFunction>().Select(tool => tool.Name).ToList() ?? [];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Handled safely.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class BlockingChatClient : IChatClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ChatMessage> LastMessages { get; private set; } = [];

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstStarted.SetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "First response"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
