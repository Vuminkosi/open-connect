# Cloud voice synthesis boundary

The voice synthesis control plane groups its registration and routes in the voice feature. Gateway requests use the gateway certificate and machine credential; consumption and settlement use a separately pinned worker certificate. Character reservations, inspected input, execution claims, worker consumption and settlement are bound to the same request and deployment.

Audio must pass a configured output privacy screen before the gateway releases it. The gateway fails closed when that screen is unavailable. An engine mode or a synthetic transcript alone is not evidence that generated audio was inspected. The feature is not available for end-to-end use until an appropriate output screen and the remaining gateway and deployment wiring are configured and verified.

Cancellation or loss of access ends a pending output-screen request and clears the gateway's audio buffer even if the screen does not respond.

The transcription worker supports a local Whisper adapter alongside its synthetic test engine. Real-mode local execution requires an explicitly selected model and revision in the verified cache, including hashed model, configuration and tokenizer files, and a preprocessor file when supplied. Model loading uses local files only; the adapter never provisions or downloads models. A manifest alone does not make the worker ready. Model availability and real transcription accuracy depend on a provisioned compatible model and must be verified independently.

The PCM WAV parser accepts extended format chunks and padded metadata chunks while rejecting truncated data and inconsistent RIFF lengths.

The worker exposes separate authenticated audio-inspection readiness and transcription endpoints that only run a verified local real model with the cloud leg disabled. Both identify the verified model manifest fingerprint; transcription also returns a digest of the received audio and a transcript for downstream screening. A transcript is not proof that all spoken content was recognized or that an output policy allowed release. The private worker transport must be protected before the gateway can use it.

The transcription contract accepts the 24 kHz mono PCM output used by the synthesis worker. Its configured audio-byte and duration limits must cover the deployment's maximum synthesis output; the default transcription limits are lower than the maximum synthesis contract.

The gateway transcription client requires mutual TLS and an expected model ID, revision and manifest fingerprint. It checks worker limits before use and verifies the returned audio digest. This client provides a transcript only; it does not authorize release or replace the output privacy policy.

The voice output privacy endpoint independently evaluates the recognized transcript at the Cloud output boundary. It records only scope, claim, the gateway-supplied model-manifest fingerprint, outcome, trace and timestamp for a completed and consumed speech claim. The record is append-only and does not contain audio or transcript text; it records a transcript inspection decision, not proof of the worker's identity, complete recognition or audio delivery.

The gateway output screen requires an allowed transcript policy decision and normalized word agreement with the inspected synthesis input. Disagreement and missing dependencies fail closed. Agreement is an additional check, not proof that all audio content was recognized; no transcript-based screen guarantees recognition completeness.

Cloud synthesis readiness requires an authenticated worker with a loaded model, configured claim consumption and a deployment identifier. The gateway verifies that identifier over its configured mutual-TLS transport before dispatch.

The local gateway exposes `POST /cloud/v1/voice/synthesis` only when voice execution is explicitly configured. The JSON result contains the requested audio format and base64 audio without echoing input text. Deployment requires separate protected transports and credentials for the synthesis worker, control plane and audio-inspection worker; the worker's claim-consumption certificate is supplied by deployment-managed mounts. The transcription worker's default limits are insufficient for the full 120-second synthesis maximum and must be raised when voice execution is enabled.

The opt-in live-system test exercises the gateway, mutual-TLS worker and control-plane connections, real provisioned synthesis and transcription models, quota settlement, output evidence, replay and privacy denial using a disposable PostgreSQL database. It does not verify the production deployment, user interfaces or recognition accuracy across languages and audio conditions.

The tenant security page presents paginated voice input and output transcript observations to owners, administrators and security analysts. Reads are tenant-scoped and audited, and the view does not expose request text, transcripts or audio. Input decisions and output transcript decisions remain distinct and neither is presented as evidence of audio delivery.

Outbound transcription requests do not follow redirects, so provider credentials and audio cannot be redirected to another endpoint.
