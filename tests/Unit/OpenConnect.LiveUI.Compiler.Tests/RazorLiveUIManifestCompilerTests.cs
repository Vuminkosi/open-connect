using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Compiler;
using Xunit;

namespace OpenConnect.LiveUI.Compiler.Tests;

public sealed class RazorLiveUIManifestCompilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openconnect-liveui-tests", Guid.NewGuid().ToString("N"));

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
            <MudPaper data-oc-target="results" aria-label="Results" />
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
                                                target.Capabilities.Contains(LiveUICapability.Filter));
        Assert.Contains(route.Targets, target => target.Kind == LiveUITargetKind.Input && target.Label == "Search tasks..." &&
                                                target.Capabilities.Contains(LiveUICapability.Filter));
        Assert.Contains(route.Targets, target => target.Kind == LiveUITargetKind.List && target.Label == "Task list" &&
                                                target.Selector == ".mud-list" && target.Capabilities.Contains(LiveUICapability.WaitForState));
        Assert.Contains(route.Targets, target => target.Kind == LiveUITargetKind.Region && target.Label == "Results" &&
                                                target.Selector == "[data-oc-target=\"results\"]" && target.Capabilities.Contains(LiveUICapability.WaitForState));
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
