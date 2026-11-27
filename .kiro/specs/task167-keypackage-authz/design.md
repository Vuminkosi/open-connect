# Design

`HandleConsumeKeyPackageAsync`: validate identity and query, reject device-only, charge the caller quota, authorize each non-self target (`Initiate`), charge the target quota, then call the store. `KeyPackageConsumeLimiter` is an internal bounded fixed-window counter registered by `AddOpenConnectMessagingHosting`. New options are in `MessagingHostingOptions` and `PublicAPI.Unshipped.txt`. See ADR-IMSG-004.
