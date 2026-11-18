# OpenConnect.Metering

Usage metering and budgets for OpenConnect: model-usage records and budget guards
(`OpenConnect.Metering`) that cap spend per scope before work is performed.

## Install

```bash
dotnet add package OpenConnect.Metering
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Metering;

IBudgetGuard guard = new InMemoryBudgetGuard(new Dictionary<string, decimal>
{
    ["support"] = 25.00m,
});

BudgetReservation? reservation = guard.TryReserve("support", estimatedAmount: 1.50m);
if (reservation is not null)
{
    guard.Settle(reservation, actualCost: 1.25m);
}
```

`InMemoryBudgetGuard` (`Budget.cs:32`) enforces process-local hard limits;
`TryReserve` (`Budget.cs:12`) returns `null` instead of overspending (fail closed).
Durable multi-node enforcement requires a persistence adapter.

## License

Same terms as the OpenConnect repository.
