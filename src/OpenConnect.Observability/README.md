# OpenConnect.Observability

OpenTelemetry instrumentation with privacy-safe telemetry: redaction-aware activities,
listeners, and exporters (`OpenConnect.Observability`) that keep sensitive data out of
traces and metrics.

## Install

```bash
dotnet add package OpenConnect.Observability
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Enable instrumentation through the `OpenConnect.Observability` namespace alongside
`OpenConnect.Core` service registration. See the repository docs (`docs/`) for telemetry
and privacy guidance.

## License

Same terms as the OpenConnect repository.
