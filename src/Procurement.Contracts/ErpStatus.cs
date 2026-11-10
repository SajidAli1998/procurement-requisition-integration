namespace Procurement.Contracts;

// Declared in lifecycle order: CanTransitionTo relies on the numeric ordering.
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

    // Equal or earlier statuses come from out-of-order or duplicate polls and are ignored.
    // Rejected can still arrive from any state before the PO exists.
    public static bool CanTransitionTo(this ErpStatus current, ErpStatus next)
    {
        if (current == next || current.IsTerminal()) return false;
        if (next == ErpStatus.Rejected) return current < ErpStatus.PoCreated;
        return next > current;
    }
}
