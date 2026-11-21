# Cloud voice synthesis boundary

The voice synthesis control plane groups its registration and routes in the voice feature. Gateway requests use the gateway certificate and machine credential; consumption and settlement use a separately pinned worker certificate. Character reservations, inspected input, execution claims, worker consumption and settlement are bound to the same request and deployment.

Audio must pass a configured output privacy screen before the gateway releases it. The gateway fails closed when that screen is unavailable. An engine mode or a synthetic transcript alone is not evidence that generated audio was inspected. The feature is not available for end-to-end use until an appropriate output screen and the remaining gateway and deployment wiring are configured and verified.
