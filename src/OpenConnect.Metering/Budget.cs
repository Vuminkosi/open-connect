namespace OpenConnect.Metering;

/// <summary>TreatAsReserved charges the reserved estimate; Deny charges the estimate and blocks the scope from further reservations until cost accounting is reconciled.</summary>
public enum MissingCostPolicy { Deny, TreatAsReserved }

/// <summary>Outcome of a budget reservation attempt.</summary>
public sealed record BudgetReservation(Guid Id, string ScopeKey, decimal Amount);

public interface IBudgetGuard
{
    /// <summary>Reserves an estimated amount against a scope. Returns null when it would exceed the hard limit (fail closed).</summary>
    BudgetReservation? TryReserve(string scopeKey, decimal estimatedAmount);

    /// <summary>Settles a reservation with the actual cost (null = cost unknown, handled per <see cref="MissingCostPolicy"/>). Idempotent. The guard's stored reservation (matched by <see cref="BudgetReservation.Id"/>) is authoritative: caller-supplied <see cref="BudgetReservation.ScopeKey"/> and <see cref="BudgetReservation.Amount"/> are ignored, and an unknown Id is a no-op.</summary>
    void Settle(BudgetReservation reservation, decimal? actualCost);

    /// <summary>Releases a reservation without spending (e.g. request never sent). Idempotent. The stored reservation's scope and amount are authoritative; caller-supplied values are ignored and an unknown Id is a no-op.</summary>
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
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
    private readonly MissingCostPolicy _missingCostPolicy;

    public InMemoryBudgetGuard(IReadOnlyDictionary<string, decimal> limits, MissingCostPolicy missingCostPolicy = MissingCostPolicy.TreatAsReserved)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.Values.Any(v => v < 0))
            throw new ArgumentOutOfRangeException(nameof(limits), "Limits cannot be negative.");
        _limits = new Dictionary<string, decimal>(limits, StringComparer.Ordinal);
        _missingCostPolicy = missingCostPolicy;
    }

    public decimal Spent(string scopeKey) { lock (_gate) return _spent.GetValueOrDefault(scopeKey); }

    public BudgetReservation? TryReserve(string scopeKey, decimal estimatedAmount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (estimatedAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedAmount));
        lock (_gate)
        {
            // Unknown scope has no limit configured: deny rather than allow unmetered spend.
            if (!_limits.TryGetValue(scopeKey, out var limit) || _blocked.Contains(scopeKey))
                return null;
            var committed = _spent.GetValueOrDefault(scopeKey) + _reserved.GetValueOrDefault(scopeKey);
            if (committed + estimatedAmount > limit)
                return null;
            var reservation = new BudgetReservation(Guid.NewGuid(), scopeKey, estimatedAmount);
            _open[reservation.Id] = reservation;
            _reserved[scopeKey] = _reserved.GetValueOrDefault(scopeKey) + estimatedAmount;
            return reservation;
        }
    }

    public void Settle(BudgetReservation reservation, decimal? actualCost)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (actualCost < 0)
            throw new ArgumentOutOfRangeException(nameof(actualCost));
        lock (_gate)
        {
            // The stored reservation is authoritative; caller-supplied ScopeKey and Amount are ignored.
            if (!_open.Remove(reservation.Id, out var stored))
                return;
            _reserved[stored.ScopeKey] -= stored.Amount;
            if (actualCost is null && _missingCostPolicy == MissingCostPolicy.Deny)
                _blocked.Add(stored.ScopeKey);
            var charge = actualCost ?? stored.Amount;
            _spent[stored.ScopeKey] = _spent.GetValueOrDefault(stored.ScopeKey) + charge;
        }
    }

    public void Release(BudgetReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_gate)
        {
            if (_open.Remove(reservation.Id, out var stored))
                _reserved[stored.ScopeKey] -= stored.Amount;
        }
    }
}
