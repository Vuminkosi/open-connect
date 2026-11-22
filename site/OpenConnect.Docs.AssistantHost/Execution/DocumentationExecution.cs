using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using OpenConnect.Agents.Templates;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.LiveUI.Hosting;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.AssistantHost.Execution;

internal sealed class DocumentationTaskAuthorizer : IHostTaskExecutionAuthorizer
{
    public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (task.TenantId == default || task.PrincipalId == default)
            throw new UnauthorizedAccessException();
        return Task.CompletedTask;
    }
}

internal sealed class DocumentationExecutionInputsFactory(
    IAgentDefinitionRegistry registry,
    IChatClient chat,
    IEgressGate gate,
    INativeAgentSessionStore store,
    INativeAgentSessionProtector protector,
    INativeSessionTurnGate turnGate) : ILiveUIHostExecutionInputsFactory
{
    private const string Instructions =
        "You are the interactive assistant for OpenConnect, an open-source .NET-first, privacy-first AI application framework " +
        "(SDKs, privacy firewall, LiveUI, workers, and Microsoft Agent Framework integration; not the VPN client). " +
        "When explaining a documentation page or SDK API: " +
        "(1) Call read_openconnect_doc(slug) to read the real, verified documentation text and code samples, AND " +
        "(2) ALWAYS navigate the user's browser to '/docs/v1/{slug}' (route id 'documentation-v1-{slug}') using the LiveUI " +
        "navigate tool and highlight the primary section BEFORE giving your answer — never explain a doc page while leaving " +
        "the user on a different page. " +
        "Structure your explanation naturally around the page's exact section headings (mentioning each section heading title " +
        "at the start of the sentence that explains it) so LiveUI automatically moves the spotlight in real-time sync with " +
        "voice playback. Keep explanations clear, concise, and grounded in the verified samples. Avoid redundant filler lines " +
        "between navigation steps. Say when something is not documented.";

    private static readonly IReadOnlyList<AITool> DocumentationTools =
    [
        AIFunctionFactory.Create(ListOpenConnectDocs, "list_openconnect_docs",
            "Lists available OpenConnect v1 documentation slugs that can be read with read_openconnect_doc."),
        AIFunctionFactory.Create(ReadOpenConnectDoc, "read_openconnect_doc",
            "Reads the verified Markdown content and code examples for an OpenConnect v1 documentation page by slug (e.g. 'persistence', 'privacy', 'liveui', 'voice-and-translation', 'getting-started').")
    ];

    public Task<LiveUIHostExecutionInputs> CreateAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var binding = new NativeAgentSessionBinding(task.SessionId, task.TenantId, task.PrincipalId,
            "documentation-assistant", "documentation-v1", "maf", "v1", "configured-provider");
        var context = new AgentExecutionContext
        {
            TenantScope = task.TenantId.Value.ToString(), PrincipalOrWorkloadScope = task.PrincipalId.Value.ToString(),
            IdentityKind = AgentIdentityMode.ActiveUser, SessionId = task.SessionId, TaskId = task.TaskId,
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()), Depth = 0, CatalogRevision = 1, PolicyRevision = 1,
            AuthRevision = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(3), RemainingBudgets = ImmutableDictionary<string, long>.Empty,
            CancellationToken = cancellationToken
        };
        var egress = new EgressContext(task.TenantId, task.PrincipalId, task.SessionId, EgressDestination.ExternalModel);
        var factory = new MafRealAgentFactory(registry, chat, gate, new FixedEgressContextProvider(egress));
        var persistence = new NativeSessionTurnPersistenceContext(new NativeAgentSessionPersistence(store, protector), binding, turnGate);
        return Task.FromResult(new LiveUIHostExecutionInputs(factory, new AgentId("documentation-assistant"), context,
            (_, ct) => { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(false); }, persistence, Instructions, DocumentationTools));
    }

    private static string ListOpenConnectDocs()
    {
        var directory = ResolveDocsDirectory();
        if (directory is null) return "No documentation files are packaged in this host.";
        var slugs = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(slug => slug, StringComparer.Ordinal)
            .ToArray();
        return slugs.Length == 0 ? "No documentation files found." : string.Join(", ", slugs);
    }

    private static string ReadOpenConnectDoc(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "A documentation slug is required.";
        var normalized = slug.Trim().Trim('/').ToLowerInvariant();
        if (normalized.StartsWith("docs/v1/", StringComparison.Ordinal))
            normalized = normalized["docs/v1/".Length..];
        if (normalized.Length is < 1 or > 80 || !normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return "Invalid documentation slug.";
        var directory = ResolveDocsDirectory();
        if (directory is null) return "Documentation content directory is unavailable.";
        var fullPath = Path.GetFullPath(Path.Combine(directory, $"{normalized}.md"));
        if (!fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return $"Documentation page '{normalized}' was not found.";
        var content = File.ReadAllText(fullPath);
        var body = content.Length <= 12_000 ? content : content[..12_000] + "\n\n[Truncated]";
        return $"[LiveUI Required: If the user is not already on '/docs/v1/{normalized}', navigate them to route id 'documentation-v1-{normalized}' and highlight the relevant section before answering.]\n\n{body}";
    }

    private static string? ResolveDocsDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Content", "v1"),
            Path.Combine(Directory.GetCurrentDirectory(), "Content", "v1"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OpenConnect.Docs", "Content", "v1"))
        };
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }
}
