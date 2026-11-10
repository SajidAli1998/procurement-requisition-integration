using FluentValidation;
using Procurement.Contracts;

namespace Procurement.Sync.Validation;

// Each error code is a QuarantineReason name. Line rules come first so the most specific reason wins.
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
            line.RuleFor(l => l.Quantity).Must(q => q == decimal.Truncate(q)).When(l => ReferenceData.WholeUnits.Contains(l.Uom) && l.Quantity > 0)
                .WithErrorCode(nameof(QuarantineReason.InvalidQuantity))
                .WithMessage(l => $"Line {l.LineNo}: quantity {l.Quantity} must be a whole number for unit {l.Uom}");
            line.RuleFor(l => l.Quantity).Must(q => DecimalPlaces(q) <= 3).When(l => l.Quantity > 0)
                .WithErrorCode(nameof(QuarantineReason.InvalidQuantity))
                .WithMessage(l => $"Line {l.LineNo}: quantity {l.Quantity} has more than 3 decimal places");
            line.RuleFor(l => l.UnitPrice).GreaterThanOrEqualTo(0)
                .WithErrorCode(nameof(QuarantineReason.InvalidPrice))
                .WithMessage(l => $"Line {l.LineNo}: unit price {l.UnitPrice} must not be negative");
            line.RuleFor(l => l.UnitPrice).Must(p => DecimalPlaces(p) <= 2).When(l => l.UnitPrice >= 0)
                .WithErrorCode(nameof(QuarantineReason.InvalidPrice))
                .WithMessage(l => $"Line {l.LineNo}: unit price {l.UnitPrice} has more than 2 decimal places; prices are in currency units and cents");
            line.RuleFor(l => l.Description).Must(NoControlCharacters)
                .WithErrorCode(nameof(QuarantineReason.SchemaInvalid))
                .WithMessage(l => $"Line {l.LineNo}: description contains control characters");
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

        RuleFor(r => r.Justification).Must(j => j is null || NoControlCharacters(j))
            .WithErrorCode(nameof(QuarantineReason.SchemaInvalid))
            .WithMessage("justification contains control characters");

        RuleFor(r => r.ApprovedAt).Must(a => a is null || a.Value <= time.GetUtcNow().AddMinutes(5))
            .WithErrorCode(nameof(QuarantineReason.InvalidApproval))
            .WithMessage(r => $"approvedAt {r.ApprovedAt:O} is in the future");

        // Before the line total, so a zero total is reported as zero rather than as a mismatch.
        RuleFor(r => r.TotalAmount).GreaterThan(0)
            .WithErrorCode(nameof(QuarantineReason.AmountNotPositive))
            .WithMessage(r => $"Total amount must be greater than zero (got {r.TotalAmount})");

        RuleFor(r => r).Must(TotalMatches).When(r => r.Lines.Count > 0 && r.TotalAmount > 0)
            .WithErrorCode(nameof(QuarantineReason.TotalMismatch))
            .WithMessage(r => $"totalAmount {r.TotalAmount} does not equal the sum of the lines ({LineTotal(r)})");
    }

    private static int DecimalPlaces(decimal d)
    {
        d = Math.Abs(d);
        var places = 0;
        while (d != decimal.Truncate(d) && places < 28)
        {
            d *= 10;
            places++;
        }
        return places;
    }

    // Tabs and line breaks are fine in free text.
    private static bool NoControlCharacters(string s) => !s.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));

    public static decimal LineTotal(Requisition r) => Math.Round(r.Lines.Sum(l => l.Quantity * l.UnitPrice), 2);

    private static bool TotalMatches(Requisition r) => Math.Abs(LineTotal(r) - r.TotalAmount) <= 0.01m;

    public List<ValidationProblem> Check(Requisition requisition) =>
        Validate(requisition).Errors
            .Select(e => new ValidationProblem(Enum.Parse<QuarantineReason>(e.ErrorCode), e.PropertyName, e.ErrorMessage))
            .ToList();
}
