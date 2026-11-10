# OpenConnect.Navigator.OperationalBrowser

Isolated browser execution for operational jobs: per-job browser sessions behind a
Playwright tool allowlist and an egress guard (`OpenConnect.Navigator.OperationalBrowser`,
`OpenConnect.Navigator.Playwright`).

## Install

```bash
dotnet add package OpenConnect.Navigator.OperationalBrowser
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Run operational browser jobs through the `OpenConnect.Navigator.OperationalBrowser`
namespace, paired with `OpenConnect.Navigator.Operational` for durable scheduling. See
the repository docs (`docs/`) for isolation and egress rules.

## License

Same terms as the OpenConnect repository.
