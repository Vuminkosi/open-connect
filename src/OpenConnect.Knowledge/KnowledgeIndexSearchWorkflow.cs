using System.Text.Json;
using OpenConnect.Privacy;

namespace OpenConnect.Knowledge;

/// <summary>Coordinates an authorized indexing job and subsequent hybrid search.</summary>
public sealed class KnowledgeIndexSearchWorkflow(KnowledgeIndexClient index, KnowledgeSearchClient search)
{
    private readonly KnowledgeIndexClient _index = index ?? throw new ArgumentNullException(nameof(index));
    private readonly KnowledgeSearchClient _search = search ?? throw new ArgumentNullException(nameof(search));

    /// <summary>
    /// Submits an already authorized immutable object reference, waits for indexing, and returns
    /// privacy-checked search hits with source citations. The caller owns the returned document.
    /// The host callbacks on both clients must authorize the exact object, destination and roles.
    /// A result is source evidence, not a generated or verified answer.
    /// </summary>
    public async Task<JsonDocument> IndexAndSearchAsync(EgressContext context, KnowledgeIndexSubmission submission,
        IReadOnlyList<string> roleIds, string query,
        Func<EgressContext, string, CancellationToken, Task<KnowledgeEmbeddingResult>> embedQuery,
        int limit = 10, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(roleIds);
        ArgumentNullException.ThrowIfNull(embedQuery);
        if (roleIds.Count > 64 || string.IsNullOrWhiteSpace(query) || query.Length > 8192 || limit is < 1 or > 32)
            throw new ArgumentException("Invalid index and search request.");
        var roles = Array.AsReadOnly(roleIds.ToArray());
        if (roles.Count > 64 || roles.Any(role => !KnowledgeIndexSubmission.Identifier(role)))
            throw new ArgumentException("Invalid index and search request.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var receipt = await _index.SubmitAsync(context, submission, deadline.Token).ConfigureAwait(false);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var status = await _index.StatusAsync(context, submission.JobId, submission.ApplicationId,
                submission.CollectionId, submission.DocumentId, roles, deadline.Token).ConfigureAwait(false);
            var state = status.RootElement.GetProperty("result").GetProperty("state").GetString();
            if (state == "completed")
                break;
            if (state == "dead")
                throw new InvalidOperationException("The knowledge indexing job failed.");
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
        }
        return await _search.SearchHybridAsync(context, submission.ApplicationId, submission.CollectionId,
            roles, query, embedQuery, limit, deadline.Token).ConfigureAwait(false);
    }
}
