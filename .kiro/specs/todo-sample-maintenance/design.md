# Todo sample maintenance design

The Todo application remains at `samples/Blazor` and is not moved, rewritten or replaced. The portal references it through a tutorial and links to its source.

Current verified-by-source boundaries:

- `Program.cs` targets the repository default executable TFM, currently .NET 10, and registers OpenConnect client/components/composer, core, EF Core SQLite and `InMemoryTodoStore`.
- The UI calls a separate gateway through `GATEWAY_URL`; the sample does not map `MapOpenConnect` itself.
- Todo records are process-local. OpenConnect persistence uses the sample-owned SQLite context and migrations.
- Navigator/voice UI registration and live worker evidence are optional and currently not claimed by the sample.

Maintenance changes require a dedicated task with source path, failure reproduction, acceptance tests, migration impact and rollback behavior. The portal migration has no Todo source ownership.
