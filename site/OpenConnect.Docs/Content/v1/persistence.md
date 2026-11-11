---
title: EF Core persistence and outbox
version: v1
summary: Use provider-neutral OpenConnect persistence with sample-owned DbContexts and migrations.
order: 70
---

# EF Core persistence and outbox

`OpenConnect.Persistence.EFCore` provides provider-neutral mappings and persistence integration. The application owns its `DbContext`, provider choice and migrations.

```csharp
builder.Services.AddOpenConnectEntityFramework<ApplicationDbContext>(options =>
{
    options.Migrations = OpenConnectMigrationMode.Manual;
});
```

A durable task acceptance requires the task, event and outbox intent to commit atomically. Broker publication is asynchronous and external side effects must not be described as exactly once without provider evidence.
