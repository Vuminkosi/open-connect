# OpenConnect.Observability

OpenTelemetry instrumentation with privacy-safe telemetry: redaction-aware activities,
listeners, and exporters (`OpenConnect.Observability`) that keep sensitive data out of
traces and metrics.

## Install

```bash
dotnet add package OpenConnect.Observability
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Observability;

using var listener = new SafeActivityListener(); // defaults to TelemetryPrivacy.CreateDefault()
```

`SafeActivityListener` (`SafeActivityListener.cs:10`) sanitizes every stopped
`Activity` through an `ITelemetryPrivacyFilter`; `TelemetryPrivacy.CreateDefault`
(`TelemetryPrivacy.cs:149`) builds the default filter plus export validator. Sensitive
data never reaches traces, metrics, or exporters.

## License

Same terms as the OpenConnect repository.
