namespace OpenConnect.Privacy.Restoration;

/// <summary>Shared fail-closed rule deciding whether a token may be resolved to plaintext.</summary>
internal static class SecretRestorationGuard
{
    /// <summary>
    /// Returns <see langword="true"/> only when the vault can report token categories and reports the token
    /// as not secret. Vaults without that capability, and any failure while checking, deny restoration.
    /// </summary>
    public static bool IsRestorable(ITokenVault vault, string token, PrivacyScope scope)
    {
        if (vault is not ISecretAwareTokenVault secretAware)
        {
            return false;
        }

        try
        {
            return !secretAware.IsSecretToken(token, scope);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
