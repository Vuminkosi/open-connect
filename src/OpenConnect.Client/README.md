# OpenConnect.Client

Typed .NET client for calling an OpenConnect gateway over HTTP: sessions, runs, and
related client operations from the `OpenConnect.Client` namespace.

## Install

```bash
dotnet add package OpenConnect.Client
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Use the `OpenConnect.Client` namespace to construct and drive the gateway client from any
.NET host, with registration helpers from the same package. See the repository docs
(`docs/`) and the gateway endpoints in `OpenConnect.Hosting.AspNetCore` for the
server side.

## License

Same terms as the OpenConnect repository.
