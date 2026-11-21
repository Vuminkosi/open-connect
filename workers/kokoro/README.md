# Kokoro speech worker

The Kokoro worker provides authenticated speech synthesis using a local CPU model. Provision the model, configuration and voice files in the configured model directory before starting the worker. Readiness verifies the files against their expected SHA-256 digests.

Missing files are not downloaded by default. An operator can explicitly set `OC_KOKORO_ALLOW_DOWNLOAD=true` to permit provisioning missing artifacts. A file with an incorrect digest is rejected and preserved for investigation; it is never replaced automatically.
