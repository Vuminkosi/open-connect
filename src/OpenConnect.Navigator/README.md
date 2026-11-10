# OpenConnect.Navigator

Navigator core: authorized navigation commands, sessions, tour plans, and diagnostics
(`OpenConnect.Navigator`, `OpenConnect.Navigator.Capture`,
`OpenConnect.Navigator.Diagnostics`).

## Install

```bash
dotnet add package OpenConnect.Navigator
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Build navigator sessions and commands from the `OpenConnect.Navigator` namespaces and
expose them over HTTP with `OpenConnect.Navigator.AspNetCore`. See the repository docs
(`docs/`) for the navigator model.

## License

Same terms as the OpenConnect repository.
