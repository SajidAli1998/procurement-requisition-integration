namespace Procurement.Contracts;

/// <summary>
/// Canonical procurement status owned by the ERP. Declared in lifecycle order; status updates
/// only ever move forward (see <see cref="ErpStatusExtensions.CanTransitionTo"/>).
/// </summary>
public enum ErpStatus
{
    Created = 0,
    InRelease = 1,
    Released = 2,
    PoCreated = 3,
    Closed = 4,
    Rejected = 10,
}

public static class ErpStatusExtensions
{
    public static bool IsTerminal(this ErpStatus status) => status is ErpStatus.Closed or ErpStatus.Rejected;

    /// <summary>
    /// Forward-only rule: equal or earlier statuses are stale (out-of-order or duplicate polls) and are ignored.
    /// Rejected is reachable from any non-terminal state before PO creation.
    /// </summary>
    public static bool CanTransitionTo(this ErpStatus current, ErpStatus next)
    {
        if (current == next || current.IsTerminal()) return false;
        if (next == ErpStatus.Rejected) return current < ErpStatus.PoCreated;
        return next > current;
    }
}
