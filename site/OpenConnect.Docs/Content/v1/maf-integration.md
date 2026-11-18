---
title: Microsoft Agent Framework integration
version: v1
summary: Compose native Direct, Harness and Workflow capabilities without a second runtime.
order: 30
---

# Microsoft Agent Framework integration

OpenConnect uses the pinned Microsoft Agent Framework packages for agent intelligence, Harness planning and native workflows. OpenConnect owns application identity, authorization, privacy, task state, outbox and client projections.

## Ownership

Do not implement a competing planner, todo engine, LLM loop or workflow replayer. If a native MAF capability is unavailable for a target framework, expose it as unavailable rather than simulating it.
