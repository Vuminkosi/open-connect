# Documentation assistant application host

This opt-in development host is an OpenConnect SDK consumer for the public documentation composer. It provides authenticated sessions, tasks, connection-bound LiveUI tools and the SDK speech endpoint. Documentation visitors do not sign in: the site signs a distinct short-lived visitor identity for each interactive circuit, and the host validates that identity and the configured tenant. Model messages and responses pass through the OpenConnect host privacy gate before crossing the model boundary. Speech text and metadata are screened before dispatch to the worker; the speech contract has no session ID, so this check uses a fresh request-scoped privacy session.

This host is **development only**. It accepts HTTPS connections from loopback, limits request rate and task retention, and keeps session state and its encryption keys in one process. Restarting it loses conversations. It cannot share state across replicas, does not implement Cloud quota settlement, and must not be used as a production or public application gateway. LiveUI worker planner and microphone transcription integration are not provided by this host yet.

To use the local Compose setup, combine the root Compose file, `docker-compose-cloud-local.yml` and `docker-compose-docs-local-host.yml`. Initialize the local TLS volume first. Supply these settings through a protected environment or ignored `.env` file:

- `OC_DOCS_ASSISTANT_TENANT`: a non-default canonical ULID reserved for the documentation application.
- `OC_DOCS_ASSISTANT_VISITOR_KEY`: a base64-encoded, randomly generated 32-byte signing key. The site and host must use the same key.
- `OC_DOCS_ASSISTANT_MODEL_ENDPOINT`, `OC_DOCS_ASSISTANT_MODEL_NAME` and `OC_DOCS_ASSISTANT_MODEL_API_KEY`: the explicitly configured HTTPS model provider and its credentials. The provider receives privacy-screened model requests.
- `OPENCONNECT_WORKER_TOKEN`: the shared private worker token, supplied by the deployment secret store.
- `OC_DOCS_ASSISTANT_CA_BUNDLE`: a protected CA bundle that lets the site validate the host's localhost certificate and any other TLS authorities the site uses, including its identity provider.

The host shares the site's network namespace so its HTTPS listener is reachable only over loopback. The Kokoro worker must be running and ready for speech output. The model and worker credentials must not be committed. The gateway's state and rate limits are local to one process; they do not establish billing or cross-instance guarantees.
