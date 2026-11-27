# Design

- New public interface `ISecretAwareTokenVault : ITokenVault` with `bool IsSecretToken(string token, PrivacyScope scope)`. `ITokenVault` is unchanged, so existing implementations still compile. `TokenVault` implements the new interface.
- New internal `SecretRestorationGuard.IsRestorable(vault, token, scope)`: true only if the vault is `ISecretAwareTokenVault` and reports not secret. Non-capable vaults and exceptions (other than cancellation) deny.
- `TokenRestorationService.RestoreTokensInTextAsync` and `StreamingPlaceholderBuffer.ResolveTokenAsync` (vault path) call the guard before `ResolveAsync`. Both ToolArguments and FinalUserOutput share the text path.
- Resolver-only `StreamingPlaceholderBuffer` constructor has no vault and cannot classify tokens. The delegate owns the decision; this is documented. Prefix recovery code is untouched (PR #146).
- Compatibility: custom vaults that did not implement the capability stop restoring tokens. Documented in README and CHANGELOG.
