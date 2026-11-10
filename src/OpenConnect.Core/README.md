# OpenConnect.Core

Composition root for OpenConnect: dependency-injection registration, validated options,
capability routing, and runtime services (`OpenConnect.Core`,
`OpenConnect.Core.Configuration`, `OpenConnect.Core.Capabilities`). Privacy protections
are enabled by default.

## Install

```bash
dotnet add package OpenConnect.Core
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Register OpenConnect through the service-registration helpers in `OpenConnect.Core` and
tune behavior via `OpenConnect.Core.Configuration`. See the repository docs (`docs/`)
for configuration and architecture guidance.

## License

Same terms as the OpenConnect repository.
