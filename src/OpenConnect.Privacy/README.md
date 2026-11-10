# OpenConnect.Privacy

Privacy firewall for OpenConnect: policy engine, tokenization, and egress gates
(`OpenConnect.Privacy`, `OpenConnect.Privacy.Policy`, `OpenConnect.Privacy.Tokenization`,
`OpenConnect.Privacy.Gates`) that deny by default and fail closed.

## Install

```bash
dotnet add package OpenConnect.Privacy
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Enforce privacy boundaries with the firewall and gates in the `OpenConnect.Privacy`
namespaces, composed through `OpenConnect.Core` service registration. See the repository
docs (`docs/`) for detector modes and policy guidance.

## License

Same terms as the OpenConnect repository.
