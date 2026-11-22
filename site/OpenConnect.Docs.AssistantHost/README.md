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

After the TLS volume and required settings are prepared, build and start the public documentation site, its application host, and the default workers:

```powershell
docker compose -f docker-compose-all.yml -f docker-compose-cloud-local.yml -f docker-compose-docs-local-host.yml up -d --build documentation-site documentation-assistant-host worker-kokoro worker-inference liveui-inference worker-security
```

Open `https://localhost:5051/docs` (or the configured `OPENCONNECT_DOCS_HTTPS_PORT`) and open the assistant. The browser must trust the local site certificate. The documentation pages do not require sign-in; Cloud dashboard authentication is configured separately. Start an explicitly provisioned transcription worker with the `transcription` profile when local microphone input is needed. Worker health and an assistant launcher alone do not establish that a model is available or that an interaction completed.

The host shares the site's network namespace so its HTTPS listener is reachable only over loopback. The Kokoro worker must be running and ready for speech output. LiveUI worker planning checks session ownership, screens request and response content, and validates proposed actions before returning them; the site still authorizes each interaction. The model and worker credentials must not be committed. The gateway's state and rate limits are local to one process; they do not establish billing or cross-instance guarantees.

The local host overlay enables a server-side check of the local transcription model before enabling microphone controls. The check uses the visitor's host credential and the host's private worker credential; neither is sent to browser JavaScript. An unavailable or invalid response disables voice input while leaving text and separately configured speech output available. General worker readiness does not establish that the local-only model is present. Refresh the page after provisioning a verified model; the check does not reserve capacity and a later voice request can still fail.

## Optional document example

The documents and knowledge guide includes an opt-in upload example using the `OpenConnect.Documents` SDK. It requires development mode, documentation visitor identity, an isolated document-worker socket, an explicitly configured extraction worker, and a signing key. It accepts synthetic PDF, TIFF, PNG or JPEG files up to 10 MiB, limits concurrent processing, and shows OCR and an extracted reference for review. Worker scanning, model readiness and retention are deployment responsibilities. Without these prerequisites the example remains disabled; no model is downloaded by the site.

To enable it in a protected local stack, provision the document and extraction profiles as described in `samples/Blazor/README.md`, compose `docker-compose-docs-document-demo.yml` with the site overlays, and set `OC_DOCS_DOCUMENT_DEMO_ENABLED=true` and `OPENCONNECT_WORKER_SIGNING_KEY` in the protected environment. Configure `OC_DOCS_EXTRACTION_ORIGIN` for the worker. Plain HTTP requires an isolated development network and the explicit `OC_DOCS_EXTRACTION_ALLOW_HTTP=true` setting. Worker tokens and signing keys must not be committed. The assistant may activate analysis after a visitor selects a file; it cannot choose or upload a file itself.

## Optional knowledge example

The same guide can demonstrate indexing and hybrid search of an operator-approved synthetic document with the `OpenConnect.Knowledge` SDK. Each visitor gets a separate document ID and principal ACL. The index entry is given a one-hour expiry; provision and monitor the knowledge expiry worker and its storage cleanup as well as the index, search and matching embedding services. A completed indexing status is required before search. Search results are cited passages, not verified answers.

Compose `docker-compose-docs-knowledge-demo.yml` with the site overlays and set `OC_DOCS_KNOWLEDGE_DEMO_ENABLED=true`. Supply `OC_DOCS_KNOWLEDGE_OBJECT_ID`, `OBJECT_SHA256`, `OBJECT_SIZE`, `OBJECT_VERSION`, and `EMBEDDING_SPACE_ID` using the same prefix, plus the worker token and signing key, through a protected environment. Configure the private index, search and embedding origins. Plain HTTP is allowed only when explicitly enabled with `OC_DOCS_KNOWLEDGE_ALLOW_HTTP=true` for an isolated development network. To use Cloud query embeddings, configure `OC_DOCS_KNOWLEDGE_USE_CLOUD_EMBEDDING=true`, the gateway origin, and a site-owned `OC_DOCS_KNOWLEDGE_CLOUD_BEARER_TOKEN`; Cloud capability calls may incur usage charges to that credential's tenant. The site does not require visitors to provide credentials. The source object must already exist under the configured tenant's protected storage contract.

## Use the assistant with local Cloud identity

When the local OIDC provider and assistant are enabled together, the documentation site must trust both HTTPS authorities. Create a PEM bundle containing the CA certificates for both the local identity provider and the assistant host, and set `OC_DOCS_CLOUD_CA_BUNDLE` to its path in the protected environment. Keep private keys out of the bundle. After initializing the existing TLS volumes and setting the identity and assistant variables, compose the combined trust overlay last:

```powershell
docker compose -f docker-compose-all.yml -f docker-compose-cloud-local.yml -f docker-compose-cloud-local-identity.yml -f docker-compose-docs-local-host.yml -f docker-compose-docs-cloud-local.yml up -d documentation-site local-identity local-identity-loopback documentation-assistant-host
```

The final overlay supplies one trust bundle for both integrations. It does not enable Cloud capability execution or alter tenant billing. The local model provider and provisioned workers must still be configured independently.
