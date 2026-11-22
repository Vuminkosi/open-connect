using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using OpenConnect.Knowledge;
using OpenConnect.Privacy;

namespace OpenConnect.Samples.Blazor;

/// <summary>Development-only search over an operator-approved versioned object.</summary>
internal sealed class TodoKnowledgeDemo(TodoDocumentDemoAccess access, IConfiguration configuration, IEgressGate gate)
{
    internal async Task<JsonDocument> SearchAsync(ClaimsPrincipal user, string query, CancellationToken cancellationToken)
    {
        var context = access.CreateContext(user);
        var prefix = "OpenConnect:KnowledgeDemo:";
        var indexOrigin = Origin("IndexOrigin");
        var searchOrigin = Origin("SearchOrigin");
        var embeddingOrigin = Origin("EmbeddingOrigin");
        var cloudEmbedding = configuration.GetValue<bool>(prefix + "UseCloudEmbedding");
        var cloudToken = configuration[prefix + "CloudBearerToken"];
        var token = configuration["OPENCONNECT_WORKER_TOKEN"];
        var keyText = configuration["OPENCONNECT_WORKER_SIGNING_KEY"];
        var objectId = configuration[prefix + "ObjectId"];
        var sha = configuration[prefix + "ObjectSha256"];
        var version = configuration[prefix + "ObjectVersion"];
        var space = configuration[prefix + "EmbeddingSpaceId"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(keyText) ||
            !int.TryParse(configuration[prefix + "ObjectSize"], out var size) ||
            string.IsNullOrWhiteSpace(objectId) || string.IsNullOrWhiteSpace(sha) ||
            string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(space) ||
            (cloudEmbedding && string.IsNullOrWhiteSpace(cloudToken)))
            throw new InvalidOperationException("Knowledge example is not configured.");
        byte[] key;
        try { key = Convert.FromHexString(keyText); }
        catch (FormatException) { throw new InvalidOperationException("Knowledge example is not configured."); }
        var allowHttp = configuration.GetValue<bool>(prefix + "AllowInsecureHttp");
        try
        {
            var documentId = "sample-" + objectId;
            var submission = new KnowledgeIndexSubmission(Guid.NewGuid(), "todo-sample", "knowledge-example", documentId,
                version, space, objectId, sha, size, version, 0, [context.Principal.Value.ToString()], []);
            bool Authorized(EgressContext scope) => access.Authorizes(scope);
            using var index = new KnowledgeIndexClient(gate, indexOrigin, token, key,
                (scope, candidate, _) => ValueTask.FromResult(Authorized(scope) && ReferenceEquals(candidate, submission)),
                (scope, app, collection, document, roles, _) => ValueTask.FromResult(Authorized(scope) &&
                    app == submission.ApplicationId && collection == submission.CollectionId && document == documentId && roles.Count == 0), allowHttp);
            using var search = new KnowledgeSearchClient(gate, searchOrigin, token, key,
                (scope, app, collection, roles, _) => ValueTask.FromResult(Authorized(scope) && app == submission.ApplicationId &&
                    collection == submission.CollectionId && roles.Count == 0), allowHttp);
            using var embed = cloudEmbedding
                ? KnowledgeEmbeddingClient.CreateCloud(gate, embeddingOrigin, cloudToken!, allowHttp)
                : new KnowledgeEmbeddingClient(gate, embeddingOrigin, token, key, EgressDestination.Worker, allowHttp);
            return await new KnowledgeIndexSearchWorkflow(index, search).IndexAndSearchAsync(context, submission, [], query,
                async (scope, text, ct) =>
                {
                    var embeddingScope = cloudEmbedding ? scope with { Destination = EgressDestination.ExternalModel } : scope;
                    var result = await embed.EmbedAsync(embeddingScope, "query", [text], ct).ConfigureAwait(false);
                    if (result.SpaceId != submission.EmbeddingSpaceId) throw new InvalidOperationException("Knowledge model space changed.");
                    return result;
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        Uri Origin(string name) => Uri.TryCreate(configuration[prefix + name], UriKind.Absolute, out var uri)
            ? uri : throw new InvalidOperationException("Knowledge example is not configured.");
    }
}
