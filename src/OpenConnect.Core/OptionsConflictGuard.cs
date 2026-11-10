using OpenConnect.Core.Configuration;

namespace OpenConnect.Core;

/// <summary>
/// Fail-fast diagnostics for conflicting repeat <c>AddOpenConnect</c> registrations
/// (CONFIG-005 remainder, AG-MAP-006 SLICE-C1). The options pipeline merges
/// delegates silently, so a second call with a differing policy would otherwise
/// be absorbed without any diagnostic. The guard compares a small set of
/// policy-defining dimensions and throws naming the field plus both values.
/// </summary>
/// <remarks>
/// Compared dimensions (additive-only compatibility: everything else still merges
/// via the normal options pipeline):
/// <list type="bullet">
/// <item>Privacy:Enabled, Privacy:Mode, Privacy:OnFailure</item>
/// <item>Per-detector Enabled flags (Rules, GlinerPii, DebertaInjection, Qwen3Guard, PaddleOcr, YuNet)</item>
/// <item>Agents:MaxDelegationDepth</item>
/// </list>
/// A repeat call with a <c>null</c> configure delegate is always treated as
/// idempotent (it cannot introduce a conflict). A repeat call whose snapshot
/// matches on every compared dimension is idempotent.
/// </remarks>
internal sealed class OpenConnectRegistrationSnapshot
{
    public OpenConnectRegistrationSnapshot(OpenConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PrivacyEnabled = options.Privacy.Enabled;
        PrivacyMode = options.Privacy.Mode.ToString();
        PrivacyOnFailure = options.Privacy.OnFailure.ToString();
        RulesEnabled = options.Privacy.Rules.Enabled;
        GlinerPiiEnabled = options.Privacy.GlinerPii.Enabled;
        DebertaInjectionEnabled = options.Privacy.DebertaInjection.Enabled;
        Qwen3GuardEnabled = options.Privacy.Qwen3Guard.Enabled;
        PaddleOcrEnabled = options.Privacy.PaddleOcr.Enabled;
        YuNetEnabled = options.Privacy.YuNet.Enabled;
        MaxDelegationDepth = options.Agents.MaxDelegationDepth;
    }

    public bool PrivacyEnabled { get; }
    public string PrivacyMode { get; } = string.Empty;
    public string PrivacyOnFailure { get; } = string.Empty;
    public bool RulesEnabled { get; }
    public bool GlinerPiiEnabled { get; }
    public bool DebertaInjectionEnabled { get; }
    public bool Qwen3GuardEnabled { get; }
    public bool PaddleOcrEnabled { get; }
    public bool YuNetEnabled { get; }
    public int MaxDelegationDepth { get; }
}

internal static class OptionsConflictGuard
{
    public static OpenConnectRegistrationSnapshot Capture(OpenConnectOptions options)
        => new(options);

    /// <summary>
    /// Returns the first conflicting dimension, or <c>null</c> when the
    /// incoming snapshot matches the already-registered one.
    /// </summary>
    public static (string Field, string OldValue, string NewValue)? FindConflict(
        OpenConnectRegistrationSnapshot registered,
        OpenConnectRegistrationSnapshot incoming)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(incoming);

        if (!string.Equals(registered.PrivacyMode, incoming.PrivacyMode, StringComparison.Ordinal))
            return ("Privacy:Mode", registered.PrivacyMode, incoming.PrivacyMode);
        if (!string.Equals(registered.PrivacyOnFailure, incoming.PrivacyOnFailure, StringComparison.Ordinal))
            return ("Privacy:OnFailure", registered.PrivacyOnFailure, incoming.PrivacyOnFailure);
        if (registered.PrivacyEnabled != incoming.PrivacyEnabled)
            return ("Privacy:Enabled", registered.PrivacyEnabled.ToString(), incoming.PrivacyEnabled.ToString());
        if (registered.RulesEnabled != incoming.RulesEnabled)
            return ("Privacy:Rules:Enabled", registered.RulesEnabled.ToString(), incoming.RulesEnabled.ToString());
        if (registered.GlinerPiiEnabled != incoming.GlinerPiiEnabled)
            return ("Privacy:GlinerPii:Enabled", registered.GlinerPiiEnabled.ToString(), incoming.GlinerPiiEnabled.ToString());
        if (registered.DebertaInjectionEnabled != incoming.DebertaInjectionEnabled)
            return ("Privacy:DebertaInjection:Enabled", registered.DebertaInjectionEnabled.ToString(), incoming.DebertaInjectionEnabled.ToString());
        if (registered.Qwen3GuardEnabled != incoming.Qwen3GuardEnabled)
            return ("Privacy:Qwen3Guard:Enabled", registered.Qwen3GuardEnabled.ToString(), incoming.Qwen3GuardEnabled.ToString());
        if (registered.PaddleOcrEnabled != incoming.PaddleOcrEnabled)
            return ("Privacy:PaddleOcr:Enabled", registered.PaddleOcrEnabled.ToString(), incoming.PaddleOcrEnabled.ToString());
        if (registered.YuNetEnabled != incoming.YuNetEnabled)
            return ("Privacy:YuNet:Enabled", registered.YuNetEnabled.ToString(), incoming.YuNetEnabled.ToString());
        if (registered.MaxDelegationDepth != incoming.MaxDelegationDepth)
            return ("Agents:MaxDelegationDepth", registered.MaxDelegationDepth.ToString(), incoming.MaxDelegationDepth.ToString());
        return null;
    }

    public static InvalidOperationException ConflictException(string field, string oldValue, string newValue)
        => new($"Conflicting OpenConnect registration: '{field}' was already registered as '{oldValue}' but a repeat AddOpenConnect call specified '{newValue}'. Repeat registrations must use identical policy; use a single AddOpenConnect call to change policy.");
}
