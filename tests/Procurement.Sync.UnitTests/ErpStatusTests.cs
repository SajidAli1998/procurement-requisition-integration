using Procurement.Contracts;

namespace Procurement.Sync.UnitTests;

public sealed class ErpStatusTests
{
    [Theory]
    [InlineData(ErpStatus.Created, ErpStatus.InRelease, true)]
    [InlineData(ErpStatus.Created, ErpStatus.PoCreated, true)]
    [InlineData(ErpStatus.Released, ErpStatus.InRelease, false)]
    [InlineData(ErpStatus.PoCreated, ErpStatus.Released, false)]
    [InlineData(ErpStatus.Released, ErpStatus.Released, false)]
    [InlineData(ErpStatus.InRelease, ErpStatus.Rejected, true)]
    [InlineData(ErpStatus.PoCreated, ErpStatus.Rejected, false)]
    [InlineData(ErpStatus.Rejected, ErpStatus.Released, false)]
    [InlineData(ErpStatus.Closed, ErpStatus.Created, false)]
    public void Status_only_moves_forward(ErpStatus from, ErpStatus to, bool allowed) =>
        from.CanTransitionTo(to).ShouldBe(allowed);
}
