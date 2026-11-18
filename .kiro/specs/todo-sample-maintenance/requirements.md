# Todo sample maintenance requirements

**Status:** Baseline preservation contract.

- **TODO-MAINT-REQ-001:** Preserve `samples/Blazor` as the independent Todo reference application.
- **TODO-MAINT-REQ-002:** Preserve existing Todo source, UI behavior, OpenConnect client/components, EF adapter, migrations, Dockerfile and worker/gateway integration unless a separately approved evidence-backed maintenance task changes them.
- **TODO-MAINT-REQ-003:** The sample must document that `InMemoryTodoStore` is process-local and that SQLite currently stores OpenConnect persistence, not Todo domain records.
- **TODO-MAINT-REQ-004:** Any future Todo persistence change must use sample-owned EF migrations and provider-neutral OpenConnect persistence; no legacy provider package may be introduced.
- **TODO-MAINT-REQ-005:** Todo tests must cover CRUD, tenant/privacy boundaries where applicable, composer failure states, optional worker absence and startup diagnostics.
- **TODO-MAINT-REQ-006:** The developer portal tutorial must run the existing Todo sample and distinguish source integration, local Docker E2E and published-package validation.
- **TODO-MAINT-REQ-007:** Native MAF remains the execution owner. Todo maintenance must not add a custom planner, agent loop, workflow engine or session manager.
