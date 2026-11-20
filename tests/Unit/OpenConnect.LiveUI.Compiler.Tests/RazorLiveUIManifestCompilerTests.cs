using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Compiler;
using Microsoft.Extensions.Options;
using Xunit;

namespace OpenConnect.LiveUI.Compiler.Tests;

public sealed class RazorLiveUIManifestCompilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openconnect-liveui-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Compile_DoesNotUseAriaLabelledbyIdsAsLabelsInStaticOrDynamicMarkup()
    {
        Write("Static.razor", """
            @page "/static"
            <span id="save-lbl">Save report</span><button aria-labelledby="save-lbl">Save report</button>
            <input aria-labelledby="name-lbl" />
            """);
        Write("Dynamic.razor", """
            @page "/dynamic"
            @if (true)
            {
                <span id="save-lbl">Save report</span><button aria-labelledby="save-lbl" @onclick="Save">Save report</button>
                <input aria-labelledby="name-lbl" @onchange="Changed" />
            }
            """);

        var result = new RazorLiveUIManifestCompiler().Compile(_root, Path.Combine(_root, "manifest.json"));

        Assert.True(result.Success);
        var routes = Assert.IsType<LiveUIManifest>(result.Manifest).Routes;
        foreach (var route in routes)
        {
            Assert.Contains(route.Targets, target => target.Kind == LiveUITargetKind.Button && target.Label == "Save report");
            Assert.DoesNotContain(route.Targets, target => target.Label is "save-lbl" or "name-lbl");
        }
        Assert.Equal(routes.Single(route => route.Path == "/static").Targets.Select(target => (target.Kind, target.Label)).ToArray(),
            routes.Single(route => route.Path == "/dynamic").Targets.Select(target => (target.Kind, target.Label)).ToArray());
    }

    [Fact]
    public void Compile_DiscoversRoutesAndUnannotatedExistingControls()
    {
        Write("Pages/Home.razor", """
            @page "/"
            <PageTitle>Task Management</PageTitle>
            <MudTabs>
                <MudTabPanel Text="All" />
                <MudTabPanel Text="Active" />
                <MudTabPanel Text="Completed" />
            </MudTabs>
            <MudTextField Placeholder="Search tasks..." />
            <MudList T="Task"><MudListItem>Report ready</MudListItem></MudList>
            <MudPaper data-oc-target="results" aria-label="Results"><MudButton>Save results</MudButton></MudPaper>
            <input type="password" aria-label="Secret" />
            """);

        var output = Path.Combine(_root, "obj", "liveui.json");
        var result = new RazorLiveUIManifestCompiler().Compile(_root, output);

        Assert.True(result.Success);
        var manifest = Assert.IsType<LiveUIManifest>(result.Manifest);
        var route = Assert.Single(manifest.Routes);
        Assert.Equal("/", route.Path);
        Assert.Equal("Task Management", route.Title);
        Assert.Contains(route.Targets, target => target.Kind == LiveUITargetKind.Tab && target.Label == "Completed" &&
                                                target.Capabilities.Contains(LiveUICapability.Filter) &&
                                                target.Capabilities.Contains(LiveUICapability.WaitForState));
        Assert.Contains(route.Targets, target => target.Kind == LiveUITargetKind.Input && target.Label == "Search tasks..." &&
                                                target.Capabilities.Contains(LiveUICapability.Filter));
        Assert.Contains(route.Targets, target => target.Kind == LiveUITargetKind.List && target.Label == "Task list" &&
                                                target.Selector == ".mud-list" && target.Capabilities.Contains(LiveUICapability.WaitForState));
        var resultRegion = Assert.Single(route.Targets, target => target.Kind == LiveUITargetKind.Region && target.Label == "Results");
        Assert.False(resultRegion.RequiresConfirmation);
        Assert.Equal("[data-oc-target=\"results\"]", resultRegion.Selector);
        Assert.Contains(LiveUICapability.WaitForState, resultRegion.Capabilities);
        Assert.False(Assert.Single(route.Targets, target => target.Kind == LiveUITargetKind.List).RequiresConfirmation);
        var saveButton = Assert.Single(route.Targets, target => target.Kind == LiveUITargetKind.Button && target.Label == "Save results");
        Assert.Equal(resultRegion.Id, saveButton.ParentId);
        Assert.DoesNotContain(route.Targets, target => target.Label == "Secret");
        Assert.Empty(new LiveUIManifestValidator().Validate(manifest));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Compile_FollowsLocalComponentReferencesAndMarksMutationsForConfirmation()
    {
        Write("Pages/Home.razor", "@page \"/\"\n<TaskActions />");
        Write("Components/TaskActions.razor", "<MudButton OnClick=\"@SaveAsync\">Save</MudButton><MudIconButton aria-label=\"Delete task\" OnClick=\"@DeleteAsync\" />");

        var result = new RazorLiveUIManifestCompiler().Compile(_root, Path.Combine(_root, "manifest.json"), "sample-build");
        var targets = Assert.Single(Assert.IsType<LiveUIManifest>(result.Manifest).Routes).Targets;
        Assert.Contains(targets, target => target.Label == "Save" && target.RequiresConfirmation);
        Assert.Contains(targets, target => target.Label == "Delete task" && target.RequiresConfirmation);
    }

    [Fact]
    public void Compile_RequiresConfirmationForInteractiveControlsWithoutLocalHandlers()
    {
        Write("Pages/Home.razor", """
            @page "/"
            <form><button type="submit">Submit report</button></form>
            <input type="checkbox" aria-label="Approve report" @onchange="Toggle" />
            <input aria-label="Report title" @oninput="Changed" />
            <select aria-label="Priority" @onchange="Changed"><option>High</option></select>
            <a href="/other">Other page</a>
            <pre><form><button type="submit">Code example</button></form></pre>
            """);

        var result = new RazorLiveUIManifestCompiler().Compile(_root, Path.Combine(_root, "manifest.json"));
        var targets = Assert.Single(Assert.IsType<LiveUIManifest>(result.Manifest).Routes).Targets;

        foreach (var label in new[] { "Submit report", "Approve report", "Report title", "Priority", "Other page" })
            Assert.True(Assert.Single(targets, target => target.Label == label).RequiresConfirmation, label);
        Assert.DoesNotContain(targets, target => target.Label == "Code example");
    }

    [Fact]
    public void Compile_UsesSameConfirmationPolicyForStaticAndConditionalControls()
    {
        Write("Static.razor", "@page \"/static\"\n<form><button type=\"submit\">Submit</button></form><input aria-label=\"Title\" />");
        Write("Conditional.razor", "@page \"/conditional\"\n@if (true) { <form><button type=\"submit\">Submit</button></form><input aria-label=\"Title\" /> }");
        var result = new RazorLiveUIManifestCompiler().Compile(_root, Path.Combine(_root, "manifest.json"));
        var routes = Assert.IsType<LiveUIManifest>(result.Manifest).Routes;
        var staticTargets = routes.Single(route => route.Path == "/static").Targets;
        var conditionalTargets = routes.Single(route => route.Path == "/conditional").Targets;

        foreach (var label in new[] { "Submit", "Title" })
        {
            Assert.True(Assert.Single(staticTargets, target => target.Label == label).RequiresConfirmation);
            Assert.True(Assert.Single(conditionalTargets, target => target.Label == label).RequiresConfirmation);
        }
    }

    [Fact]
    public async Task CompiledSubmitWithoutConfirmationProviderIsDeniedBeforeDispatch()
    {
        Write("Home.razor", "@page \"/\"\n<form><button type=\"submit\">Submit report</button></form>");
        var result = new RazorLiveUIManifestCompiler().Compile(_root, Path.Combine(_root, "manifest.json"));
        var manifest = Assert.IsType<LiveUIManifest>(result.Manifest);
        var route = Assert.Single(manifest.Routes);
        var target = Assert.Single(route.Targets, target => target.Label == "Submit report");
        var runtime = new TestRuntime();
        var orchestrator = new LiveUIOrchestrator(new TestManifestProvider(manifest), new AllowAuthorizer(), runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());
        var action = new LiveUIAction("submit-1", LiveUISchema.CurrentVersion, "session", manifest.ContentHash,
            route.Id, target.Id, LiveUIActionType.Click, new LiveUIActionParameters(), TimeSpan.FromSeconds(1));

        var receipt = await orchestrator.ExecuteAsync(action);

        Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome);
        Assert.Equal("approval_required", receipt.Code);
        Assert.Equal(0, runtime.Calls);
    }

    private sealed class TestManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<LiveUIManifest?>(manifest);
    }

    private sealed class AllowAuthorizer : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(true, "allowed"));
    }

    private sealed class TestRuntime : ILiveUIRuntime
    {
        public int Calls { get; private set; }
        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "unexpected", true));
        }
    }

    [Fact]
    public void Compile_ExcludesSensitiveInputsFromLabelsMetadataAndDynamicTypes()
    {
        Write("Pages/Home.razor", """
            @page "/"
            <MudTextField Label="API key" />
            <MudTextField Label="Account PIN" />
            <MudTextField Label="Password entry" InputType="InputType.Password" />
            <MudTextField Label="Shipping address" />
            <input name="api_token" aria-label="Account" />
            <input autocomplete="cc-number" aria-label="Card entry" />
            <input type="@dynamicType" aria-label="Dynamic input" />
            """);

        var result = new RazorLiveUIManifestCompiler().Compile(_root, Path.Combine(_root, "manifest.json"));
        var targets = Assert.Single(Assert.IsType<LiveUIManifest>(result.Manifest).Routes).Targets;

        Assert.DoesNotContain(targets, target => target.Label is "API key" or "Account PIN" or "Password entry" or "api_token" or "Card entry" or "Dynamic input");
        Assert.Contains(targets, target => target.Label == "Shipping address" && target.Kind == LiveUITargetKind.Input);
    }

    [Fact]
    public void Compile_IsDeterministicAndReportsAmbiguousRepeatedTargets()
    {
        Write("Home.razor", "@page \"/\"\n<button>Refresh</button><button>Refresh</button>");
        var compiler = new RazorLiveUIManifestCompiler();
        var firstPath = Path.Combine(_root, "first.json");
        var secondPath = Path.Combine(_root, "second.json");

        var first = compiler.Compile(_root, firstPath, "stable-build");
        var second = compiler.Compile(_root, secondPath, "stable-build");

        Assert.Equal(File.ReadAllText(firstPath), File.ReadAllText(secondPath));
        Assert.Equal(2, Assert.Single(Assert.IsType<LiveUIManifest>(first.Manifest).Routes).Targets.Count(target => target.Label == "Refresh"));
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Code == "LUI1002" && diagnostic.Severity == LiveUICompilerDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Compile_FailsWhenNoPageRouteExists()
    {
        Write("Components/Widget.razor", "<button>Save</button>");

        var result = new RazorLiveUIManifestCompiler().Compile(_root, Path.Combine(_root, "manifest.json"));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "LUI1001");
        Assert.False(File.Exists(Path.Combine(_root, "manifest.json")));
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
