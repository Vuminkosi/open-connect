using System.Security.Cryptography;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Knowledge;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.Services.KnowledgeDemo;

internal interface IDocumentationKnowledgeDemo
{
    Task<JsonDocument> SearchAsync(string query, CancellationToken cancellationToken);
}

internal sealed class DocumentationKnowledgeConfiguration(Uri index, Uri search, Uri embedding, string workerToken,
    string signingKey, bool cloud, string? cloudToken, bool allowHttp, string objectId, string objectSha256,
    int objectSize, string objectVersion, string embeddingSpace)
{
    internal Uri Index { get; } = index;
    internal Uri Search { get; } = search;
    internal Uri Embedding { get; } = embedding;
    internal string WorkerToken { get; } = workerToken;
    internal string SigningKey { get; } = signingKey;
    internal bool Cloud { get; } = cloud;
    internal string? CloudToken { get; } = cloudToken;
    internal bool AllowHttp { get; } = allowHttp;
    internal string ObjectId { get; } = objectId;
    internal string ObjectSha256 { get; } = objectSha256;
    internal int ObjectSize { get; } = objectSize;
    internal string ObjectVersion { get; } = objectVersion;
    internal string EmbeddingSpace { get; } = embeddingSpace;
}

internal static class DocumentationKnowledgeRegistration
{
    internal static IServiceCollection AddDocumentationKnowledgeDemo(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        const string prefix = "OpenConnect:KnowledgeDemo:";
        if (!configuration.GetValue<bool>(prefix + "Enabled")) return services;
        if (!environment.IsDevelopment() || !services.Any(service => service.ServiceType == typeof(DocumentationGatewayVisitorTokens)))
            throw new InvalidOperationException("The knowledge example requires development and documentation visitor identity.");
        var allowHttp = configuration.GetValue<bool>(prefix + "AllowInsecureHttp");
        Uri Origin(string name)
        {
            if (!Uri.TryCreate(configuration[prefix + name], UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("The knowledge example worker settings are invalid.");
            return uri;
        }
        var index = Origin("IndexOrigin");
        var search = Origin("SearchOrigin");
        var embedding = Origin("EmbeddingOrigin");
        var token = configuration["OPENCONNECT_WORKER_TOKEN"];
        var key = configuration["OPENCONNECT_WORKER_SIGNING_KEY"];
        var cloud = configuration.GetValue<bool>(prefix + "UseCloudEmbedding");
        var cloudToken = configuration[prefix + "CloudBearerToken"];
        var objectId = configuration[prefix + "ObjectId"];
        var sha = configuration[prefix + "ObjectSha256"];
        var version = configuration[prefix + "ObjectVersion"];
        var space = configuration[prefix + "EmbeddingSpaceId"];
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(key) ||
            (cloud && (string.IsNullOrWhiteSpace(cloudToken) || cloudToken.Length > 160 || cloudToken.Contains(',') || cloudToken.Any(char.IsWhiteSpace))) ||
            !int.TryParse(configuration[prefix + "ObjectSize"], out var size))
            throw new InvalidOperationException("The knowledge example worker settings are invalid.");
        byte[] keyBytes;
        try { keyBytes = Convert.FromHexString(key); }
        catch (FormatException) { throw new InvalidOperationException("The knowledge example worker settings are invalid."); }
        try { if (keyBytes.Length < 32) throw new InvalidOperationException("The knowledge example worker settings are invalid."); }
        finally { CryptographicOperations.ZeroMemory(keyBytes); }
        try
        {
            _ = new KnowledgeIndexSubmission(Guid.NewGuid(), "documentation", "visitor-demo", "validation", version!, space!,
                objectId!, sha!, size, version!, 0, ["validation"], [], DateTimeOffset.UtcNow.AddHours(1));
        }
        catch (ArgumentException) { throw new InvalidOperationException("The knowledge example object settings are invalid."); }
        services.AddOpenConnectRulesDetector();
        services.EnsureHostPrivacyServices();
        services.AddSingleton(new DocumentationKnowledgeConfiguration(index, search, embedding, token, key, cloud, cloudToken,
            allowHttp, objectId!, sha!, size, version!, space!));
        services.AddSingleton<DocumentationKnowledgeCapacity>();
        services.AddScoped<IDocumentationKnowledgeDemo, DocumentationKnowledgeDemo>();
        return services;
    }
}

internal sealed class DocumentationKnowledgeCapacity : IDisposable
{
    internal SemaphoreSlim Slots { get; } = new(2, 2);
    public void Dispose() => Slots.Dispose();
}

internal sealed class DocumentationKnowledgeDemo(DocumentationGatewayVisitorIdentity visitor,
    DocumentationGatewayVisitorTokens tokens, DocumentationKnowledgeConfiguration settings,
    DocumentationKnowledgeCapacity capacity, IEgressGate gate) : IDocumentationKnowledgeDemo
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private KnowledgeIndexSubmission? _submission;
    private bool _indexed;

    public async Task<JsonDocument> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) throw new ArgumentException("Invalid query.", nameof(query));
        if (!tokens.TryValidate(visitor.Token, out var tenant, out var principal)) throw new UnauthorizedAccessException();
        if (!await _oneAtATime.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Knowledge example is busy.");
        var acquired = false;
        byte[]? key = null;
        try
        {
            acquired = await capacity.Slots.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!acquired) throw new InvalidOperationException("Knowledge example is busy.");
            _submission ??= new KnowledgeIndexSubmission(Guid.NewGuid(), "documentation", "visitor-demo", "docs-" + principal,
                settings.ObjectVersion, settings.EmbeddingSpace, settings.ObjectId, settings.ObjectSha256, settings.ObjectSize,
                settings.ObjectVersion, 0, [principal], [], DateTimeOffset.UtcNow.AddHours(1));
            var submission = _submission;
            if (submission.DocumentExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Knowledge example expired.");
            var context = new EgressContext(new TenantId(Ulid.Parse(tenant)), new PrincipalId(Ulid.Parse(principal)),
                new SessionId(Ulid.NewUlid()), EgressDestination.Worker);
            bool Authorized(EgressContext scope) => tokens.TryValidate(visitor.Token, out var currentTenant, out var currentPrincipal) &&
                currentTenant == tenant && currentPrincipal == principal && scope == context;
            key = Convert.FromHexString(settings.SigningKey);
            using var index = new KnowledgeIndexClient(gate, settings.Index, settings.WorkerToken, key,
                (scope, candidate, _) => ValueTask.FromResult(Authorized(scope) && ReferenceEquals(candidate, submission)),
                (scope, app, collection, document, roles, _) => ValueTask.FromResult(Authorized(scope) &&
                    app == submission.ApplicationId && collection == submission.CollectionId && document == submission.DocumentId && roles.Count == 0), settings.AllowHttp);
            using var search = new KnowledgeSearchClient(gate, settings.Search, settings.WorkerToken, key,
                (scope, app, collection, roles, _) => ValueTask.FromResult(Authorized(scope) && app == submission.ApplicationId &&
                    collection == submission.CollectionId && roles.Count == 0), settings.AllowHttp);
            using var embed = settings.Cloud
                ? KnowledgeEmbeddingClient.CreateCloud(gate, settings.Embedding, settings.CloudToken!, settings.AllowHttp)
                : new KnowledgeEmbeddingClient(gate, settings.Embedding, settings.WorkerToken, key, EgressDestination.Worker, settings.AllowHttp);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            if (!_indexed)
            {
                using var receipt = await index.SubmitAsync(context, submission, deadline.Token).ConfigureAwait(false);
                while (true)
                {
                    using var status = await index.StatusAsync(context, submission.JobId, submission.ApplicationId,
                        submission.CollectionId, submission.DocumentId, [], deadline.Token).ConfigureAwait(false);
                    var state = status.RootElement.GetProperty("result").GetProperty("state").GetString();
                    if (state == "completed") { _indexed = true; break; }
                    if (state == "dead") throw new InvalidOperationException("Knowledge indexing failed.");
                    await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
                }
            }
            var response = await search.SearchHybridAsync(context, submission.ApplicationId, submission.CollectionId, [], query,
                async (scope, text, ct) =>
                {
                    if (!Authorized(scope)) throw new UnauthorizedAccessException();
                    var embeddingScope = settings.Cloud ? scope with { Destination = EgressDestination.ExternalModel } : scope;
                    var result = await embed.EmbedAsync(embeddingScope, "query", [text], ct).ConfigureAwait(false);
                    if (!Authorized(scope)) throw new UnauthorizedAccessException();
                    if (result.SpaceId != submission.EmbeddingSpaceId) throw new InvalidOperationException("Knowledge model space changed.");
                    return result;
                }, cancellationToken: deadline.Token).ConfigureAwait(false);
            try
            {
                if (!Authorized(context)) throw new UnauthorizedAccessException();
                foreach (var ranked in response.RootElement.GetProperty("result").GetProperty("hits").EnumerateArray())
                {
                    var hit = ranked.GetProperty("hit");
                    if (hit.GetProperty("document_id").GetString() != submission.DocumentId ||
                        hit.GetProperty("source_revision").GetString() != submission.SourceRevision)
                        throw new InvalidOperationException("Knowledge search returned an unauthorized source.");
                }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (acquired) capacity.Slots.Release();
            _oneAtATime.Release();
        }
    }
}
