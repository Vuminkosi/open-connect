using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Samples.Workers.Host;
using OpenConnect.Testing;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class TodoVoiceAdmissionTests
{
    [Theory]
    [InlineData("Create toto to check weighbridge tomorrow", "Create Todo to check weighbridge tomorrow", true)]
    [InlineData("Create toto to check waybridge tomorrow", "Create Todo to check weighbridge tomorrow", false)]
    [InlineData("Keep weybridge and weigh bridge", "Keep weighbridge and weighbridge", false)]
    public async Task Sample_registration_preserves_legacy_timing_and_normalization_through_relay(string text, string normalized, bool early)
    {
        var registrations = Registrations();
        var executor = new RecordingExecutor();
        registrations.AddSingleton<IHostTaskExecutor>(executor);
        registrations.AddSingleton<IHostTaskExecutionAuthorizer>(executor);
        await using var services = registrations.BuildServiceProvider();
        var policy = services.GetRequiredService<IVoiceTranscriptAdmissionPolicy>();
        Assert.Equal(early, policy.ShouldAdmitEarly(text));
        var workers = services.GetServices<IHostedService>().ToArray();
        foreach (var hosted in workers)
            await hosted.StartAsync(CancellationToken.None);
        using var upstream = new ScriptedVoiceSocket();
        using var browser = new ScriptedVoiceSocket();
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessions = services.GetRequiredService<InMemorySessionStore>();
        var session = sessions.Create(tenant, principal);
        var method = typeof(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions).Assembly
            .GetType("OpenConnect.Hosting.AspNetCore.VoiceRealtimeWebSocketHandler")!
            .GetMethod("RelayWorkerToBrowserAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var relay = (Task)method.Invoke(null, [upstream, browser, new DefaultHttpContext { RequestServices = services },
            session.Id.Value.ToString(), Guid.NewGuid().ToString(), 1L, tenant.Value, principal.Value, CancellationToken.None, NullLogger.Instance])!;
        try
        {
            upstream.Enqueue(JsonSerializer.Serialize(new { type = "transcript", text, final = true }));
            await browser.WaitForSendAsync();
            Assert.Equal(early ? 1 : 0, sessions.GetEvents(session.Id.Value, 0).Count(x => x.Type == "task.accepted"));
            upstream.End();
            await relay.WaitAsync(TimeSpan.FromSeconds(5));
            var execution = await executor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(normalized, execution.Message);
            Assert.Equal(tenant, execution.TenantId);
            Assert.Equal(principal, execution.PrincipalId);
            Assert.Equal(session.Id, execution.SessionId);
            Assert.Single(sessions.GetEvents(session.Id.Value, 0), x => x.Type == "task.accepted");
        }
        finally
        {
            upstream.End();
            try
            {
                await relay.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                foreach (var hosted in workers)
                    await hosted.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public void Sample_registration_preserves_explicit_host_policy()
    {
        var custom = new ExplicitPolicy();
        var registrations = new ServiceCollection();
        registrations.AddSingleton<IVoiceTranscriptAdmissionPolicy>(custom);
        registrations.AddMetaTodoIntegration(Configuration());
        using var services = registrations.BuildServiceProvider();
        Assert.Same(custom, services.GetRequiredService<IVoiceTranscriptAdmissionPolicy>());
    }

    private static ServiceCollection Registrations()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenConnectTaskExecution();
        services.AddMetaTodoIntegration(Configuration());
        return services;
    }
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["OpenConnect:ApiUrl"] = "http://unused.invalid/",
        ["OpenConnect:ApiKey"] = "synthetic-not-used",
        ["OpenConnect:ReasoningModel"] = "synthetic-not-used",
        ["TODO_SERVICE_URL"] = "http://unused.invalid/",
        ["TODO_INTERNAL_TOKEN"] = "synthetic-not-used",
        ["TODO_INTERNAL_TENANT"] = "synthetic-not-used"
    }).Build();
    private sealed class RecordingExecutor : IHostTaskExecutor, IHostTaskExecutionAuthorizer
    {
        public TaskCompletionSource<HostTaskExecution> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken) { Completed.TrySetResult(task); return Task.CompletedTask; }
        public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class ExplicitPolicy : IVoiceTranscriptAdmissionPolicy
    {
        public bool ShouldAdmitEarly(string authoritativeTranscript) => false;
        public string NormalizeTranscript(string transcript) => transcript;
    }
}
