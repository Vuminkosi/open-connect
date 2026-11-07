using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Templates;

/// <summary>Thread-safe registry that validates and resolves single-base, narrowing-only templates.</summary>
public sealed class TemplateRegistry
{
    private static readonly Regex PlaceholderPattern = new(@"\{\{(?<key>[A-Za-z][A-Za-z0-9]*)\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretPattern = new(
        @"(?i)\b(?:bearer\s+)[A-Za-z0-9._~+/=-]+|\b(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, Func<TemplateVariables, string?>> PlaceholderValues =
        new Dictionary<string, Func<TemplateVariables, string?>>(StringComparer.Ordinal)
        {
            ["applicationName"] = variables => variables.ApplicationName,
            ["taskGoal"] = variables => variables.TaskGoal,
            ["locale"] = variables => variables.Locale,
            ["userProfileSummary"] = variables => variables.UserProfileSummary
        };

    private readonly object _sync = new();
    private Dictionary<string, AgentTemplate> _templates = new(StringComparer.Ordinal);

    /// <summary>Registers a template after validating its complete inheritance chain.</summary>
    /// <param name="template">The template to register.</param>
    /// <param name="allowWidening">Set true only with explicit trusted server-side approval; widening requires approval per AG-REQ-012.</param>
    public void Register(AgentTemplate template, bool allowWidening = false)
    {
        ArgumentNullException.ThrowIfNull(template);
        ValidateTemplate(template);
        lock (_sync)
        {
            if (_templates.ContainsKey(template.Id))
                throw new InvalidOperationException($"Template '{template.Id}' is already registered.");

            var candidate = new Dictionary<string, AgentTemplate>(_templates, StringComparer.Ordinal) { [template.Id] = template };
            ValidateChain(template, candidate, allowWidening);
            _templates = candidate;
        }
    }

    /// <summary>Gets the resolved template, with derived values applied after its base values.</summary>
    /// <param name="id">The template identifier.</param>
    /// <returns>The resolved template.</returns>
    public AgentTemplate Resolve(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_sync)
        {
            if (!_templates.TryGetValue(id, out var template))
                throw new KeyNotFoundException($"Template '{id}' was not found.");
            return template.BaseTemplateId is null ? template : Merge(ResolveBase(template, _templates), template);
        }
    }

    /// <summary>
    /// Loads the instruction text through the caller-provided Core InstructionLoader adapter, substitutes
    /// only approved values, HTML-encodes them, and calculates a SHA-256 revision of the rendered content.
    /// The adapter keeps the Agents package independent of the Core assembly.
    /// </summary>
    /// <param name="id">The template identifier.</param>
    /// <param name="variables">Values used to replace approved placeholders.</param>
    /// <param name="loadInstructionsAsync">Caller-provided loader for instruction content.</param>
    /// <param name="cancellationToken">Token used to cancel the load.</param>
    /// <returns>Rendered content and its SHA-256 revision.</returns>
    public async Task<RenderedTemplateInstructions> RenderAsync(
        string id,
        TemplateVariables variables,
        Func<string, CancellationToken, Task<string>> loadInstructionsAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(loadInstructionsAsync);
        var template = Resolve(id);
        var content = await loadInstructionsAsync(template.InstructionsPath, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(content);

        var rendered = PlaceholderPattern.Replace(content, match =>
        {
            var key = match.Groups["key"].Value;
            if (!PlaceholderValues.TryGetValue(key, out var getValue))
                throw new InvalidDataException($"Instruction template contains unsupported placeholder '{key}'.");
            var value = getValue(variables) ?? string.Empty;
            value = SecretPattern.Replace(value, "[REDACTED]");
            return HtmlEncoder.Default.Encode(value);
        });

        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rendered))).ToLowerInvariant();
        return new RenderedTemplateInstructions(rendered, revision);
    }

    private static AgentTemplate ResolveBase(AgentTemplate template, IReadOnlyDictionary<string, AgentTemplate> templates) =>
        template.BaseTemplateId is null
            ? template
            : Merge(ResolveBase(templates[template.BaseTemplateId], templates), templates[template.BaseTemplateId]);

    private static AgentTemplate Merge(AgentTemplate parent, AgentTemplate child) => child with
    {
        AllowedToolIds = new HashSet<ToolId>(child.AllowedToolIds),
        AllowedScopes = child.AllowedScopes is null ? new HashSet<string>(parent.AllowedScopes ?? (IReadOnlySet<string>)new HashSet<string>(), StringComparer.Ordinal) : new HashSet<string>(child.AllowedScopes, StringComparer.Ordinal)
    };

    private static void ValidateChain(AgentTemplate template, IReadOnlyDictionary<string, AgentTemplate> templates, bool allowWidening)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = template;
        while (current.BaseTemplateId is not null)
        {
            if (!visited.Add(current.Id))
                throw new InvalidOperationException($"Template inheritance contains a cycle at '{current.Id}'.");
            if (!templates.TryGetValue(current.BaseTemplateId, out var parent))
                throw new InvalidOperationException($"Template '{current.Id}' references missing base '{current.BaseTemplateId}'.");
            if (!allowWidening)
                EnsureNarrows(current, parent);
            current = parent;
        }
    }

    private static void EnsureNarrows(AgentTemplate child, AgentTemplate parent)
    {
        if (!child.AllowedToolIds.IsSubsetOf(parent.AllowedToolIds) ||
            !(child.AllowedScopes ?? new HashSet<string>(StringComparer.Ordinal)).IsSubsetOf(parent.AllowedScopes ?? new HashSet<string>(StringComparer.Ordinal)) ||
            !BudgetNarrows(child.Budget, parent.Budget))
            throw new InvalidOperationException($"Template '{child.Id}' widens the capabilities or budget of base '{parent.Id}'.");
    }

    private static bool BudgetNarrows(AgentBudget child, AgentBudget parent) =>
        Within(child.MaxToolCalls, parent.MaxToolCalls) && Within(child.MaxModelCalls, parent.MaxModelCalls) &&
        Within(child.MaxTokens, parent.MaxTokens) && Within(child.MaxConcurrentAgents, parent.MaxConcurrentAgents) &&
        Within(child.MaxQueuedAgents, parent.MaxQueuedAgents) && Within(child.MaxTotalAgents, parent.MaxTotalAgents) &&
        (parent.MaxRuntime is null || child.MaxRuntime is not null && child.MaxRuntime <= parent.MaxRuntime);

    // Zero represents no allowance; it is not an unbounded value.
    private static bool Within(long child, long parent) => child <= parent;

    private static void ValidateTemplate(AgentTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.Id) || string.IsNullOrWhiteSpace(template.Version) ||
            string.IsNullOrWhiteSpace(template.Description) || string.IsNullOrWhiteSpace(template.InstructionsPath) ||
            string.IsNullOrWhiteSpace(template.SafetyLabel))
            throw new ArgumentException("Template id, version, description, instruction path, and safety label are required.", nameof(template));
        if (Path.IsPathRooted(template.InstructionsPath) || template.InstructionsPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..", StringComparer.Ordinal))
            throw new ArgumentException("Template instruction path must be a safe relative path.", nameof(template));
        ArgumentNullException.ThrowIfNull(template.AllowedToolIds);
        ArgumentNullException.ThrowIfNull(template.Budget);
        ArgumentNullException.ThrowIfNull(template.MemoryPolicy);
        if (template.Budget.MaxToolCalls < 0 || template.Budget.MaxModelCalls < 0 || template.Budget.MaxTokens < 0 ||
            template.Budget.MaxConcurrentAgents < 0 || template.Budget.MaxQueuedAgents < 0 || template.Budget.MaxTotalAgents < 0 ||
            template.Budget.MaxRuntime < TimeSpan.Zero)
            throw new ArgumentException("Template budget limits cannot be negative.", nameof(template));
    }
}
