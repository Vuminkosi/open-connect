# OpenConnect.Transport.Nats

NATS JetStream transport for OpenConnect: agent-event publishing, worker envelopes, and
outbox relay for reliable delivery (`OpenConnect.Transport.Nats`,
`OpenConnect.Transport.Nats.AgentEvents`).

## Install

```bash
dotnet add package OpenConnect.Transport.Nats
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Transport.Nats;

var publisher = new NatsPublisher(jetStreamPublisher);
await publisher.PublishAsync("oc.agent.events", envelopeJson);
```

`NatsPublisher.PublishAsync` (`NatsPublisher.cs:11`) validates the worker envelope and
publishes through `IJetStreamPublisher.PublishAsync` (`:5`) with JetStream message-id
deduplication; oversized envelopes throw `ArgumentOutOfRangeException`.

## License

Same terms as the OpenConnect repository.
