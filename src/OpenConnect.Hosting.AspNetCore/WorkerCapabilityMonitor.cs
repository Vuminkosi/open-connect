using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>Protected worker endpoint and exclusively owned logical capability IDs.</summary>
public sealed class WorkerCapabilitySource
{
    internal const int MaxCapabilityIdsPerSource = 16;
    internal readonly string Endpoint;
    internal readonly string Token;
    internal readonly HashSet<string> Ids;
    internal readonly bool Inference;

    /// <summary>Creates an opt-in source. HTTP requires an explicitly protected network; absolute socket paths use Unix sockets.</summary>
    public WorkerCapabilitySource(string endpoint, string token, IReadOnlyList<string> capabilityIds,
        bool inference = false, bool allowInsecureHttp = false)
    {
        ArgumentNullException.ThrowIfNull(capabilityIds);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) ||
            capabilityIds.Count is < 1 or > MaxCapabilityIdsPerSource)
            throw new ArgumentException("Invalid capability source.");
        var snapshot = capabilityIds.ToArray();
        var ids = snapshot.ToHashSet(StringComparer.Ordinal);
        if (ids.Count != snapshot.Length || (inference && (ids.Count != 1 || !ids.Contains("documents.extract"))))
            throw new ArgumentException("Invalid capability source.");
        using var empty = JsonDocument.Parse("{\"capabilities\":[]}");
        _ = WorkerCapabilityResponseParser.Parse(empty.RootElement, ids, false);
        if (!Path.IsPathFullyQualified(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
                !(uri.Scheme == Uri.UriSchemeHttps || allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
                throw new ArgumentException("A protected worker endpoint is required.", nameof(endpoint));
        }
        Endpoint = endpoint;
        Token = token;
        Ids = ids;
        Inference = inference;
    }
}

/// <summary>Polls configured private workers and leases only their verified ready capabilities.</summary>
public sealed class WorkerCapabilityMonitor : BackgroundService
{
    internal const int MaxSources = 16;
    private readonly ICapabilityRegistry _registry;
    private readonly (WorkerCapabilitySource Source, HttpClient Client)[] _sources;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _state = new();
    private bool _disposed;

    /// <summary>
    /// The monitor exclusively owns the configured IDs in this registry. Other registrations must not use them.
    /// Configure authenticated private workers and a polling interval shorter than the registry lease.
    /// </summary>
    public WorkerCapabilityMonitor(ICapabilityRegistry registry, IReadOnlyList<WorkerCapabilitySource> sources,
        TimeSpan? interval = null) : this(registry, sources, interval, CreateClient) { }

    internal WorkerCapabilityMonitor(ICapabilityRegistry registry, IReadOnlyList<WorkerCapabilitySource> sources,
        TimeSpan? interval, Func<WorkerCapabilitySource, HttpClient> createClient)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(createClient);
        var snapshot = sources.ToArray();
        if (snapshot.Length is < 1 or > MaxSources || snapshot.Any(source => source is null))
            throw new ArgumentException("Invalid capability sources.");
        var ids = snapshot.SelectMany(source => source.Ids).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => registry.Get(new CapabilityId(id)) is not null))
            throw new ArgumentException("Capability source IDs must have exclusive ownership.");
        _interval = interval ?? TimeSpan.FromSeconds(15);
        if (_interval < TimeSpan.FromSeconds(1) || _interval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(interval));
        _registry = registry;
        var clients = new List<(WorkerCapabilitySource, HttpClient)>();
        try
        { foreach (var source in snapshot) clients.Add((source, createClient(source))); }
        catch { foreach (var (_, client) in clients) client.Dispose(); throw; }
        _sources = clients.ToArray();
    }

    /// <summary>Refreshes each source; unavailable, invalid or omitted IDs are marked stale.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_state)
                ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (source, client) in _sources)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/v1/capabilities");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.Token);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65536)
                        throw new InvalidOperationException();
                    await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[4096];
                    int count;
                    while ((count = await input.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
                    { if (buffer.Length + count > 65536) throw new InvalidOperationException(); buffer.Write(chunk, 0, count); }
                    buffer.Position = 0;
                    using var json = JsonDocument.Parse(buffer);
                    var ready = WorkerCapabilityResponseParser.Parse(json.RootElement, source.Ids, source.Inference);
                    deadline.Token.ThrowIfCancellationRequested();
                    var published = ready.Select(item => item.Id.Value).ToHashSet(StringComparer.Ordinal);
                    lock (_state)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        deadline.Token.ThrowIfCancellationRequested();
                        foreach (var id in source.Ids)
                            if (!published.Contains(id))
                                _registry.MarkStale(new CapabilityId(id));
                        foreach (var descriptor in ready)
                            _registry.Register(descriptor);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { Stale(source); throw; }
                catch { Stale(source); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var (source, _) in _sources)
                Stale(source);
            throw;
        }
        finally { _refresh.Release(); }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            do
            { await RefreshAsync(stoppingToken).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { foreach (var (source, _) in _sources) Stale(source); }
    }

    private void Stale(WorkerCapabilitySource source)
    { foreach (var id in source.Ids) _registry.MarkStale(new CapabilityId(id)); }

    private static HttpClient CreateClient(WorkerCapabilitySource source)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        Uri origin;
        if (Path.IsPathFullyQualified(source.Endpoint))
        {
            var socketEndpoint = new UnixDomainSocketEndPoint(source.Endpoint);
            handler.ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                { await socket.ConnectAsync(socketEndpoint, token).ConfigureAwait(false); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            };
            origin = new Uri("http://localhost");
        }
        else
            origin = new Uri(source.Endpoint, UriKind.Absolute);
        return new HttpClient(handler) { BaseAddress = origin, Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        lock (_state)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var (source, _) in _sources)
                Stale(source);
        }
        base.Dispose();
        foreach (var (_, client) in _sources)
            client.Dispose();
    }
}
