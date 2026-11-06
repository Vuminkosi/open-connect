namespace OpenConnect.Privacy.Tokenization;

/// <summary>Creates privacy tokens through an <see cref="ITokenVault"/>.</summary>
public sealed class TokenGenerator(ITokenVault vault)
{
    /// <summary>Stores a value in the vault and returns its token.</summary>
    /// <param name="value">Value to protect.</param>
    /// <param name="entityType">Type of entity represented by the value.</param>
    /// <param name="scope">Scope required to resolve the token.</param>
    /// <param name="ttl">How long the token remains valid.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The generated token.</returns>
    public ValueTask<string> CreateAsync(string value, string entityType, PrivacyScope scope, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        vault.StoreAsync(value, entityType, scope, ttl, cancellationToken);
}
