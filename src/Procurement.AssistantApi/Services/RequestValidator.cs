using Microsoft.Extensions.Options;
using Procurement.AssistantApi.Model;

namespace Procurement.AssistantApi.Services;

/// <summary>
/// Validates an assistant's request against the catalogue and policy limits. Every issue carries a field path,
/// a stable code and a concrete suggestion, so an agent can correct itself without guessing.
/// </summary>
public sealed class RequestValidator(IOptions<AssistantOptions> options, TimeProvider time)
{
    public ValidationResult Validate(CreateRequisitionRequest request)
    {
        var o = options.Value;
        var issues = new List<ValidationIssue>();
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        var costCenter = ReferenceCatalogue.CostCenter(request.CostCenter);
        if (costCenter is null)
            issues.Add(new("costCenter", "UNKNOWN_COST_CENTER", $"Cost center '{request.CostCenter}' does not exist.",
                "Pick one from GET /v1/reference/cost-centers."));

        var plant = ReferenceCatalogue.Plant(request.Plant);
        if (plant is null)
            issues.Add(new("plant", "UNKNOWN_PLANT", $"Plant '{request.Plant}' does not exist.", "Pick one from GET /v1/reference/plants."));
        else if (costCenter is not null && plant.CompanyCode != costCenter.CompanyCode)
            issues.Add(new("plant", "PLANT_NOT_IN_COMPANY",
                $"Plant {plant.Code} belongs to company {plant.CompanyCode}, but cost center {costCenter.Code} belongs to company {costCenter.CompanyCode}.",
                $"Use one of: {string.Join(", ", ReferenceCatalogue.Plants.Where(p => p.CompanyCode == costCenter.CompanyCode).Select(p => p.Code))}."));

        if (request.NeededBy <= today || request.NeededBy > today.AddDays(o.MaxNeededByDays))
            issues.Add(new("neededBy", "NEEDED_BY_OUT_OF_RANGE",
                $"neededBy must be between {today.AddDays(1):yyyy-MM-dd} and {today.AddDays(o.MaxNeededByDays):yyyy-MM-dd}.",
                "Ask the user for a date in that range."));

        var justification = request.Justification?.Trim() ?? "";
        if (justification.Length is < 10 or > 500)
            issues.Add(new("justification", "JUSTIFICATION_LENGTH", "justification must be 10-500 characters.",
                "Summarise the user's business reason in one or two sentences."));

        var lines = request.Lines ?? [];
        if (lines.Count == 0 || lines.Count > o.MaxLines)
            issues.Add(new("lines", "LINES_COUNT", $"A request needs 1-{o.MaxLines} lines; got {lines.Count}.",
                lines.Count == 0 ? "Add at least one line." : "Split the request into several requests."));

        decimal total = 0;
        var priced = true;
        var seen = new HashSet<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var material = ReferenceCatalogue.Material(line.MaterialCode);
            if (material is null)
            {
                priced = false;
                issues.Add(new($"lines[{i}].materialCode", "UNKNOWN_MATERIAL", $"Material '{line.MaterialCode}' is not in the catalogue.",
                    "Search GET /v1/reference/materials?search=<words> and use the returned code."));
                continue;
            }
            if (!seen.Add(material.Code))
                issues.Add(new($"lines[{i}].materialCode", "DUPLICATE_LINE", $"{material.Code} appears more than once.",
                    "Combine the quantities into one line."));

            var wholeOnly = ReferenceCatalogue.CountedUnits.Contains(material.Uom);
            if (line.Quantity <= 0 || line.Quantity > o.MaxQuantityPerLine || (wholeOnly && line.Quantity != decimal.Truncate(line.Quantity)))
            {
                priced = false;
                issues.Add(new($"lines[{i}].quantity", "INVALID_QUANTITY",
                    $"Quantity must be greater than 0 and at most {o.MaxQuantityPerLine}{(wholeOnly ? $", in whole {material.Uom}" : "")}.",
                    "Confirm the quantity with the user."));
                continue;
            }
            total += Math.Round(line.Quantity * material.UnitPrice, 2);
        }

        if (priced && lines.Count > 0 && total > o.MaxRequestAmount)
            issues.Add(new("lines", "SPEND_LIMIT_EXCEEDED",
                $"Estimated total {total:N2} exceeds the {o.MaxRequestAmount:N2} limit for assistant-raised requests.",
                "Reduce the quantities, split the request, or ask the user to raise it directly with procurement."));

        var valid = issues.Count == 0;
        return new ValidationResult(valid, priced && lines.Count > 0 ? total : null, costCenter?.Currency, issues);
    }
}
