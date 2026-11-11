// <copyright file="StructuredOutputValidationTests.cs">
// Tests for StructuredOutputValidator (OC2-D-CONFIG-015).
// Validates structured model outputs before use, bounded repair/retry,
// and zero tool invocation / external effect on invalid output.
// </copyright>

using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class StructuredOutputValidationTests
{
    [Fact]
    public async Task ValidOutput_Passes_And_ExecutesAction()
    {
        var validator = new StructuredOutputValidator(maxRepairs: 2);
        var toolInvocations = 0;
        const string rawJson = """{"status": "ok", "count": 42}""";

        var result = await validator.ExecuteIfValidAsync(
            outputSchemaRef: "properties:status,count",
            rawContent: rawJson,
            validAction: (validated, _) =>
            {
                toolInvocations++;
                return ValueTask.FromResult("executed");
            });

        Assert.Equal("executed", result);
        Assert.Equal(1, toolInvocations);
    }

    [Fact]
    public async Task InvalidOutput_Blocked_WithZeroToolInvocation()
    {
        var validator = new StructuredOutputValidator(maxRepairs: 2);
        var toolInvocations = 0;
        const string invalidJson = "this is not json at all";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await validator.ExecuteIfValidAsync(
                outputSchemaRef: "object",
                rawContent: invalidJson,
                validAction: (validated, _) =>
                {
                    toolInvocations++;
                    return ValueTask.FromResult("executed");
                });
        });

        Assert.Contains("validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, toolInvocations); // Crucial gate: zero tool invocation on invalid output!
    }

    [Fact]
    public async Task RepairableWithinBound_Passes_AfterRepair()
    {
        var validator = new StructuredOutputValidator(maxRepairs: 2);
        var toolInvocations = 0;
        var repairAttempts = 0;
        const string initialMalformed = """{"status": "partial" """; // missing closing brace

        var result = await validator.ExecuteIfValidAsync(
            outputSchemaRef: "properties:status",
            rawContent: initialMalformed,
            validAction: (validated, _) =>
            {
                toolInvocations++;
                return ValueTask.FromResult("repaired-and-executed");
            },
            repairCallback: (broken, reason) =>
            {
                repairAttempts++;
                return ValueTask.FromResult("""{"status": "repaired"}""");
            });

        Assert.Equal("repaired-and-executed", result);
        Assert.Equal(1, repairAttempts);
        Assert.Equal(1, toolInvocations);
    }

    [Fact]
    public async Task UnrepairableAfterBound_FailsClosed_WithZeroToolInvocation()
    {
        var validator = new StructuredOutputValidator(maxRepairs: 2);
        var toolInvocations = 0;
        var repairAttempts = 0;
        const string badContent = "persistently bad output";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await validator.ExecuteIfValidAsync(
                outputSchemaRef: "object",
                rawContent: badContent,
                validAction: (validated, _) =>
                {
                    toolInvocations++;
                    return ValueTask.FromResult("executed");
                },
                repairCallback: (broken, reason) =>
                {
                    repairAttempts++;
                    return ValueTask.FromResult("still bad content");
                });
        });

        Assert.Contains("validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, repairAttempts); // Hit exactly maxRepairs (2)
        Assert.Equal(0, toolInvocations); // Zero tool invocation!
    }

    [Fact]
    public async Task SchemaMismatch_MissingRequiredProperty_Rejected()
    {
        var validator = new StructuredOutputValidator(maxRepairs: 0);
        const string jsonMissingProperty = """{"other": 123}""";

        var validation = await validator.ValidateWithRepairAsync(
            outputSchemaRef: "properties:requiredProp",
            rawContent: jsonMissingProperty);

        Assert.False(validation.IsValid);
        Assert.Contains("Missing required property 'requiredProp'", validation.ErrorMessage);
    }
}
