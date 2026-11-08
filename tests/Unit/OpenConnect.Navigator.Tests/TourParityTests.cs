using System.Text.Json;
using OpenConnect.Navigator;
using Xunit;

namespace OpenConnect.Navigator.Tests;

public sealed class TourParityTests
{
    [Fact]
    public void Tour_schema_required_fields_and_bounds_match_runtime_validator()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(FindSchemaPath()));
        var root = schema.RootElement;
        var stepSchema = root.GetProperty("properties").GetProperty("steps");
        Assert.Equal(NavigatorTourValidator.AbsoluteMaxSteps, stepSchema.GetProperty("maxItems").GetInt32());

        var itemSchema = stepSchema.GetProperty("items");
        Assert.Contains("type", itemSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var conditionals = itemSchema.GetProperty("allOf").EnumerateArray().ToArray();
        Assert.Equal(2, conditionals.Length);
        Assert.Equal(new[] { "execute", "focus", "highlight", "locate" }, conditionals[0]
            .GetProperty("if").GetProperty("properties").GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(x => x.GetString()).Order().ToArray());
        Assert.Contains("targetId", conditionals[0].GetProperty("then").GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("action", conditionals[1].GetProperty("then").GetProperty("required").EnumerateArray().Select(x => x.GetString()));

        var validator = new NavigatorTourValidator();
        Assert.Throws<ArgumentException>(() => validator.Validate(Plan(new(NavigatorTourStepType.Highlight))));
        Assert.Throws<ArgumentException>(() => validator.Validate(Plan(new(NavigatorTourStepType.Execute, "target"))));
        validator.Validate(Plan(new(NavigatorTourStepType.Highlight, "target")));
        validator.Validate(Plan(new(NavigatorTourStepType.Execute, "target", Action: NavigatorActionType.Observe)));
        Assert.Throws<ArgumentException>(() => validator.Validate(new("tour", 1,
            Enumerable.Repeat(new NavigatorTourStep(NavigatorTourStepType.Explain), NavigatorTourValidator.AbsoluteMaxSteps + 1).ToArray(), "/")));
    }

    private static NavigatorTourPlan Plan(NavigatorTourStep step) => new("tour", 1, [step], "/");

    private static string FindSchemaPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts", "navigator", "v1", "tour.json")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new FileNotFoundException("Navigator tour contract was not found."), "contracts", "navigator", "v1", "tour.json");
    }
}
