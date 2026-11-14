# OpenConnect.LiveUI

OpenConnect.LiveUI provides the versioned UI manifest, typed action and receipt contracts, safe configuration, manifest hashing, and action orchestration used by LiveUI adapters.

## Register

The host calls AddOpenConnectLiveUI and configures the supported modes and safe defaults. Hosts provide an ILiveUIManifestProvider, an ILiveUIActionAuthorizer, and an ILiveUIRuntime. The authorizer receives the current route, target, manifest, and typed action. It must bind authorization to the authenticated host context and return UserConfirmed only when a matching user approval exists. No target capability grants permission by itself.

LiveUIOrchestrator rejects invalid or stale manifests, unknown routes and targets, unsupported target capabilities, unauthorized actions, duplicate action IDs within a session, and actions without verified postconditions. LiveUIManifestHasher computes the content digest over stable, sorted manifest content.

The core package has no browser, UI framework, model provider, or persistence dependency.
