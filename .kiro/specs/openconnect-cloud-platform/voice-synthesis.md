# Cloud voice synthesis boundary

The voice synthesis control plane groups its registration and routes in the voice feature. Gateway requests use the gateway certificate and machine credential; consumption and settlement use a separately pinned worker certificate. Character reservations, inspected input, execution claims, worker consumption and settlement are bound to the same request and deployment.

Audio must pass a configured output privacy screen before the gateway releases it. The gateway fails closed when that screen is unavailable. An engine mode or a synthetic transcript alone is not evidence that generated audio was inspected. The feature is not available for end-to-end use until an appropriate output screen and the remaining gateway and deployment wiring are configured and verified.

Cancellation or loss of access ends a pending output-screen request and clears the gateway's audio buffer even if the screen does not respond.

The transcription worker supports a local Whisper adapter alongside its synthetic test engine. Real-mode local execution requires an explicitly selected model and revision in the verified cache, including hashed model, configuration and tokenizer files, and a preprocessor file when supplied. Model loading uses local files only; the adapter never provisions or downloads models. A manifest alone does not make the worker ready. Model availability and real transcription accuracy depend on a provisioned compatible model and must be verified independently.

The PCM WAV parser accepts extended format chunks and padded metadata chunks while rejecting truncated data and inconsistent RIFF lengths.

The worker exposes a separate authenticated audio-inspection transcription endpoint that only runs a verified local real model with the cloud leg disabled. It returns a digest of the received audio and a transcript for downstream screening; a transcript is not proof that all spoken content was recognized or that an output policy allowed release. The private worker transport must be protected before the gateway can use it.

Outbound transcription requests do not follow redirects, so provider credentials and audio cannot be redirected to another endpoint.
