# OpenConnect.Hosting.AspNetCore

ASP.NET Core hosting for OpenConnect: endpoint route builders that expose sessions and
execution over HTTP (`OpenConnect.Hosting.AspNetCore`,
`OpenConnect.Hosting.AspNetCore.Execution`).

## Install

```bash
dotnet add package OpenConnect.Hosting.AspNetCore
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Map the endpoints from the `OpenConnect.Hosting.AspNetCore` namespace onto your ASP.NET
Core app after registering services via `OpenConnect.Core`. See the repository docs
(`docs/`) for the endpoint surface and security notes.

## License

Same terms as the OpenConnect repository.
