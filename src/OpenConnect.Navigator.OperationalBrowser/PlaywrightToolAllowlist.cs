using System.Text.Json;
using System.Text.RegularExpressions;
using OpenConnect.Navigator;

namespace OpenConnect.Navigator.Playwright;

/// <summary>Maps Navigator's constrained actions to the reviewed Playwright MCP surface.</summary>
/// <remarks>
/// Reviewed against Playwright MCP 1.x tool names. Keep this list pinned to the integration's reviewed version.
/// Fill is denied with <c>sensitive_input_denied</c> when its <c>target</c> descriptor names a credential-like
/// field (password, secret, token, API key, one-time code, card number and similar). This is a fail-closed
/// text heuristic over the descriptor, not a check of the resolved element's type.
/// </remarks>
public static class PlaywrightToolAllowlist
{
    private static readonly IReadOnlyDictionary<NavigatorActionType, string> Tools =
        new Dictionary<NavigatorActionType, string>
        {
            [NavigatorActionType.Observe] = "browser_snapshot",
            [NavigatorActionType.Navigate] = "browser_navigate",
            [NavigatorActionType.Click] = "browser_click",
            [NavigatorActionType.Fill] = "browser_type",
            [NavigatorActionType.Select] = "browser_select_option",
            [NavigatorActionType.Scroll] = "browser_wait_for",
            [NavigatorActionType.Capture] = "browser_take_screenshot"
        };

    /// <summary>Resolves an action only when its arguments satisfy the facade's fail-closed rules.</summary>
    public static bool TryResolve(NavigatorActionType action, JsonElement arguments, Uri? approvedOrigin,
        bool screenshotConsent, out string toolName, out string denialReason)
        => TryResolve(action, arguments, approvedOrigin, screenshotConsent, out toolName, out _, out denialReason);

    /// <summary>Resolves an action and returns only its validated, normalized arguments.</summary>
    public static bool TryResolve(NavigatorActionType action, JsonElement arguments, Uri? approvedOrigin,
        bool screenshotConsent, out string toolName, out JsonElement normalizedArguments, out string denialReason)
    {
        toolName = string.Empty;
        normalizedArguments = default;
        denialReason = "action_not_allowed";
        if (!Tools.TryGetValue(action, out var candidate))
            return false;
        if (arguments.ValueKind != JsonValueKind.Object)
        { denialReason = "invalid_arguments"; return false; }

        var allowedProperties = action switch
        {
            NavigatorActionType.Observe or NavigatorActionType.Capture => Array.Empty<string>(),
            NavigatorActionType.Navigate => new[] { "url" },
            NavigatorActionType.Click => new[] { "target" },
            NavigatorActionType.Fill or NavigatorActionType.Select => new[] { "target", "value" },
            NavigatorActionType.Scroll => new[] { "timeout" },
            _ => Array.Empty<string>()
        };
        if (arguments.EnumerateObject().Any(p => !allowedProperties.Contains(p.Name, StringComparer.Ordinal)) ||
            allowedProperties.Any(name => arguments.EnumerateObject().Count(p => p.NameEquals(name)) > 1))
        { denialReason = "invalid_arguments"; return false; }

        var normalized = new Dictionary<string, object>();
        if (action == NavigatorActionType.Navigate)
        {
            if (!TryGetString(arguments, "url", out var rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) ||
                url.Scheme is not ("http" or "https") || approvedOrigin is null ||
                !SameOrigin(url, approvedOrigin))
            { denialReason = "origin_not_approved"; return false; }
            normalized["url"] = url.AbsoluteUri;
        }

        if (action == NavigatorActionType.Click && !HasTarget(arguments))
        { denialReason = "explicit_target_required"; return false; }
        if (action == NavigatorActionType.Click && TryGetString(arguments, "target", out var clickTarget))
            normalized["target"] = clickTarget.Trim();

        if (action == NavigatorActionType.Fill)
        {
            if (!HasTarget(arguments) || !TryGetString(arguments, "value", out var fillValue))
            { denialReason = "explicit_target_required"; return false; }
            TryGetString(arguments, "target", out var fillTarget);
            if (IsCredentialLikeTarget(fillTarget))
            { denialReason = "sensitive_input_denied"; return false; }
            normalized["target"] = fillTarget.Trim();
            normalized["value"] = fillValue;
        }
        if (action == NavigatorActionType.Select)
        {
            if (!HasTarget(arguments) || !TryGetString(arguments, "value", out var selectValue))
            { denialReason = "invalid_arguments"; return false; }
            TryGetString(arguments, "target", out var selectTarget);
            normalized["target"] = selectTarget.Trim();
            normalized["value"] = selectValue.Trim();
        }

        if (action == NavigatorActionType.Capture && !screenshotConsent)
        { denialReason = "capture_consent_required"; return false; }

        if (action == NavigatorActionType.Scroll)
        {
            if (!arguments.TryGetProperty("timeout", out var timeout) || !timeout.TryGetInt32(out var milliseconds) ||
                milliseconds is < 0 or > 10_000)
            { denialReason = "wait_timeout_out_of_range"; return false; }
            normalized["timeout"] = milliseconds;
        }

        toolName = candidate;
        normalizedArguments = JsonSerializer.SerializeToElement(normalized);
        denialReason = string.Empty;
        return true;
    }

    private static bool TryGetString(JsonElement value, string property, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        result = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool HasTarget(JsonElement value) =>
        TryGetString(value, "target", out var target) && !string.IsNullOrWhiteSpace(target);

    private static bool SameOrigin(Uri requested, Uri approved) =>
        string.Equals(requested.Scheme, approved.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(requested.Host, approved.Host, StringComparison.OrdinalIgnoreCase) && requested.Port == approved.Port;

    // Matched anywhere in the target after separators are removed, so "API key", "api_key" and "apiKey" match.
    private static readonly string[] CredentialTerms =
    [
        "password", "passcode", "passwd", "secret", "token", "credential", "apikey", "privatekey",
        "onetimecode", "onetimepassword", "cardnumber", "securitycode", "socialsecurity"
    ];

    // Short abbreviations are matched only as whole words to avoid hits inside ordinary words (e.g. "footprint").
    private static readonly string[] CredentialWords = ["otp", "pin", "cvv", "cvc", "ssn", "pwd"];

    /// <summary>
    /// Heuristic: denies Fill when the target descriptor text names a credential-like field. This is a text
    /// check on the caller-supplied descriptor only. It cannot see the resolved element, so it is not a
    /// substitute for element-type checks (for example <c>input[type=password]</c>) and a neutral descriptor
    /// can still address a sensitive field.
    /// </summary>
    private static bool IsCredentialLikeTarget(string target)
    {
        var compact = new string(target.Where(char.IsLetterOrDigit).ToArray());
        if (CredentialTerms.Any(term => compact.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return true;
        var words = Regex.Split(target, "[^\\p{L}\\p{Nd}]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return words.Any(word => CredentialWords.Contains(word, StringComparer.OrdinalIgnoreCase));
    }
}

// This type retains its historical namespace after consolidation into the surviving OperationalBrowser project.
