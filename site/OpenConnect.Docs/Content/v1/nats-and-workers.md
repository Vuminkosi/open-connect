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
