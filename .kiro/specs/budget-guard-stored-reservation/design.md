# Budget guard stored reservation design

`_open` maps reservation Id to the `BudgetReservation` created by `TryReserve`. Under `_gate`, `Settle` and `Release` call `_open.Remove(reservation.Id, out var stored)`. If the Id is absent they return. Otherwise all bookkeeping (`_reserved`, `_spent`, `_blocked`, default charge when cost is unknown) uses `stored.ScopeKey` and `stored.Amount`.

The `BudgetReservation` record and `IBudgetGuard` signatures are unchanged, so PublicAPI files need no update. Doc comments on `IBudgetGuard.Settle` and `Release` state that stored values are authoritative.

Security: the Id is a random GUID and the only caller-trusted field; removal under the lock keeps settlement single-use.
