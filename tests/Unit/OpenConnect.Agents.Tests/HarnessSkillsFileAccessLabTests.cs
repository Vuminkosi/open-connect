// <copyright file="HarnessSkillsFileAccessLabTests.cs">
// Deterministic native Harness skills + file-access sandbox lab (MAF 1.27.0).
// Skill source loading/metadata/progressive disclosure via the native skill API
// (AgentSkillFrontmatter / AgentInlineSkill / AgentInMemorySkillsSource /
// AgentFileSkillsSource / AgentSkillsProvider), unsupported executable targets
// denied by the native boundaries, and file-access sandbox/allowlist via the
// native AgentFileStore boundary (FileSystemAgentFileStore rooted at test temp;
// FileAccessStore opt-in null default denies unsafe host access).
// No custom skills engine, no network, no host filesystem writes outside test
// temp (cleaned up). Lab-safe Harness configuration explicitly disables the
// CWD-default skill discovery and file-memory providers.
// </copyright>

using Microsoft.Agents.AI;
#pragma warning disable MAAI001 // Skills/file-access surface is evaluation-only in MAF 1.27.0; this lab pins and records the exact surface.
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves native Harness skills loading + file-access sandbox on MAF 1.27.0
/// with deterministic in-test fixtures (no network, no host writes).
/// </summary>
public sealed class HarnessSkillsFileAccessLabTests
{
    private readonly ITestOutputHelper _output;

    public HarnessSkillsFileAccessLabTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Skill_frontmatter_carries_discovery_metadata_and_validates()
    {
        // L1 (discovery) layer per the Agent Skills specification: minimal
        // metadata advertised without loading full skill content.
        var frontmatter = new AgentSkillFrontmatter(
            "lab-skill",
            "Lab skill for deterministic harness tests.",
            "lab-only");
        Assert.Equal("lab-skill", frontmatter.Name);
        Assert.Equal("Lab skill for deterministic harness tests.", frontmatter.Description);
        Assert.Equal("lab-only", frontmatter.Compatibility);
        _output.WriteLine($"Frontmatter: name={frontmatter.Name}, description={frontmatter.Description}, compatibility={frontmatter.Compatibility}.");

        // Native spec validation rejects invalid names/descriptions.
        var invalid = Assert.Throws<ArgumentException>(() => new AgentSkillFrontmatter(
            "Bad Name With Spaces",
            "Lab skill with an invalid name.",
            "lab-only"));
        _output.WriteLine($"Invalid name denied natively: {invalid.GetType().Name}: {invalid.Message}");
    }

    [Fact]
    public async Task In_memory_source_loads_skill_and_defers_full_content()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        const string Body = "LAB INSTRUCTIONS BODY: deterministic progressive-disclosure fixture.";
        var skill = new AgentInlineSkill(
            new AgentSkillFrontmatter("lab-inline-skill", "Lab inline skill.", "lab-only"),
            Body,
            null,
            null);
        skill.AddResource("lab-notes", "lab-resource-value", "Lab resource.");
        _output.WriteLine("AgentInlineSkill built in-memory (no filesystem, no network).");

        // In-memory source loading via the native skill-source API.
        using var source = new AgentInMemorySkillsSource(new AgentSkill[] { skill });
        var lab = await CreateLabContextAsync(ct);
        var skills = (await source.GetSkillsAsync(new AgentSkillsSourceContext(lab.Agent, lab.Session), ct)).ToList();
        Assert.Single(skills);
        Assert.Equal("lab-inline-skill", skills[0].Frontmatter.Name);
        Assert.Equal("Lab inline skill.", skills[0].Frontmatter.Description);
        _output.WriteLine($"Loaded {skills.Count} skill(s) from AgentInMemorySkillsSource; L1 metadata only (no body yet).");

        // Progressive disclosure: L1 metadata was already visible above; the
        // full L2 body loads on demand and the L3 resource reads on demand.
        var content = await skills[0].GetContentAsync(ct);
        Assert.Contains(Body, content, StringComparison.Ordinal);
        Assert.DoesNotContain(Body, skills[0].Frontmatter.Description, StringComparison.Ordinal);
        var resource = await skills[0].GetResourceAsync("lab-notes", ct);
        Assert.NotNull(resource);
        Assert.Null(await skills[0].GetScriptAsync("missing-script", ct));
        _output.WriteLine($"L2 content length={content.Length}; L3 resource 'lab-notes' present; missing script returns null.");

        // Native provider accepts the in-memory skills (owns its pipeline).
        using var provider = new AgentSkillsProvider(new AgentSkill[] { skill });
        Assert.NotNull(provider);
        _output.WriteLine($"AgentSkillsProvider constructed over in-memory skills: {provider.GetType().FullName}.");
    }

    [Fact]
    public async Task File_skill_source_loads_test_local_manifest_and_ignores_executable_target()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        // Test-local manifest only: skill dir + SKILL.md under test temp, plus
        // an executable file that must NOT be surfaced as a runnable script.
        var root = NewTempRoot();
        try
        {
            var skillDir = Path.Combine(root, "lab-file-skill");
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(
                Path.Combine(skillDir, "SKILL.md"),
                """
                ---
                name: lab-file-skill
                description: Lab file skill for deterministic harness tests.
                ---

                # Lab File Skill
                LAB FILE BODY: loaded from the test-local manifest.
                """,
                ct);
            await File.WriteAllTextAsync(Path.Combine(skillDir, "run.exe"), "MZ-lab-stub-not-executed", ct);
            _output.WriteLine($"Test-local skill manifest written under {skillDir} (SKILL.md + unsupported run.exe).");

            using var source = new AgentFileSkillsSource(skillDir, null, null, null);
            var lab = await CreateLabContextAsync(ct);
            var skills = (await source.GetSkillsAsync(new AgentSkillsSourceContext(lab.Agent, lab.Session), ct)).ToList();
            Assert.Single(skills);
            Assert.Equal("lab-file-skill", skills[0].Frontmatter.Name);
            var content = await skills[0].GetContentAsync(ct);
            Assert.Contains("LAB FILE BODY", content, StringComparison.Ordinal);
            _output.WriteLine($"File source loaded skill '{skills[0].Frontmatter.Name}'; SKILL.md body length={content.Length}.");

            // Unsupported target: executables are outside the default script
            // allowlist (.py/.js/.sh/.ps1/.cs/.csx), so no script is surfaced.
            var defaults = new AgentFileSkillsSourceOptions();
            Assert.Null(defaults.AllowedScriptExtensions);
            Assert.Null(await skills[0].GetScriptAsync("run.exe", ct));
            _output.WriteLine("Unsupported target documented: run.exe has no script surface (default AllowedScriptExtensions exclude executables; GetScriptAsync returns null).");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task File_skill_script_without_runner_is_denied()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var root = NewTempRoot();
        try
        {
            var skillDir = Path.Combine(root, "lab-script-skill");
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(
                Path.Combine(skillDir, "SKILL.md"),
                """
                ---
                name: lab-script-skill
                description: Lab script skill for deterministic harness tests.
                ---

                # Lab Script Skill
                LAB SCRIPT BODY.
                """,
                ct);
            await File.WriteAllTextAsync(Path.Combine(skillDir, "helper.py"), "print('lab-stub-not-executed')\n", ct);

            // No script runner supplied: discovery still works, but execution
            // is denied natively (no custom engine, no subprocess spawned).
            using var source = new AgentFileSkillsSource(skillDir, null, null, null);
            var lab = await CreateLabContextAsync(ct);
            var skills = (await source.GetSkillsAsync(new AgentSkillsSourceContext(lab.Agent, lab.Session), ct)).ToList();
            Assert.Single(skills);
            var script = await skills[0].GetScriptAsync("helper.py", ct);
            Assert.NotNull(script);
            _output.WriteLine($"Discovered script surface: name={script!.Name} (runner not provided).");

            var denied = await Assert.ThrowsAsync<InvalidOperationException>(
                () => script.RunAsync(skills[0], null, serviceProvider: null, ct));
            _output.WriteLine($"Script execution denied natively: {denied.GetType().Name}: {denied.Message}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task File_store_sandbox_allows_rooted_path_and_rejects_escape()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var root = NewTempRoot();
        try
        {
            // Native sandbox: all paths resolve relative to the configured root.
            var store = new FileSystemAgentFileStore(root);

            // Allowlist behaviour: a path inside the root succeeds.
            await store.WriteAsync("notes/lab.md", "lab-body", ct);
            Assert.Equal("lab-body", await store.ReadAsync("notes/lab.md", ct));
            Assert.True(await store.FileExistsAsync("notes/lab.md", ct));
            _output.WriteLine("Allowed path notes/lab.md round-tripped inside the sandbox root.");

            // Sandbox boundary: lexical traversal and absolute host paths are
            // rejected natively with ArgumentException (no host access).
            var traversal = await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync("../escape.md", ct));
            var absolute = await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(
                Path.Combine(Path.GetTempPath(), "oc-lab-should-not-exist.md"), ct));
            _output.WriteLine($"Traversal denied: {traversal.Message}");
            _output.WriteLine($"Absolute host path denied: {absolute.Message}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unsafe_host_access_is_denied_by_default()
    {
        // File access is opt-in: null (default) => no FileAccessProvider, so
        // the agent has no file access tools and cannot touch the host.
        var options = new HarnessAgentOptions();
        Assert.Null(options.FileAccessStore);
        Assert.Null(options.FileAccessProviderOptions);
        _output.WriteLine("HarnessAgentOptions default: FileAccessStore=null, FileAccessProviderOptions=null (no file access tools).");

        // Skill discovery and file memory default to CWD-backed providers, so
        // a deterministic lab must explicitly disable them (no host writes).
        Assert.False(options.DisableAgentSkillsProvider);
        Assert.Null(options.AgentSkillsSource);
        Assert.False(options.DisableFileMemory);
        var labSafe = new HarnessAgentOptions
        {
            DisableAgentSkillsProvider = true,
            DisableFileMemory = true,
            DisableWebSearch = true,
            DisableOpenTelemetry = true,
        };
        Assert.True(labSafe.DisableAgentSkillsProvider);
        Assert.True(labSafe.DisableFileMemory);
        _output.WriteLine("Lab-safe configuration: DisableAgentSkillsProvider=true, DisableFileMemory=true (CWD defaults opted out).");
    }

    private static string NewTempRoot()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "oc-harness-lab-" + Guid.NewGuid().ToString("N"))).FullName;

    private async Task<(HarnessAgent Agent, AgentSession Session)> CreateLabContextAsync(CancellationToken ct)
    {
        // Native context carrier: a real HarnessAgent over a deterministic echo
        // stub (no model calls, no network) with every CWD/host-touching
        // built-in disabled, so GetSkillsAsync runs against lab fixtures only.
        var options = new HarnessAgentOptions
        {
            Id = "lab-skills-fileaccess",
            Name = "LabSkillsFileAccess",
            Description = "Deterministic skills/file-access lab agent.",
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableOpenTelemetry = true,
        };
        var client = new LabEchoChatClient();
        HarnessAgent agent = client.AsHarnessAgent(options);
        AgentSession session = await agent.CreateSessionAsync(ct);
        _output.WriteLine($"Lab context: agent={agent.GetType().FullName}, session created (built-ins disabled).");
        return (agent, session);
    }

    private sealed class LabEchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("harness-skills-fileaccess-lab");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ack:lab-echo")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
