# OpenConnect knowledge client

`KnowledgeEmbeddingClient` sends private embedding requests to a configured
worker after evaluating every text through the host `IEgressGate`. It sends
the sanitized values, signs the exact serialized request, and validates the
response's task ID, model-space metadata, vector dimensions, batch count and
actual vector norm. A 100-second deadline bounds response headers and body reads,
in addition to caller cancellation.

The host must authenticate and authorize the principal for the tenant before
constructing the egress context. Configure the destination according to the
actual deployment boundary: it defaults to `ExternalModel`; choose `Worker`
only for a trusted local worker under the host's privacy policy. Plain HTTP
is disabled unless explicitly opted in for an appropriately protected private
transport. Keep the bearer token and signing key in the host secret store.
The client owns its HTTP connection pool, does not follow redirects or use a
system proxy, and should be disposed when the application no longer needs it.

This client does not implement entitlement checks, quotas, network isolation,
retry/idempotency, or indexing. A successful embedding is application data
and must be protected and retained according to tenant policy.
