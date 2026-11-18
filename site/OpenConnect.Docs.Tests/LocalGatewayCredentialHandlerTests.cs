using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using OpenConnect.Samples.Blazor;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class LocalGatewayCredentialHandlerTests
{
    [Fact]
    public async Task Selects_exact_destination_credentials_and_does_not_follow_redirect()
    {
        await using var listener = new LoopbackServer();
        var guard = Secret();
        var gateway = Secret();
        using var client = LocalGatewayCredentialHandler.CreateClient(
            listener.Url + "/guard", guard, listener.Url + "/gateway", gateway);

        using var request = new HttpRequestMessage(HttpMethod.Get, listener.Url + "/guard?query=1");
        var (status, received) = await SendAndReplyAsync(client, request, listener, "302 Found", "/gateway");
        Assert.Equal(HttpStatusCode.Found, status);
        Assert.True(received.Contains($"Authorization: Bearer {guard}\r\n", StringComparison.OrdinalIgnoreCase));
        Assert.Null(request.Headers.Authorization);

        using var second = new HttpRequestMessage(HttpMethod.Get, listener.Url + "/gateway");
        var (secondStatus, secondReceived) = await SendAndReplyAsync(client, second, listener, "200 OK");
        Assert.Equal(HttpStatusCode.OK, secondStatus);
        Assert.True(secondReceived.Contains($"Authorization: Bearer {gateway}\r\n", StringComparison.OrdinalIgnoreCase));
        Assert.Null(second.Headers.Authorization);
    }

    [Fact]
    public async Task Rejects_mismatched_paths_authorities_and_existing_headers()
    {
        using var client = Client();
        foreach (var destination in new[]
        {
            "http://127.0.0.1:42123/guard/child",
            "http://127.0.0.1:42124/guard",
            "http://127.0.0.1:42123/Guard",
            "http://127.0.0.1:42123/other"
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, destination);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request));
        }
        using var authorized = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:42123/guard");
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(authorized));
        using var host = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:42123/guard");
        host.Headers.Host = "example.test";
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(host));
    }

    [Theory]
    [InlineData("http://127.0.0.1:42123/a/%2e%2e/guard")]
    [InlineData("http://127.0.0.1:42123/guard%2fchild")]
    public async Task Rejects_encoded_request_paths_before_transport(string destination)
    {
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, destination);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(request));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Removes_authorization_after_error_response()
    {
        await using var listener = new LoopbackServer();
        using var client = LocalGatewayCredentialHandler.CreateClient(
            listener.Url + "/guard", Secret(), listener.Url + "/gateway", Secret());
        using var request = new HttpRequestMessage(HttpMethod.Get, listener.Url + "/guard");
        var (status, _) = await SendAndReplyAsync(client, request, listener, "500 Internal Server Error");
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData("http://localhost:42123/guard")]
    [InlineData("http://127.0.0.1/guard")]
    [InlineData("http://127.0.0.1:42123/a/%2e%2e/guard")]
    [InlineData("http://127.0.0.1:42123/a/../guard")]
    [InlineData("http://user@127.0.0.1:42123/guard")]
    [InlineData("http://127.0.0.1:42123/guard#fragment")]
    public void Rejects_ambiguous_or_encoded_configured_destinations(string destination)
    {
        Assert.Throws<ArgumentException>(() => LocalGatewayCredentialHandler.CreateClient(
            destination, Secret(), "http://127.0.0.1:42123/gateway", Secret()));
    }

    [Fact]
    public void Rejects_overlapping_destinations_for_both_credentials()
    {
        Assert.Throws<ArgumentException>(() => LocalGatewayCredentialHandler.CreateClient(
            ["http://127.0.0.1:42123/guard", "http://127.0.0.1:42123/guard"], Secret(),
            ["http://127.0.0.1:42123/gateway"], Secret()));
        Assert.Throws<ArgumentException>(() => LocalGatewayCredentialHandler.CreateClient(
            "http://127.0.0.1:42123/guard", Secret(), "http://127.0.0.1:42123/guard", Secret()));
    }

    [Fact]
    public async Task Removes_authorization_after_transport_error_and_cancellation()
    {
        await using var listener = new LoopbackServer();
        using var client = LocalGatewayCredentialHandler.CreateClient(
            listener.Url + "/guard", Secret(), listener.Url + "/gateway", Secret());
        using var errorRequest = new HttpRequestMessage(HttpMethod.Post, listener.Url + "/guard");
        using var errorCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var errorTask = client.SendAsync(errorRequest, errorCancellation.Token);
        Exception? errorFailure = null;
        try
        {
            await listener.ReplyWithMalformedResponseAsync();
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => errorTask.WaitAsync(TimeSpan.FromSeconds(12)));
        }
        catch (Exception exception)
        {
            errorFailure = exception;
            throw;
        }
        finally
        {
            errorCancellation.Cancel();
            listener.CloseConnection();
            await ObserveAsync(errorTask, errorFailure);
        }
        Assert.Null(errorRequest.Headers.Authorization);

        using var cancellation = new CancellationTokenSource();
        using var safetyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, safetyTimeout.Token);
        using var cancelledRequest = new HttpRequestMessage(HttpMethod.Get, listener.Url + "/gateway");
        var cancelledTask = client.SendAsync(cancelledRequest, linkedCancellation.Token);
        Exception? cancellationFailure = null;
        try
        {
            await listener.AcceptAndReadAsync();
            Assert.False(safetyTimeout.IsCancellationRequested);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTask.WaitAsync(TimeSpan.FromSeconds(12)));
        }
        catch (Exception exception)
        {
            cancellationFailure = exception;
            throw;
        }
        finally
        {
            cancellation.Cancel();
            listener.CloseConnection();
            await ObserveAsync(cancelledTask, cancellationFailure);
        }
        Assert.Null(cancelledRequest.Headers.Authorization);
    }

    private static async Task<(HttpStatusCode Status, string Request)> SendAndReplyAsync(
        HttpClient client, HttpRequestMessage request, LoopbackServer listener, string status, string? location = null)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responseTask = client.SendAsync(request, cancellation.Token);
        Exception? failure = null;
        try
        {
            var received = await listener.ReplyAsync(status, location);
            using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(12));
            return (response.StatusCode, received);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            cancellation.Cancel();
            listener.CloseConnection();
            await ObserveAsync(responseTask, failure);
        }
    }

    private static async Task ObserveAsync(Task<HttpResponseMessage> task, Exception? originalFailure)
    {
        try
        {
            using var response = await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Observe a late fault, and dispose a late response; this does not terminate the operation.
            _ = task.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion) completed.Result.Dispose();
                    else _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var cleanupFailure = new Xunit.Sdk.XunitException(
                "HTTP operation did not settle within the cleanup timeout; its eventual completion is not guaranteed.");
            if (originalFailure is null) throw cleanupFailure;
            originalFailure.Data["HTTP cleanup failure"] = cleanupFailure;
        }
        catch (Exception)
        {
            // The original operation or assertion reports the failure.
        }
    }

    private static HttpClient Client() => LocalGatewayCredentialHandler.CreateClient(
        "http://127.0.0.1:42123/guard", Secret(), "http://127.0.0.1:42123/gateway", Secret());

    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private TcpClient? _connection;

        public LoopbackServer() => _listener.Start();
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public async Task<string> AcceptAndReadAsync()
        {
            CloseConnection();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _connection = await _listener.AcceptTcpClientAsync(timeout.Token);
            var stream = _connection.GetStream();
            var buffer = new byte[8192];
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (count == 0) break;
                length += count;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            return Encoding.ASCII.GetString(buffer, 0, length);
        }

        public async Task<string> ReplyAsync(string status, string? location = null)
        {
            try
            {
                var request = await AcceptAndReadAsync();
                var response = $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n" +
                    (location is null ? "" : $"Location: {location}\r\n") + "\r\n";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _connection!.GetStream().WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
                return request;
            }
            finally
            {
                CloseConnection();
            }
        }

        public async Task ReplyWithMalformedResponseAsync()
        {
            try
            {
                await AcceptAndReadAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _connection!.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 invalid\r\n\r\n"), timeout.Token);
            }
            finally
            {
                CloseConnection();
            }
        }

        public void CloseConnection()
        {
            _connection?.Dispose();
            _connection = null;
        }

        public ValueTask DisposeAsync()
        {
            CloseConnection();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
