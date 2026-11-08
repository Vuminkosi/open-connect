# Navigator v1 JSON contracts

These schemas use JSON Schema Draft 2020-12. The target, tour, and session property names mirror the Navigator C# records; enum values use the kebab-case wire representation. Tours are bounded to 25 steps; observations to 500 nodes and 65,536 serialized bytes (the byte ceiling is declared as `x-maxBytes` for consumer-side enforcement). Worker speech chunk text is limited to 1,500 characters.

`command.json` defines a session/principal/tenant-scoped command grant with an expiry and normalized-arguments SHA-256 binding. `receipt.json` records the corresponding outcome. Consumers must validate size limits and authorization server-side; schema validation alone does not grant authority.
