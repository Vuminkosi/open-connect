# OpenConnect.Abstractions

Dependency-free contracts for OpenConnect: stable IDs, DTOs, error codes, and the core
interfaces (`OpenConnect.Abstractions`) shared by every layer. This package has no external
dependencies by design, so all other OpenConnect packages can reference it safely.

## Install

```bash
dotnet add package OpenConnect.Abstractions
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Reference the `OpenConnect.Abstractions` namespace for identifiers, DTOs, and service
contracts. Concrete behavior lives in the companion packages (notably `OpenConnect.Core`
for composition); see the repository docs (`docs/`) for end-to-end setup.

## License

Same terms as the OpenConnect repository.
