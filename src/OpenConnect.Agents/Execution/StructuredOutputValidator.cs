using System.Text.Json;

namespace OpenConnect.Agents.Execution;

/// <summary>Result of structured output validation and bounded repair.</summary>
/// <param name="IsValid">Whether the content is valid against the requested schema reference.</param>
/// <param name="ValidatedContent">The validated content when valid, or null.</param>
/// <param name="ErrorMessage">Diagnostic error message on validation failure, or null.</param>
/// <param name="RepairsAttempted">Count of repair attempts executed.</param>
public sealed record StructuredOutputValidationResult(
    bool IsValid,
    string? ValidatedContent = null,
    string? ErrorMessage = null,
    int RepairsAttempted = 0);

/// <summary>
/// Validates structured model outputs before use, enforcing schema constraints, bounded repair/retry,
/// and fail-closed denial with zero tool or external effect invocation on invalid output.
/// </summary>
public sealed class StructuredOutputValidator
{
    private readonly int _maxRepairs;

    /// <summary>Creates a structured output validator with bounded repairs.</summary>
    /// <param name="maxRepairs">Maximum repair attempts permitted before fail-closed rejection. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">Max repairs is negative.</exception>
    public StructuredOutputValidator(int maxRepairs = 2)
    {
        if (maxRepairs < 0) throw new ArgumentOutOfRangeException(nameof(maxRepairs), "Max repairs must be non-negative.");
        _maxRepairs = maxRepairs;
    }

    /// <summary>
    /// Validates raw content against the specified schema reference, invoking repair callback if needed up to the bound.
    /// </summary>
    /// <param name="outputSchemaRef">Optional schema reference or constraint identifier.</param>
    /// <param name="rawContent">Raw model output string to validate.</param>
    /// <param name="repairCallback">Optional asynchronous repair callback (currentContent, reason) -> repairedContent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result indicating success, validated content, error message, and repair count.</returns>
    public async ValueTask<StructuredOutputValidationResult> ValidateWithRepairAsync(
        string? outputSchemaRef,
        string rawContent,
        Func<string, string, ValueTask<string>>? repairCallback = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentContent = rawContent;
        var repairs = 0;

        while (true)
        {
            var (valid, error) = TryValidate(outputSchemaRef, currentContent);
            if (valid)
            {
                return new StructuredOutputValidationResult(true, currentContent, null, repairs);
            }

            if (repairs >= _maxRepairs || repairCallback == null)
            {
                return new StructuredOutputValidationResult(false, null, error, repairs);
            }

            repairs++;
            try
            {
                currentContent = await repairCallback(currentContent, error ?? "Validation failed").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new StructuredOutputValidationResult(false, null, $"Repair attempt {repairs} failed: {ex.Message}", repairs);
            }
        }
    }

    /// <summary>
    /// Executes a consequential action (e.g., tool invocation) only if the structured output is valid.
    /// On validation failure, execution is blocked and the action is NEVER called.
    /// </summary>
    /// <typeparam name="TResult">Return type of the valid action.</typeparam>
    /// <param name="outputSchemaRef">Optional schema reference or constraint identifier.</param>
    /// <param name="rawContent">Raw model output string to validate.</param>
    /// <param name="validAction">Consequential action to invoke only if validation succeeds.</param>
    /// <param name="repairCallback">Optional repair callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the valid action.</returns>
    /// <exception cref="InvalidOperationException">Validation fails and action is blocked.</exception>
    public async ValueTask<TResult> ExecuteIfValidAsync<TResult>(
        string? outputSchemaRef,
        string rawContent,
        Func<string, CancellationToken, ValueTask<TResult>> validAction,
        Func<string, string, ValueTask<string>>? repairCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validAction);
        var validation = await ValidateWithRepairAsync(outputSchemaRef, rawContent, repairCallback, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Structured output validation failed: {validation.ErrorMessage}. Consequential action blocked.");
        }

        return await validAction(validation.ValidatedContent!, cancellationToken).ConfigureAwait(false);
    }

    private static (bool IsValid, string? Error) TryValidate(string? schemaRef, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "Content is empty or whitespace.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            return (false, $"Invalid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (string.IsNullOrWhiteSpace(schemaRef))
            {
                return (true, null);
            }

            if (schemaRef.StartsWith("properties:", StringComparison.OrdinalIgnoreCase))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return (false, "Expected root JSON object.");

                var required = schemaRef["properties:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var prop in required)
                {
                    if (!doc.RootElement.TryGetProperty(prop, out _))
                        return (false, $"Missing required property '{prop}'.");
                }
            }
            else if (schemaRef.Equals("object", StringComparison.OrdinalIgnoreCase))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return (false, "Expected root JSON object.");
            }
            else if (schemaRef.Equals("array", StringComparison.OrdinalIgnoreCase))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return (false, "Expected root JSON array.");
            }

            return (true, null);
        }
    }
}
