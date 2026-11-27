# Requirements: secret token restoration (issue #173)

Tier T2 (privacy, public API).

- AC1: SECRET, CREDENTIAL, API_KEY, PRIVATE_KEY, ACCESS_TOKEN and PASSWORD tokens stay placeholders in `StreamingPlaceholderBuffer` for every chunk split.
- AC2: `TokenRestorationService` leaves those tokens unresolved for `FinalUserOutput` and `ToolArguments` with a decorated or custom `ITokenVault`.
- AC3: A vault that cannot report the entity category is denied.
- AC4: The streaming property test keeps secrets unrestored while EMAIL restores; a wrapper-vault test covers the non-streaming path.
- AC5: Streaming and non-streaming output are identical for the same text and scope.
- AC6: `PublicAPI.Unshipped.txt` and the Privacy README describe the rule.
