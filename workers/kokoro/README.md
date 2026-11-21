# Kokoro speech worker

The Kokoro worker provides authenticated speech synthesis using a local CPU model. Provision the model, configuration and voice files in the configured model directory before starting the worker. Readiness verifies the files against their expected SHA-256 digests.

Missing files are not downloaded by default. An operator can explicitly set `OC_KOKORO_ALLOW_DOWNLOAD=true` to permit provisioning missing artifacts. A file with an incorrect digest is rejected and preserved for investigation; it is never replaced automatically.

Cloud claim consumption is opt-in with `OPENCONNECT_CLOUD_CONSUMPTION_ENABLED=true`, `OPENCONNECT_CLOUD_AUTHORITY_ORIGIN`, `OPENCONNECT_CLOUD_AUTHORITY_CA_FILE`, `OPENCONNECT_CLOUD_WORKER_CERT_FILE`, and `OPENCONNECT_CLOUD_WORKER_KEY_FILE`. Certificate paths must be absolute and supplied by the deployment. In this mode the legacy execution route fails closed; the Cloud route requires worker authentication and a successful single-use, payload-bound control-plane claim before synthesis. The Cloud control-plane consumption endpoint and gateway dispatch are not yet integrated, so enabling this mode alone does not make Cloud synthesis available. Ambiguous claim results are not retried. Python cannot guarantee clearing all immutable copies of request content.
