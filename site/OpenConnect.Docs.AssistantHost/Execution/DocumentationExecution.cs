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
        "STRICT RESPONSE & NAVIGATION RULES: " +
        "(1) NEVER narrate your internal planning, thinking, tool calls, or navigation steps. Do NOT emit status filler like " +
        "'On it', 'Got it', 'Let me check', 'I'll pull together', 'I've got the details', or 'You're on the guide now'. " +
        "Emit NO text between tool calls — call your tools silently and only speak/write the final useful answer for the user. " +
        "(2) Navigate to AT MOST ONE documentation page per turn (the single best starting page for the user's question) so " +
        "voice playback starts within seconds. Never chain multi-page tours across 2 or 3 pages in one turn. " +
        "(3) Call read_openconnect_doc(slug) to verify exact package names and DI calls before answering, and ALWAYS " +
        "navigate the user's browser to '/docs/v1/{slug}' (route id 'documentation-v1-{slug}') and highlight the primary section " +
        "before your final answer. " +
        "(4) INTERACTIVE, CONCISE GUIDANCE (NEVER DUMP A WHOLE PAGE): Because you are already opening and highlighting the " +
        "documentation page on screen for the user, NEVER summarize or recite the entire page, and NEVER paste fenced code blocks " +
        "(```...```) into your response unless the user explicitly asks you to write custom code not already shown on the page. " +
        "Instead, keep every response short, clear, and conversational (2 to 4 short sentences, under 90 words): tell the user " +
        "what the highlighted sample on the page does (for example: 'Use the highlighted sample in **Speech input and output** to " +
        "register the voice client and Silero VAD on your host'), mention the exact section heading title so LiveUI spotlights it " +
        "in sync with your voice, and end with a short question offering 2 or 3 next steps (e.g. 'Want me to walk through " +
        "Host Speech-to-Text next, or open LiveUI interaction and inference?').";

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
        var content = SanitizeForExternalModel(File.ReadAllText(fullPath));
        var body = content.Length <= 6_000 ? content : content[..6_000] + "\n\n[Truncated]";
        return $"[LiveUI Required: If the user is not already on '/docs/v1/{normalized}', navigate them to route id 'documentation-v1-{normalized}' and highlight the relevant section before answering.]\n\n{body}";
    }

    private static string SanitizeForExternalModel(string markdown)
    {
        // Neutralize documentation placeholder emails, bearer tokens, and connection-string passwords
        // so the host Privacy Firewall does not treat public doc examples as live sensitive egress.
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            markdown, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "<example-email>");
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized, @"(?i)(Password|Pwd|ApiKey|Secret|Token)\s*[:=]\s*[^\s;""'`,}]+", "$1=<placeholder>");
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized, @"(?i)Bearer\s+[A-Za-z0-9._\-~+/]+=*", "Bearer <placeholder>");
        return sanitized;
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
