# Budget guard stored reservation requirements

Issue: Vuminkosi/open-connect#99. Tier: T1. Scope: `src/OpenConnect.Metering/Budget.cs`.

## Problem

`InMemoryBudgetGuard.Settle` and `Release` removed the open reservation by Id but then used the caller-supplied `ScopeKey` and `Amount`. A caller holding a modified `BudgetReservation` could corrupt reserved and spent totals, throw `KeyNotFoundException` for an unknown scope, or block the wrong scope under `MissingCostPolicy.Deny`.

## Acceptance criteria

- **AC1** `Settle` and `Release` use the stored reservation's scope and amount. Caller `ScopeKey` and `Amount` are ignored.
- **AC2** Settling a reservation with an inflated `Amount` charges the stored amount, keeps reserved and spent consistent, and a later reservation beyond the limit is still denied. The test fails on the previous code.
- **AC3** Settling or releasing with an unknown `ScopeKey` and valid Id does not throw and leaves no stuck reservation.
- **AC4** Repeated `Settle` or `Release`, and calls with a never-issued Id, remain no-ops.
- **AC5** With `MissingCostPolicy.Deny` and `actualCost` null, only the stored scope is blocked.
- **AC6** No public API shape change.
