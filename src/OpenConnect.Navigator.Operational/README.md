# OpenConnect.Navigator.Operational

Durable operational navigator: leased job execution with an operational handler for
long-running operations work (`OpenConnect.Navigator.Operational`,
`OpenConnect.Navigator.Operational.Durable`).

## Install

```bash
dotnet add package OpenConnect.Navigator.Operational
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Schedule and supervise durable jobs with the types in the
`OpenConnect.Navigator.Operational` namespaces, composed through service registration
alongside the core `OpenConnect.Navigator` services. See the repository docs (`docs/`)
for operational semantics.

## License

Same terms as the OpenConnect repository.
