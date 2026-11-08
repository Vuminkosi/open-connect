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
    private Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private Dictionary<string, string> _migrations = new(StringComparer.Ordinal);

    /// <summary>Registers a template after validating its complete inheritance chain.</summary>
    /// <param name="template">The template to register.</param>
    /// <param name="allowWidening">Set true only with explicit trusted server-side approval; widening requires approval per AG-REQ-012.</param>
    public void Register(AgentTemplate template, bool allowWidening = false)
    {
        ArgumentNullException.ThrowIfNull(template);
        ValidateTemplate(template);
        lock (_sync)
        {
            if (_aliases.ContainsKey(template.Id))
                throw new InvalidOperationException($"Template id '{template.Id}' is already registered as an alias.");
            if (_templates.ContainsKey(template.Id))
                throw new InvalidOperationException($"Template '{template.Id}' is already registered.");

            var candidate = new Dictionary<string, AgentTemplate>(_templates, StringComparer.Ordinal) { [template.Id] = template };
            ValidateChain(template, candidate, allowWidening);
            _templates = candidate;
        }
    }

    /// <summary>Registers an alias that resolves to an existing canonical template.</summary>
    /// <param name="alias">A distinct name for the canonical template.</param>
    /// <param name="templateId">The registered canonical template identifier.</param>
    /// <exception cref="ArgumentException">An identifier is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The alias collides with a template or alias, or the target does not exist.</exception>
    public void RegisterAlias(string alias, string templateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        lock (_sync)
        {
            if (_templates.ContainsKey(alias) || _aliases.ContainsKey(alias))
                throw new InvalidOperationException($"Alias '{alias}' is already in use.");
            if (!_templates.ContainsKey(templateId))
                throw new InvalidOperationException($"Alias target template '{templateId}' is not registered.");

            var candidate = new Dictionary<string, string>(_aliases, StringComparer.Ordinal) { [alias] = templateId };
            _aliases = candidate;
        }
    }

    /// <summary>
    /// Records an advisory migration from a registered template or alias to a registered template.
    /// Migrations never redirect <see cref="Resolve(string)"/>; persisted identities continue to resolve as recorded.
    /// Hosts may use <see cref="TryGetMigration(string, out string?)"/> to suggest upgrades for new registrations.
    /// </summary>
    /// <param name="oldTemplateId">A registered template identifier or alias to retain as the source identity.</param>
    /// <param name="newTemplateId">The registered suggested replacement template.</param>
    /// <exception cref="ArgumentException">An identifier is null, empty, whitespace, or both identifiers are equal.</exception>
    /// <exception cref="InvalidOperationException">The source or target is invalid, the source already has a migration, or the migration creates a cycle.</exception>
    public void RegisterMigration(string oldTemplateId, string newTemplateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldTemplateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newTemplateId);
        if (StringComparer.Ordinal.Equals(oldTemplateId, newTemplateId))
            throw new ArgumentException("Migration source and target must be distinct.", nameof(newTemplateId));

        lock (_sync)
        {
            if (!_templates.ContainsKey(oldTemplateId) && !_aliases.ContainsKey(oldTemplateId))
                throw new InvalidOperationException($"Migration source '{oldTemplateId}' is not registered.");
            if (!_templates.ContainsKey(newTemplateId))
                throw new InvalidOperationException($"Migration target '{newTemplateId}' is not registered.");
            if (_migrations.ContainsKey(oldTemplateId))
                throw new InvalidOperationException($"Migration source '{oldTemplateId}' already has a migration.");

            var candidate = new Dictionary<string, string>(_migrations, StringComparer.Ordinal) { [oldTemplateId] = newTemplateId };
            if (HasMigrationCycle(candidate))
                throw new InvalidOperationException("Template migrations cannot contain cycles.");
            _migrations = candidate;
        }
    }

    /// <summary>Gets the advisory migration target for a registered template or alias.</summary>
    /// <param name="oldTemplateId">The source template identifier or alias.</param>
    /// <param name="newTemplateId">The suggested registered replacement, or null when no migration exists.</param>
    /// <returns>true when an advisory migration is recorded; otherwise false.</returns>
    public bool TryGetMigration(string oldTemplateId, out string? newTemplateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldTemplateId);
        lock (_sync)
            return _migrations.TryGetValue(oldTemplateId, out newTemplateId);
    }

    /// <summary>Gets the resolved template, with derived values applied after its base values.</summary>
    /// <param name="id">The template identifier.</param>
    /// <returns>The resolved template.</returns>
    public AgentTemplate Resolve(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_sync)
        {
            var canonicalId = _aliases.TryGetValue(id, out var aliasTarget) ? aliasTarget : id;
            if (!_templates.TryGetValue(canonicalId, out var template))
                throw new KeyNotFoundException($"Template '{id}' was not found.");
            return template.BaseTemplateId is null ? template : Merge(ResolveBase(template, _templates), template);
        }
    }

    private static bool HasMigrationCycle(IReadOnlyDictionary<string, string> migrations)
    {
        foreach (var source in migrations.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = source;
            while (migrations.TryGetValue(current, out var next))
            {
                current = next;
                if (!visited.Add(current) || StringComparer.Ordinal.Equals(current, source))
                    return true;
            }
        }
        return false;
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
        var budgetViolation = GetBudgetViolation(child.Budget, parent.Budget);
        if (budgetViolation is not null)
            throw new InvalidOperationException($"Template '{child.Id}' widens budget field '{budgetViolation.Value.Field}' from {budgetViolation.Value.ChildValue} to {budgetViolation.Value.ParentValue} relative to base '{parent.Id}'.");

        if (!child.AllowedToolIds.IsSubsetOf(parent.AllowedToolIds) ||
            !(child.AllowedScopes ?? new HashSet<string>(StringComparer.Ordinal)).IsSubsetOf(parent.AllowedScopes ?? new HashSet<string>(StringComparer.Ordinal)))
            throw new InvalidOperationException($"Template '{child.Id}' widens the capabilities of base '{parent.Id}'.");
    }

    private static (string Field, object ChildValue, object ParentValue)? GetBudgetViolation(AgentBudget child, AgentBudget parent)
    {
        if (child.MaxToolCalls > parent.MaxToolCalls) return (nameof(child.MaxToolCalls), child.MaxToolCalls, parent.MaxToolCalls);
        if (child.MaxModelCalls > parent.MaxModelCalls) return (nameof(child.MaxModelCalls), child.MaxModelCalls, parent.MaxModelCalls);
        if (child.MaxTokens > parent.MaxTokens) return (nameof(child.MaxTokens), child.MaxTokens, parent.MaxTokens);
        if (child.MaxConcurrentAgents > parent.MaxConcurrentAgents) return (nameof(child.MaxConcurrentAgents), child.MaxConcurrentAgents, parent.MaxConcurrentAgents);
        if (child.MaxQueuedAgents > parent.MaxQueuedAgents) return (nameof(child.MaxQueuedAgents), child.MaxQueuedAgents, parent.MaxQueuedAgents);
        if (child.MaxTotalAgents > parent.MaxTotalAgents) return (nameof(child.MaxTotalAgents), child.MaxTotalAgents, parent.MaxTotalAgents);
        if (parent.MaxRuntime is not null && (child.MaxRuntime is null || child.MaxRuntime > parent.MaxRuntime))
            return (nameof(child.MaxRuntime), child.MaxRuntime?.ToString() ?? "unlimited", parent.MaxRuntime.Value.ToString());
        return null;
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
