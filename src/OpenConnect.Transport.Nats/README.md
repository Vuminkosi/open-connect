# OpenConnect.Transport.Nats

NATS JetStream transport for OpenConnect: agent-event publishing, worker envelopes, and
outbox relay for reliable delivery (`OpenConnect.Transport.Nats`,
`OpenConnect.Transport.Nats.AgentEvents`).

## Install

```bash
dotnet add package OpenConnect.Transport.Nats
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Publish and relay events with the types in the `OpenConnect.Transport.Nats` namespaces,
composed through `OpenConnect.Core` service registration against your NATS server. See
the repository docs (`docs/`) for transport topology.

## License

Same terms as the OpenConnect repository.
