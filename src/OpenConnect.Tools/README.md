# OpenConnect.Tools

Tool plane for OpenConnect: catalog, broker, and per-call authorization
(`OpenConnect.Tools`) governing which tools agents may invoke.

## Install

```bash
dotnet add package OpenConnect.Tools
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Publish and authorize tools with the types in the `OpenConnect.Tools` namespace,
composed through `OpenConnect.Core` service registration. See the repository docs
(`docs/`) for the authorization model.

## License

Same terms as the OpenConnect repository.
