using Mocks.Shared;

namespace Mocks.SapApi;

// Duplicate ExternalReferences are allowed, as in SAP.
public sealed class SapStore(MonotonicClock clock)
{
    // Plant to company code. SAP rejects a plant used outside its company code.
    public static readonly IReadOnlyDictionary<string, string> PlantCompany = new Dictionary<string, string>
    {
        ["1100"] = "1000", ["1200"] = "1000", ["2100"] = "2000", ["2200"] = "2000",
        // The workbook has no org codes, so we assume one Qatar entity for it.
        ["3100"] = "3000",
    };

    private static readonly HashSet<string> Units = ["EA", "BOX", "KG", "L", "M", "H"];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SapRequisition> _rows = new(StringComparer.Ordinal);
    private long _nextNumber = 10_000_001;
    private long _nextPo = 4_500_000_001;

    public static List<string> Validate(PurchaseRequisitionInput input, bool isCreate)
    {
        var errors = new List<string>();
        if (isCreate && string.IsNullOrWhiteSpace(input.ExternalReference)) errors.Add("ExternalReference is required");
        if (input.PurchaseRequisitionType is not null and not "NB") errors.Add($"Document type {input.PurchaseRequisitionType} is not allowed");
        if (isCreate && (input.Items is null || input.Items.Count == 0)) errors.Add("At least one item is required");

        foreach (var (item, index) in (input.Items ?? []).Select((item, index) => (item, index)))
        {
            var label = $"Item {item.PurchaseRequisitionItem ?? ((index + 1) * 10).ToString("D5")}";
            if (item.RequestedQuantity is null or <= 0) errors.Add($"{label}: RequestedQuantity must be positive");
            if (item.PurchaseRequisitionPrice is null or < 0) errors.Add($"{label}: PurchaseRequisitionPrice must not be negative");
            if (item.BaseUnit is null || !Units.Contains(item.BaseUnit)) errors.Add($"{label}: unit {item.BaseUnit} is not defined");
            if (item.PurReqnItemCurrency is not { Length: 3 }) errors.Add($"{label}: currency is invalid");
            if (item.DeliveryDate is null) errors.Add($"{label}: DeliveryDate is required");
            if (item.Plant is null || !PlantCompany.TryGetValue(item.Plant, out var company))
                errors.Add($"{label}: plant {item.Plant} does not exist");
            else if (company != item.CompanyCode)
                errors.Add($"{label}: plant {item.Plant} is not assigned to company code {item.CompanyCode}");
        }
        return errors;
    }

    public SapRequisition Create(PurchaseRequisitionInput input, string? number = null, string createdBy = "INTEGRATION")
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            var pr = new SapRequisition
            {
                PurchaseRequisition = number ?? (_nextNumber++).ToString("D10"),
                PurReqnDescription = input.PurReqnDescription,
                ExternalReference = input.ExternalReference!,
                ExternalVersion = input.ExternalVersion ?? 0,
                CreatedByUser = createdBy,
                CreationDateTime = now,
                LastChangeDateTime = now,
                Items = MapItems(input.Items!),
            };
            _rows[pr.PurchaseRequisition] = pr;
            return Clone(pr);
        }
    }

    // For documents that existed before the integration started (the workbook's DUPLICATE rows).
    public void Seed(SapRequisition pr, SapLifecycle lifecycle)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            _rows[pr.PurchaseRequisition] = pr;
            pr.LastChangeDateTime = now;
            if (lifecycle != SapLifecycle.Created) SetLifecycleLocked(pr, lifecycle);
        }
    }

    public SapRequisition? Get(string number)
    {
        lock (_gate) return _rows.TryGetValue(number, out var pr) ? Clone(pr) : null;
    }

    public IReadOnlyList<SapRequisition> FindByExternalReference(string reference)
    {
        lock (_gate)
        {
            return _rows.Values.Where(r => r.ExternalReference == reference)
                .OrderBy(r => r.PurchaseRequisition, StringComparer.Ordinal).Select(Clone).ToList();
        }
    }

    public (IReadOnlyList<SapRequisition> Items, KeysetCursor? Next) ChangedSince(DateTime? since, KeysetCursor? after, int top)
    {
        lock (_gate)
        {
            var matches = _rows.Values
                .Where(r => since is null || r.LastChangeDateTime > since)
                .Where(r => after is null || after.Value.IsBefore(r.LastChangeDateTime, r.PurchaseRequisition))
                .OrderBy(r => r.LastChangeDateTime).ThenBy(r => r.PurchaseRequisition, StringComparer.Ordinal)
                .Take(top + 1)
                .ToList();
            var page = matches.Take(top).Select(Clone).ToList();
            KeysetCursor? next = matches.Count > top ? new KeysetCursor(page[^1].LastChangeDateTime, page[^1].PurchaseRequisition) : null;
            return (page, next);
        }
    }

    public enum PatchOutcome { Updated, NotFound, PreconditionMissing, PreconditionFailed, Locked }

    public (PatchOutcome Outcome, SapRequisition? Row, long Version) Patch(string number, HttpRequest request, PurchaseRequisitionInput input)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(number, out var pr)) return (PatchOutcome.NotFound, null, 0);
            switch (ETags.CheckIfMatch(request, pr.Version))
            {
                case PreconditionResult.Missing: return (PatchOutcome.PreconditionMissing, null, pr.Version);
                case PreconditionResult.Failed: return (PatchOutcome.PreconditionFailed, null, pr.Version);
            }
            if (pr.Lifecycle is not (SapLifecycle.Created or SapLifecycle.InRelease))
                return (PatchOutcome.Locked, Clone(pr), pr.Version);

            if (input.PurReqnDescription is not null) pr.PurReqnDescription = input.PurReqnDescription;
            if (input.ExternalVersion is not null) pr.ExternalVersion = input.ExternalVersion.Value;
            if (input.Items is not null) pr.Items = MapItems(input.Items);
            Touch(pr);
            return (PatchOutcome.Updated, Clone(pr), pr.Version);
        }
    }

    public int AdvanceAll(int rejectEvery)
    {
        lock (_gate)
        {
            var moved = 0;
            foreach (var pr in _rows.Values.OrderBy(r => r.PurchaseRequisition, StringComparer.Ordinal))
            {
                var next = pr.Lifecycle switch
                {
                    SapLifecycle.Created => SapLifecycle.InRelease,
                    SapLifecycle.InRelease => rejectEvery > 0 && NumericPart(pr.PurchaseRequisition) % rejectEvery == 0
                        ? SapLifecycle.Rejected
                        : SapLifecycle.Released,
                    SapLifecycle.Released => SapLifecycle.PoCreated,
                    SapLifecycle.PoCreated => SapLifecycle.Closed,
                    _ => pr.Lifecycle,
                };
                if (next == pr.Lifecycle) continue;
                SetLifecycleLocked(pr, next);
                moved++;
            }
            return moved;
        }
    }

    public SapRequisition? SetLifecycle(string number, SapLifecycle lifecycle)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(number, out var pr)) return null;
            SetLifecycleLocked(pr, lifecycle);
            return Clone(pr);
        }
    }

    // A buyer editing the requisition in SAP, which changes its ETag.
    public SapRequisition? Touch(string number)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(number, out var pr)) return null;
            pr.PurReqnDescription = (pr.PurReqnDescription ?? "") + " [edited in SAP]";
            Touch(pr);
            return Clone(pr);
        }
    }

    public bool Delete(string number)
    {
        lock (_gate) return _rows.Remove(number);
    }

    // Accepts combinations SAP would never produce.
    public SapRequisition? SetRawStatus(string number, string? processingStatus, string? releaseStatus)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(number, out var pr)) return null;
            if (processingStatus is not null) pr.ProcessingStatus = processingStatus;
            if (releaseStatus is not null) pr.ReleaseStatus = releaseStatus;
            Touch(pr);
            return Clone(pr);
        }
    }

    public IReadOnlyList<SapRequisition> All()
    {
        lock (_gate) return _rows.Values.OrderBy(r => r.PurchaseRequisition, StringComparer.Ordinal).Select(Clone).ToList();
    }

    public object Stats()
    {
        lock (_gate)
        {
            return new
            {
                total = _rows.Count,
                byLifecycle = _rows.Values.GroupBy(r => r.Lifecycle.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                duplicateExternalReferences = _rows.Values.GroupBy(r => r.ExternalReference)
                    .Where(g => g.Count() > 1)
                    .Select(g => new { externalReference = g.Key, requisitions = g.Select(r => r.PurchaseRequisition) }),
            };
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _rows.Clear();
            _nextNumber = 10_000_001;
            _nextPo = 4_500_000_001;
        }
    }

    private static long NumericPart(string number) =>
        long.TryParse(new string(number.Where(char.IsDigit).ToArray()), out var n) ? n : 0;

    private void SetLifecycleLocked(SapRequisition pr, SapLifecycle lifecycle)
    {
        pr.Lifecycle = lifecycle;
        (pr.ProcessingStatus, pr.ReleaseStatus) = lifecycle switch
        {
            SapLifecycle.Created => ("N", ""),
            SapLifecycle.InRelease => ("N", "01"),
            SapLifecycle.Released => ("N", "02"),
            SapLifecycle.PoCreated => ("B", "02"),
            SapLifecycle.Closed => ("L", "02"),
            SapLifecycle.Rejected => ("N", "08"),
            _ => (pr.ProcessingStatus, pr.ReleaseStatus),
        };
        if (lifecycle == SapLifecycle.PoCreated)
        {
            var po = (_nextPo++).ToString();
            foreach (var item in pr.Items) item.PurchaseOrder = po;
        }
        Touch(pr);
    }

    private void Touch(SapRequisition pr)
    {
        pr.Version++;
        pr.LastChangeDateTime = clock.UtcNow;
    }

    private static List<SapRequisitionItem> MapItems(IEnumerable<PurchaseRequisitionItemInput> items) =>
        items.Select((i, n) => new SapRequisitionItem
        {
            PurchaseRequisitionItem = i.PurchaseRequisitionItem ?? ((n + 1) * 10).ToString("D5"),
            Material = i.Material,
            PurchaseRequisitionItemText = i.PurchaseRequisitionItemText,
            MaterialGroup = i.MaterialGroup,
            RequestedQuantity = i.RequestedQuantity ?? 0,
            BaseUnit = i.BaseUnit ?? "",
            PurchaseRequisitionPrice = i.PurchaseRequisitionPrice ?? 0,
            PurReqnItemCurrency = i.PurReqnItemCurrency ?? "",
            Plant = i.Plant ?? "",
            CompanyCode = i.CompanyCode ?? "",
            PurchasingGroup = i.PurchasingGroup ?? "001",
            TaxCode = i.TaxCode,
            DeliveryDate = i.DeliveryDate ?? default,
        }).ToList();

    private static SapRequisition Clone(SapRequisition pr) => new()
    {
        PurchaseRequisition = pr.PurchaseRequisition,
        PurchaseRequisitionType = pr.PurchaseRequisitionType,
        PurReqnDescription = pr.PurReqnDescription,
        ExternalReference = pr.ExternalReference,
        ExternalVersion = pr.ExternalVersion,
        ProcessingStatus = pr.ProcessingStatus,
        ReleaseStatus = pr.ReleaseStatus,
        CreatedByUser = pr.CreatedByUser,
        CreationDateTime = pr.CreationDateTime,
        LastChangeDateTime = pr.LastChangeDateTime,
        Version = pr.Version,
        Lifecycle = pr.Lifecycle,
        Items = pr.Items.Select(i => new SapRequisitionItem
        {
            PurchaseRequisitionItem = i.PurchaseRequisitionItem,
            Material = i.Material,
            PurchaseRequisitionItemText = i.PurchaseRequisitionItemText,
            MaterialGroup = i.MaterialGroup,
            RequestedQuantity = i.RequestedQuantity,
            BaseUnit = i.BaseUnit,
            PurchaseRequisitionPrice = i.PurchaseRequisitionPrice,
            PurReqnItemCurrency = i.PurReqnItemCurrency,
            Plant = i.Plant,
            CompanyCode = i.CompanyCode,
            PurchasingGroup = i.PurchasingGroup,
            TaxCode = i.TaxCode,
            DeliveryDate = i.DeliveryDate,
            PurchaseOrder = i.PurchaseOrder,
        }).ToList(),
    };
}
