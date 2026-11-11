# Task 005B design and boundaries

## Existing execution path

The Todo UI is `samples/Blazor`. It owns `InMemoryTodoStore`, registers OpenConnect client/components/composer and points `GATEWAY_URL` at an external gateway. It does not expose OpenConnect HTTP endpoints.

The available separate gateway is `samples/workers/shared/OpenConnect.Samples.Workers.Host`. It installs synthetic header-based authentication, OpenConnect core privacy, task execution, MAF host execution and `MapOpenConnect`. It is designed for worker samples and does not register the Todo store or Todo-specific tools.

Therefore a genuine Todo tool invocation cannot be tested through the existing gateway without adding a new application boundary or changing the existing Todo architecture. That is blocked by the task's non-duplication and preservation constraints.

## Safe tests

- Keep the corrected existing Blazor HTTP smoke as the no-AI UI/startup gate.
- Keep process-local Todo CRUD tests in the portal test project.
- Use existing unit/hosting tests for authenticated task, tenant isolation, event replay, privacy and MAF behavior where they already cover the framework.
- Do not relabel those framework tests as Todo gateway E2E.
- Add browser tests only after a real Playwright dependency and a stable portal/Todo launch fixture are approved.
