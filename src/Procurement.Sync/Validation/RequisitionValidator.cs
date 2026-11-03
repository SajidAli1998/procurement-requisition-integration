using FluentValidation;
using Procurement.Contracts;

namespace Procurement.Sync.Validation;

/// <summary>
/// Business rules on the canonical model. Each rule's error code is a <see cref="QuarantineReason"/>,
/// so a failure maps straight to a quarantine reason. Line rules come first so the most specific reason wins.
/// </summary>
public sealed class RequisitionValidator : AbstractValidator<Requisition>
{
    public RequisitionValidator(TimeProvider time)
    {
        RuleFor(r => r.Lines).NotEmpty()
            .WithErrorCode(nameof(QuarantineReason.MissingLines))
            .WithMessage("Requisition has no lines");

        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Quantity).GreaterThan(0)
                .WithErrorCode(nameof(QuarantineReason.InvalidQuantity))
                .WithMessage(l => $"Line {l.LineNo}: quantity {l.Quantity} must be greater than zero");
            line.RuleFor(l => l.UnitPrice).GreaterThanOrEqualTo(0)
                .WithErrorCode(nameof(QuarantineReason.InvalidPrice))
                .WithMessage(l => $"Line {l.LineNo}: unit price {l.UnitPrice} must not be negative");
            line.RuleFor(l => l.Uom).Must(ReferenceData.Units.Contains)
                .WithErrorCode(nameof(QuarantineReason.UnknownUnitOfMeasure))
                .WithMessage(l => $"Line {l.LineNo}: unit of measure '{l.Uom}' is not recognised");
            line.RuleFor(l => l.Plant).Must(ReferenceData.Plants.Contains)
                .WithErrorCode(nameof(QuarantineReason.UnknownPlant))
                .WithMessage(l => $"Line {l.LineNo}: plant '{l.Plant}' is not recognised");
        });

        RuleFor(r => r.Currency).Must(ReferenceData.Currencies.Contains)
            .WithErrorCode(nameof(QuarantineReason.UnknownCurrency))
            .WithMessage(r => $"Currency '{r.Currency}' is not a supported ISO 4217 code");

        RuleFor(r => r.NeededBy).Must(d => d >= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime))
            .WithErrorCode(nameof(QuarantineReason.NeededByInPast))
            .WithMessage(r => $"neededBy {r.NeededBy:yyyy-MM-dd} is in the past");

        // Checked before the line total, so a zero total is reported as such rather than as a mismatch.
        RuleFor(r => r.TotalAmount).GreaterThan(0)
            .WithErrorCode(nameof(QuarantineReason.AmountNotPositive))
            .WithMessage(r => $"Total amount must be greater than zero (got {r.TotalAmount})");

        RuleFor(r => r).Must(TotalMatches).When(r => r.Lines.Count > 0 && r.TotalAmount > 0)
            .WithErrorCode(nameof(QuarantineReason.TotalMismatch))
            .WithMessage(r => $"totalAmount {r.TotalAmount} does not equal the sum of the lines ({LineTotal(r)})");
    }

    public static decimal LineTotal(Requisition r) => Math.Round(r.Lines.Sum(l => l.Quantity * l.UnitPrice), 2);

    private static bool TotalMatches(Requisition r) => Math.Abs(LineTotal(r) - r.TotalAmount) <= 0.01m;

    public List<ValidationProblem> Check(Requisition requisition) =>
        Validate(requisition).Errors
            .Select(e => new ValidationProblem(Enum.Parse<QuarantineReason>(e.ErrorCode), e.PropertyName, e.ErrorMessage))
            .ToList();
}
