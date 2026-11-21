# Documentation assistant application host

This opt-in development host is an OpenConnect SDK consumer for the public documentation composer. It provides authenticated sessions, tasks, connection-bound LiveUI tools and the SDK speech endpoint. Documentation visitors do not sign in: the site signs a distinct short-lived visitor identity for each interactive circuit, and the host validates that identity and the configured tenant. Model messages and responses pass through the OpenConnect host privacy gate before crossing the model boundary. Speech text and metadata are screened before dispatch to the worker; the speech contract has no session ID, so this check uses a fresh request-scoped privacy session.

This host is **development only**. It accepts HTTPS connections from loopback, limits request rate and task retention, and keeps session state and its encryption keys in one process. Restarting it loses conversations. It cannot share state across replicas, does not implement Cloud quota settlement, and must not be used as a production or public application gateway. Microphone transcription requires a separately provisioned local transcription worker and verified model; without one, transcription is unavailable.

To use the local Compose setup, combine the root Compose file, `docker-compose-cloud-local.yml` and `docker-compose-docs-local-host.yml`. Initialize the local TLS volume first. Supply these settings through a protected environment or ignored `.env` file:

- `OC_DOCS_ASSISTANT_TENANT`: a non-default canonical ULID reserved for the documentation application.
- `OC_DOCS_ASSISTANT_VISITOR_KEY`: a base64-encoded, randomly generated 32-byte signing key. The site and host must use the same key.
- `OC_DOCS_ASSISTANT_MODEL_ENDPOINT`, `OC_DOCS_ASSISTANT_MODEL_NAME` and `OC_DOCS_ASSISTANT_MODEL_API_KEY`: the explicitly configured HTTPS model provider and its credentials. The provider receives privacy-screened model requests.
- `OPENCONNECT_WORKER_TOKEN`: the shared private worker token, supplied by the deployment secret store.
- `OC_DOCS_ASSISTANT_CA_BUNDLE`: a protected CA bundle that lets the site validate the host's localhost certificate and any other TLS authorities the site uses, including its identity provider.
- Optional LiveUI worker planning: set `OC_DOCS_ASSISTANT_LIVEUI_WORKER_ENABLED=true`, `OC_DOCS_ASSISTANT_LIVEUI_WORKER_ENDPOINT` to the private worker HTTP origin, and `OC_DOCS_ASSISTANT_LIVEUI_WORKER_TOKEN` to its credential. The worker must already be provisioned and ready.

The host shares the site's network namespace so its HTTPS listener is reachable only over loopback. The Kokoro worker must be running and ready for speech output. LiveUI worker planning checks session ownership, screens request and response content, and validates proposed actions before returning them; the site still authorizes each interaction. The model and worker credentials must not be committed. The gateway's state and rate limits are local to one process; they do not establish billing or cross-instance guarantees.

## Use the assistant with local Cloud identity

When the local OIDC provider and assistant are enabled together, the documentation site must trust both HTTPS authorities. Create a PEM bundle containing the CA certificates for both the local identity provider and the assistant host, and set `OC_DOCS_CLOUD_CA_BUNDLE` to its path in the protected environment. Keep private keys out of the bundle. After initializing the existing TLS volumes and setting the identity and assistant variables, compose the combined trust overlay last:

```powershell
docker compose -f docker-compose-all.yml -f docker-compose-cloud-local.yml -f docker-compose-cloud-local-identity.yml -f docker-compose-docs-local-host.yml -f docker-compose-docs-cloud-local.yml up -d documentation-site local-identity local-identity-loopback documentation-assistant-host
```

The final overlay supplies one trust bundle for both integrations. It does not enable Cloud capability execution or alter tenant billing. The local model provider and provisioned workers must still be configured independently.
