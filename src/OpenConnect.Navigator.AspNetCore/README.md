# OpenConnect.Navigator.AspNetCore

ASP.NET Core surface for Navigator: HTTP endpoints with session stores and outbox support
(`OpenConnect.Navigator.AspNetCore`, `OpenConnect.Navigator.AspNetCore.Outbox`).

## Install

```bash
dotnet add package OpenConnect.Navigator.AspNetCore
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Map the navigator endpoints from the `OpenConnect.Navigator.AspNetCore` namespace onto
your app after registering the core `OpenConnect.Navigator` services. See the repository
docs (`docs/`) for endpoint and persistence guidance.

## License

Same terms as the OpenConnect repository.
