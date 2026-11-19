using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Testing;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class VoiceTranscriptAdmissionTests
{
    [Theory]
    [InlineData("Create Todo to check weighbridge tomorrow")]
    [InlineData("Keep toto and waybridge exactly as spoken")]
    public async Task Default_policy_waits_for_close_and_preserves_final_text(string text)
    {
        var dispatcher = new RecordingDispatcher();
        using var services = Services(dispatcher).BuildServiceProvider();
        using var worker = new ScriptedVoiceSocket();
        using var browser = new ScriptedVoiceSocket();
        var relay = Relay(services, worker, browser);
        try
        {
            worker.Enqueue(Frame(text));
            await browser.WaitForSendAsync();
            Assert.Empty(dispatcher.Requests);
        }
        finally
        {
            worker.End();
            await relay.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(text, Assert.Single(dispatcher.Requests).Message);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Custom_policy_controls_early_timing_and_task_text_without_duplicate_fallback(bool early, bool transform)
    {
        var dispatcher = new RecordingDispatcher();
        var policy = new CustomPolicy(early, transform);
        var registrations = Services(dispatcher);
        registrations.AddSingleton<IVoiceTranscriptAdmissionPolicy>(policy);
        using var services = registrations.BuildServiceProvider();
        using var worker = new ScriptedVoiceSocket();
        using var browser = new ScriptedVoiceSocket();
        var relay = Relay(services, worker, browser);
        try
        {
            worker.Enqueue(Frame("ordinary application command"));
            await browser.WaitForSendAsync();
            Assert.Equal(early ? 1 : 0, dispatcher.Requests.Count);
            Assert.Equal("ordinary application command", policy.LastDecision);
            using var forwarded = JsonDocument.Parse(Assert.Single(browser.Sent));
            Assert.Equal("ordinary application command", forwarded.RootElement.GetProperty("text").GetString());
        }
        finally
        {
            worker.End();
            await relay.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(transform ? "custom: ordinary application command" : "ordinary application command", Assert.Single(dispatcher.Requests).Message);
        Assert.Equal(1, policy.Normalizations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Neutral_fallback_keeps_longest_authoritative_transcript_on_close(bool abrupt)
    {
        var dispatcher = new RecordingDispatcher();
        using var services = Services(dispatcher).BuildServiceProvider();
        using var worker = new ScriptedVoiceSocket();
        using var browser = new ScriptedVoiceSocket();
        var relay = Relay(services, worker, browser);
        try
        {
            worker.Enqueue(Frame("  keep this longer provider transcript  ", false));
            await browser.WaitForSendAsync();
            worker.Enqueue(Frame("transcript"));
            await browser.WaitForSendAsync();
            Assert.Empty(dispatcher.Requests);
        }
        finally
        {
            if (abrupt)
                worker.Fail();
            else
                worker.End();
            await relay.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal("keep this longer provider transcript", Assert.Single(dispatcher.Requests).Message);
    }

    [Fact]
    public async Task Partial_only_stream_does_not_create_authoritative_admission()
    {
        var dispatcher = new RecordingDispatcher();
        using var services = Services(dispatcher).BuildServiceProvider();
        using var worker = new ScriptedVoiceSocket();
        using var browser = new ScriptedVoiceSocket();
        var relay = Relay(services, worker, browser);
        try
        {
            worker.Enqueue(Frame("unfinished partial", false));
            await browser.WaitForSendAsync();
        }
        finally
        {
            worker.End();
            await relay.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Empty(dispatcher.Requests);
    }

    [Fact]
    public async Task Relay_cancellation_preserves_existing_propagation_without_close_fallback()
    {
        var dispatcher = new RecordingDispatcher();
        using var services = Services(dispatcher).BuildServiceProvider();
        using var worker = new ScriptedVoiceSocket();
        using var browser = new ScriptedVoiceSocket();
        using var cancellation = new CancellationTokenSource();
        var relay = Relay(services, worker, browser, cancellation.Token);
        try
        {
            worker.Enqueue(Frame("authoritative final awaiting stream close"));
            await browser.WaitForSendAsync();
            Assert.Empty(dispatcher.Requests);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(dispatcher.Requests);
        }
        finally
        {
            cancellation.Cancel();
            try
            { await relay.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class CustomPolicy(bool early, bool transform) : IVoiceTranscriptAdmissionPolicy
    {
        public string? LastDecision { get; private set; }
        public int Normalizations { get; private set; }
        public bool ShouldAdmitEarly(string authoritativeTranscript) { LastDecision = authoritativeTranscript; return early; }
        public string NormalizeTranscript(string transcript) { Normalizations++; return transform ? "custom: " + transcript : transcript; }
    }

    private static ServiceCollection Services(RecordingDispatcher dispatcher)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostTaskExecutionDispatcher>(dispatcher);
        services.AddSingleton<IHostTaskExecutionAuthorizer, NoExecution>();
        services.AddSingleton<IHostTaskExecutor, NoExecution>();
        return services;
    }
    private static string Frame(string text, bool final = true) => JsonSerializer.Serialize(new { type = "transcript", text, final });
    private static Task Relay(IServiceProvider services, ScriptedVoiceSocket worker, ScriptedVoiceSocket browser, CancellationToken cancellationToken = default)
    {
        var method = typeof(EndpointRouteBuilderExtensions).Assembly.GetType("OpenConnect.Hosting.AspNetCore.VoiceRealtimeWebSocketHandler")!
            .GetMethod("RelayWorkerToBrowserAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Task)method.Invoke(null, [worker, browser, new DefaultHttpContext { RequestServices = services },
            Ulid.NewUlid().ToString(), Guid.NewGuid().ToString(), 1L, Ulid.NewUlid(), Ulid.NewUlid(), cancellationToken, NullLogger.Instance])!;
    }
    private sealed class RecordingDispatcher : IHostTaskExecutionDispatcher
    {
        public ConcurrentQueue<TaskRequest> Requests { get; } = new();
        public Task<AdmissionResult> AcceptAsync(TaskRequest request, string key, string fingerprint, TenantId tenant, PrincipalId principal, CancellationToken cancellationToken, HostTaskOrigin origin = HostTaskOrigin.NonInteractive)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new AdmissionResult(ExecutionAdmissionStatus.Accepted, null));
        }
        public Task<TaskRecord?> CancelAsync(Ulid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class NoExecution : IHostTaskExecutor, IHostTaskExecutionAuthorizer
    {
        public Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken) => throw new InvalidOperationException("Relay admission must not execute tasks.");
        public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken) => throw new InvalidOperationException("Relay admission must not change authorization.");
    }
}
