---
title: Configuration reference
version: v1
summary: Configure OpenConnect with secure defaults and explicit optional infrastructure.
order: 60
---

# Configuration reference

`AddOpenConnect()` enables the host privacy firewall by default. Optional persistence, NATS, workers, Navigator and voice integrations require explicit registration and independent readiness evidence.

## Secure defaults

```csharp
builder.Services.AddOpenConnect(options =>
{
    options.Privacy.Enabled = true;
});
```

Registration does not launch Docker, download models, create databases, provision streams or publish packages.

## Capability status

Advertise only capabilities that are installed and ready. Disabled model-backed detectors must not load models or advertise readiness. A required privacy detector that is unavailable blocks the affected protected egress.
