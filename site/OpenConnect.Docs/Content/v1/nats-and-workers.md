---
title: NATS and worker integration
version: v1
summary: Route optional worker capabilities without placing sensitive content in JetStream.
order: 90
---

# NATS and worker integration

NATS JetStream is optional transport. Raw sensitive prompts, vault mappings, audio, screenshots and secrets must not be placed in broker payloads.

Workers are independently deployed and advertise only verified capabilities. Security workers detect and classify; the .NET host retains authorization, privacy policy and vault control.

Worker evidence is tiered: static and contract checks do not establish model-live or release readiness.

## Deployment sequence

1. Choose the worker and its required capability in [supported services](/docs/v1/supported-services). Review its source README, configuration, model license and resource requirements.
2. Provision private transport, scoped credentials and any required database, object store or model files. Apply application-owned migrations explicitly where required. Do not assume a container build downloads a model.
3. Deploy the worker separately and check its liveness, readiness and advertised capability. A live process can still be unable to load its model or dependencies.
4. Configure the trusted host to connect over the worker's supported protected transport. Authorize the caller and apply the appropriate privacy gate before dispatch.
5. Send a representative authorized request through the host, verify the result and failure behavior, and monitor the worker and outbox independently.

Do not publish sensitive content into JetStream envelopes. A transport acknowledgement is not a worker result. Reconcile retries with the operation's idempotency semantics; a timeout does not establish that the worker performed no work.

## Transport integration

`OpenConnect.Transport.Nats` provides JetStream publisher and outbox integration. It validates worker envelopes and uses JetStream message-ID deduplication. Your application is responsible for provisioning and securing NATS and choosing stream retention and consumers; registration alone does not create a broker. Broker deduplication is not an end-to-end exactly-once guarantee for external side effects.

## Source samples

The source repository's `samples/workers` contains individual worker samples and a shared harness. For example, from the repository root on a compatible shell:

```bash
./samples/workers/shared/scripts/run-one.sh security --tier contract --provider sqlite
```

This is a contract-tier sample, not proof of a model-live deployment. Review the sample README for Docker prerequisites, profiles, model manifests, cleanup behavior and the intended evidence tier before running it. Do not remove persistent volumes containing data as part of ordinary troubleshooting.

## Related links

- [EF Core persistence and outbox](/docs/v1/persistence)
- [Supported services and integrations](/docs/v1/supported-services)
