using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenConnect.Privacy.Tokenization;

namespace OpenConnect.Privacy.Restoration;

/// <summary>Identifies the target destination for token restoration.</summary>
public enum RestorationTarget
{
    /// <summary>Final assistant response presented to the authenticated user.</summary>
    FinalUserOutput,
    /// <summary>Arguments passed to an authorized local/external tool.</summary>
    ToolArguments,
    /// <summary>Internal log or telemetry (never restored).</summary>
    Diagnostics
}

/// <summary>Specifies destination policy and allowed field paths for restoration.</summary>
/// <param name="Target">The destination target.</param>
/// <param name="AllowedFieldPaths">Allowlisted field names or JSON paths when restoring tool arguments.</param>
public sealed record RestorationContext(RestorationTarget Target, IReadOnlySet<string>? AllowedFieldPaths = null);

/// <summary>Enforces authorized path-specific restoration of tokenized entities.</summary>
public interface ITokenRestorationService
{
    /// <summary>Restores authorized tokens in user output or tool arguments.</summary>
    ValueTask<string> RestoreAsync(string content, PrivacyScope scope, RestorationContext context, CancellationToken cancellationToken = default);
}

/// <summary>Default implementation of <see cref="ITokenRestorationService"/> using an encrypted token vault.</summary>
public sealed class TokenRestorationService(ITokenVault vault) : ITokenRestorationService
{
    private static readonly Regex TokenRegex = new(@"\[OC_[A-Za-z0-9_]+\]", RegexOptions.Compiled);
    private readonly ITokenVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));

    /// <inheritdoc />
    public async ValueTask<string> RestoreAsync(string content, PrivacyScope scope, RestorationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (content.Length == 0) return string.Empty;

        // Never restore tokens in diagnostics, logs, or untrusted destinations
        if (context.Target == RestorationTarget.Diagnostics)
        {
            return content;
        }

        if (context.Target == RestorationTarget.FinalUserOutput)
        {
            return await RestoreTokensInTextAsync(content, scope, cancellationToken).ConfigureAwait(false);
        }

        if (context.Target == RestorationTarget.ToolArguments)
        {
            var allowedFields = context.AllowedFieldPaths ?? new HashSet<string>(StringComparer.Ordinal);
            return await RestoreToolArgumentsJsonAsync(content, allowedFields, scope, cancellationToken).ConfigureAwait(false);
        }

        return content;
    }

    private async ValueTask<string> RestoreTokensInTextAsync(string text, PrivacyScope scope, CancellationToken cancellationToken)
    {
        var matches = TokenRegex.Matches(text);
        if (matches.Count == 0) return text;

        var sb = new StringBuilder();
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                sb.Append(text, lastIndex, match.Index - lastIndex);
            }

            var token = match.Value;
            var resolved = await _vault.ResolveAsync(token, scope, cancellationToken).ConfigureAwait(false);
            sb.Append(resolved ?? token);
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            sb.Append(text, lastIndex, text.Length - lastIndex);
        }

        return sb.ToString();
    }

    private async ValueTask<string> RestoreToolArgumentsJsonAsync(string json, IReadOnlySet<string> allowedFields, PrivacyScope scope, CancellationToken cancellationToken)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                foreach (var property in obj.ToArray())
                {
                    if (allowedFields.Contains(property.Key) && property.Value is JsonValue val && val.TryGetValue<string>(out var strVal))
                    {
                        var restored = await RestoreTokensInTextAsync(strVal, scope, cancellationToken).ConfigureAwait(false);
                        obj[property.Key] = restored;
                    }
                }
                return obj.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // If not JSON, leave unchanged
        }
        return json;
    }
}
