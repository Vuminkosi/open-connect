namespace OpenConnect.Metering;

public enum MissingCostPolicy { Deny, TreatAsReserved }

/// <summary>Outcome of a budget reservation attempt.</summary>
public sealed record BudgetReservation(Guid Id, string ScopeKey, decimal Amount);

public interface IBudgetGuard
{
    /// <summary>Reserves an estimated amount against a scope. Returns null when it would exceed the hard limit (fail closed).</summary>
    BudgetReservation? TryReserve(string scopeKey, decimal estimatedAmount);

    /// <summary>Settles a reservation with the actual cost (null = cost unknown, handled per <see cref="MissingCostPolicy"/>). Idempotent.</summary>
    void Settle(BudgetReservation reservation, decimal? actualCost);

    /// <summary>Releases a reservation without spending (e.g. request never sent). Idempotent.</summary>
    void Release(BudgetReservation reservation);
}

/// <summary>Process-local hard-limit guard. Durable multi-node enforcement requires a persistence adapter.</summary>
public sealed class InMemoryBudgetGuard : IBudgetGuard
{
    private readonly object _gate = new();
    private readonly Dictionary<string, decimal> _limits;
    private readonly Dictionary<string, decimal> _spent = new();
    private readonly Dictionary<string, decimal> _reserved = new();
    private readonly Dictionary<Guid, BudgetReservation> _open = new();
    private readonly MissingCostPolicy _missingCostPolicy;

    public InMemoryBudgetGuard(IReadOnlyDictionary<string, decimal> limits, MissingCostPolicy missingCostPolicy = MissingCostPolicy.TreatAsReserved)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.Values.Any(v => v < 0)) throw new ArgumentOutOfRangeException(nameof(limits), "Limits cannot be negative.");
        _limits = new Dictionary<string, decimal>(limits, StringComparer.Ordinal);
        _missingCostPolicy = missingCostPolicy;
    }

    public decimal Spent(string scopeKey) { lock (_gate) return _spent.GetValueOrDefault(scopeKey); }

    public BudgetReservation? TryReserve(string scopeKey, decimal estimatedAmount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (estimatedAmount < 0) throw new ArgumentOutOfRangeException(nameof(estimatedAmount));
        lock (_gate)
        {
            // Unknown scope has no limit configured: deny rather than allow unmetered spend.
            if (!_limits.TryGetValue(scopeKey, out var limit)) return null;
            var committed = _spent.GetValueOrDefault(scopeKey) + _reserved.GetValueOrDefault(scopeKey);
            if (committed + estimatedAmount > limit) return null;
            var reservation = new BudgetReservation(Guid.NewGuid(), scopeKey, estimatedAmount);
            _open[reservation.Id] = reservation;
            _reserved[scopeKey] = _reserved.GetValueOrDefault(scopeKey) + estimatedAmount;
            return reservation;
        }
    }

    public void Settle(BudgetReservation reservation, decimal? actualCost)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (actualCost < 0) throw new ArgumentOutOfRangeException(nameof(actualCost));
        lock (_gate)
        {
            if (!_open.Remove(reservation.Id)) return;
            _reserved[reservation.ScopeKey] -= reservation.Amount;
            var charge = actualCost ?? (_missingCostPolicy == MissingCostPolicy.TreatAsReserved ? reservation.Amount : 0m);
            _spent[reservation.ScopeKey] = _spent.GetValueOrDefault(reservation.ScopeKey) + charge;
        }
    }

    public void Release(BudgetReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_gate)
        {
            if (_open.Remove(reservation.Id)) _reserved[reservation.ScopeKey] -= reservation.Amount;
        }
    }
}
