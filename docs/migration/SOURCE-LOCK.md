# OpenConnect Source Lock and Migration Provenance

**Date:** 2026-11-06
**Status:** Authoritative source lock for Kiroween 2026 — Skeleton Crew

## Source Repository

**Reference Project:** Swift-Aid
**Repository:** https://github.com/Vuminkosi/Swift-Aid
**Local Reference:** C:\DevOps\SwiftAidv2
**Pinned Commit SHA:** `5a6bad62f87d9e9dfabbc2d0fa1a0b491f0bada7`

## Migration Principles

1. **New Project, New Implementation:** OpenConnect is a new open-source framework developed during Kiroween 2026. Swift-Aid serves as a read-only engineering reference only.

2. **No Blind Copies:** Swift-Aid source code, clinical workflows, patient data, case-specific authorization, secrets, or configuration must not be copied into OpenConnect.

3. **License Compliance:** All third-party code, model weights, and dependencies must have verified licenses before incorporation.

4. **Independent Development:** Prefer new implementation over copy. When Swift-Aid patterns are adapted:
   - Remove all domain-specific logic (healthcare, cases, patients)
   - Abstract to generic framework contracts
   - Document the adaptation decision

## Extracted Concepts (Not Direct Copies)

| Swift-Aid Concept | OpenConnect Adaptation | Status |
|---|---|---|
| `EllaiSessionService` session versioning | Generic session fencing | To implement |
| `PostgresEllaiSessionStore` durability | Generic PostgreSQL persistence | To implement |
| `EllaiSpecialistRegistry` | Generic agent registry | To implement |
| `EllaiToolBroker` | Generic tool broker | To implement |
| `EllaiUiRegistry` | Generic component registry | To implement |
| `JetStreamPublisher` patterns | Generic NATS transport | To implement |
| `EllaiVoiceTurnCoordinator` | Generic voice coordination | To implement |
| Classification worker patterns | Generic worker protocol | To implement |
| MMS-TTS service patterns | Generic TTS worker | To implement |

## Prohibited Content

The following must NEVER be copied from Swift-Aid:

- Patient data, health records, or clinical information
- Case-specific authorization policies
- Healthcare domain workflows
- Production secrets, credentials, or connection strings
- Any personally identifiable information
- Clinical decision logic or medical advice systems

## Verification

Before any PR that references Swift-Aid patterns:

1. Confirm no patient/clinical data included
2. Verify generic abstraction (no healthcare domain)
3. Document source reference and adaptation decision
4. Include license attribution if applicable

---

**Maintainer:** OpenConnect Team
**Last Updated:** 2026-11-06
