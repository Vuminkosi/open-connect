using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Samples.Workers.Host;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class TodoServiceClientTests
{
    private const string Guard = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Token = "synthetic-todo-token";
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    [Fact]
    public async Task Different_tenant_is_rejected_before_dependencies_are_used()
    {
        var executor = new MetaTodoTaskExecutor(
            null!, null!, null!, null!, null!, null!,
            Options.Create(new MetaCloudOptions { TodoTenant = Tenant }),
            NullLogger<MetaTodoTaskExecutor>.Instance);
        var task = new HostTaskExecution(
            new TaskId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()),
            new PrincipalId(Ulid.NewUlid()), new SessionId(Ulid.NewUlid()),
            "List my tasks", "tenant-isolation");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => executor.ExecuteAsync(task, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guarded_client_does_not_forward_credentials_on_redirect(bool useDependencyInjection)
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        target.Start();
        var sourceUrl = $"http://127.0.0.1:{((IPEndPoint)source.LocalEndpoint).Port}";
        var targetUrl = $"http://127.0.0.1:{((IPEndPoint)target.LocalEndpoint).Port}/capture";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var services = new ServiceCollection().AddMetaTodoIntegration(Configuration(sourceUrl)).BuildServiceProvider();
        using var suppliedClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            BaseAddress = new Uri(sourceUrl + "/internal/openconnect/todos/")
        };
        var client = useDependencyInjection
            ? services.GetRequiredService<TodoServiceClient>()
            : new TodoServiceClient(suppliedClient, Options.Create(OptionsFor(sourceUrl)));

        var requestTask = client.ListAsync(timeout.Token);
        try
        {
            using var connection = await source.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            var request = await ReadHeadersAsync(stream, timeout.Token);
            var response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 302 Found\r\nLocation: {targetUrl}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, timeout.Token);

            await Assert.ThrowsAsync<HttpRequestException>(() => requestTask);
            Assert.Contains($"Authorization: Bearer {Token}\r\n", request, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"X-OpenConnect-Local-Guard: {Guard}\r\n", request, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"X-OpenConnect-Tenant: {Tenant}\r\n", request, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(Guard, Token);
            using var noRedirect = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var unexpected = await target.AcceptTcpClientAsync(noRedirect.Token);
            });
        }
        finally
        {
            timeout.Cancel();
            // Observe a late fault even if the bounded cleanup wait expires.
            _ = requestTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            try
            {
                await requestTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // The test body owns the assertion or primary failure, not teardown.
            }
        }
    }

    [Theory]
    [InlineData("http://example.com", "http://example.com/internal/openconnect/todos/", Guard, Token)]
    [InlineData("http://127.0.0.1:1234/path", "http://127.0.0.1:1234/internal/openconnect/todos/", Guard, Token)]
    [InlineData("http://127.0.0.1:1234", "http://127.0.0.1:1235/internal/openconnect/todos/", Guard, Token)]
    [InlineData("http://127.0.0.1:1234", "http://127.0.0.1:1234/wrong/", Guard, Token)]
    [InlineData("http://127.0.0.1:1234", "http://127.0.0.1:1234/internal/openconnect/todos/", Guard, "")]
    [InlineData("http://127.0.0.1:1234", "http://127.0.0.1:1234/internal/openconnect/todos/", Guard, Guard)]
    public async Task Guarded_direct_client_rejects_invalid_destination_or_credentials(
        string serviceUrl, string baseUrl, string guard, string token)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var client = new TodoServiceClient(http, Options.Create(new MetaCloudOptions
        {
            TodoServiceUrl = serviceUrl,
            TodoInternalToken = token,
            TodoTenant = Tenant,
            LocalGuardCredential = guard
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("TODO_SERVICE_URL")]
    [InlineData("TODO_INTERNAL_TOKEN")]
    [InlineData("TODO_INTERNAL_TENANT")]
    [InlineData("OpenConnect:LocalAcceptance:ServiceBearer")]
    public void Guarded_registration_rejects_missing_settings(string missing)
    {
        var configuration = Configuration("http://127.0.0.1:1234");
        configuration[missing] = null;
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMetaTodoIntegration(configuration));
    }

    private static MetaCloudOptions OptionsFor(string url) => new()
    {
        TodoServiceUrl = url,
        TodoInternalToken = Token,
        TodoTenant = Tenant,
        LocalGuardCredential = Guard
    };

    private static IConfigurationRoot Configuration(string url) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:ApiUrl"] = "http://127.0.0.1:1234",
            ["OpenConnect:ApiKey"] = "synthetic-model-key",
            ["OpenConnect:ReasoningModel"] = "synthetic-model",
            ["OpenConnect:LocalAcceptance:Enabled"] = "true",
            ["OpenConnect:LocalAcceptance:ServiceBearer"] = Guard,
            ["TODO_SERVICE_URL"] = url,
            ["TODO_INTERNAL_TOKEN"] = Token,
            ["TODO_INTERNAL_TENANT"] = Tenant
        }).Build();

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (bytes.Count < 16384)
        {
            if (await stream.ReadAsync(buffer, cancellationToken) == 0)
                throw new IOException("Connection closed before request headers completed.");
            bytes.Add(buffer[0]);
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 &&
                bytes[count - 2] == 13 && bytes[count - 1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new IOException("Request headers exceeded the test limit.");
    }
}
