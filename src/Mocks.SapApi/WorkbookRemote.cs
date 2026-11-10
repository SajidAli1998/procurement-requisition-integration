using System.Globalization;
using Procurement.SyntheticData;

namespace Mocks.SapApi;

public enum Dataset { Workbook, Generated }

// The workbook's "mock remote": document numbers from Mock_Remote_Status, documents that already exist (DUPLICATE),
// and the scheduled 429, 503 and 409 answers from Failure_Injection. With the Generated data set none of this
// applies and SAP behaves like real SAP, duplicates allowed.
public sealed class WorkbookRemote
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _postsByReference = new(StringComparer.Ordinal);
    private readonly string? _workbookPath;
    private Dictionary<string, PackFailure> _failures = new(StringComparer.Ordinal);
    private Dictionary<string, string> _documentIds = new(StringComparer.Ordinal);
    private long _nextFallback = 60_001;

    public WorkbookRemote(IConfiguration configuration, SapStore store)
    {
        _workbookPath = configuration["Dataset:WorkbookPath"];
        Reset(configuration.GetValue("Dataset:Source", Dataset.Workbook), store);
    }

    public Dataset Dataset { get; private set; }

    public bool RejectsDuplicateReferences => Dataset == Dataset.Workbook;

    public void Reset(Dataset dataset, SapStore store)
    {
        store.Reset();
        lock (_gate)
        {
            Dataset = dataset;
            _postsByReference.Clear();
            _nextFallback = 60_001;
            if (dataset != Dataset.Workbook)
            {
                _failures = new(StringComparer.Ordinal);
                _documentIds = new(StringComparer.Ordinal);
                return;
            }

            var pack = DataPack.Load(_workbookPath);
            _failures = pack.Failures.ToDictionary(f => f.RequisitionId, StringComparer.Ordinal);
            _documentIds = pack.RemoteStatus.ToDictionary(r => r.RequisitionId, r => r.RemoteDocumentId, StringComparer.Ordinal);

            var requisitions = pack.Requisitions.ToDictionary(r => r.Id);
            foreach (var f in pack.Failures.Where(f => f.Mode == "DUPLICATE"))
            {
                var r = requisitions[f.RequisitionId];
                var remote = pack.RemoteStatus.FirstOrDefault(s => s.RequisitionId == r.Id);
                store.Seed(new SapRequisition
                {
                    PurchaseRequisition = _documentIds[r.Id],
                    PurReqnDescription = r.Purpose,
                    ExternalReference = r.Id,
                    ExternalVersion = f.RemoteCurrentVersion ?? r.Version,
                    CreatedByUser = "EARLIER-LOAD",
                    Items = r.Lines.Select(l => new SapRequisitionItem
                    {
                        PurchaseRequisitionItem = l.LineNumber.ToString("D5", CultureInfo.InvariantCulture),
                        Material = l.ItemCode,
                        PurchaseRequisitionItemText = l.Description,
                        RequestedQuantity = l.Quantity,
                        BaseUnit = l.Unit,
                        PurchaseRequisitionPrice = l.UnitPrice,
                        PurReqnItemCurrency = r.Currency,
                        Plant = DataPack.Plant,
                        CompanyCode = DataPack.CompanyCode,
                        TaxCode = l.TaxCode,
                        DeliveryDate = r.RequiredBy ?? default,
                    }).ToList(),
                }, remote?.RemoteStatus == "Approved" ? SapLifecycle.Released : SapLifecycle.Created);
            }
        }
    }

    // RATE_LIMIT and TRANSIENT_5XX fail the first Trigger_Attempts creates for a reference.
    public PackFailure? ScheduledFailure(string reference)
    {
        lock (_gate)
        {
            var attempt = _postsByReference[reference] = _postsByReference.GetValueOrDefault(reference) + 1;
            return _failures.TryGetValue(reference, out var f) && f.Mode is "RATE_LIMIT" or "TRANSIENT_5XX" && attempt <= f.TriggerAttempts
                ? f
                : null;
        }
    }

    public long? NewerVersionThan(string reference, long sentVersion)
    {
        lock (_gate)
        {
            return _failures.TryGetValue(reference, out var f) && f.Mode == "VERSION_CONFLICT"
                && f.RemoteCurrentVersion is { } current && sentVersion < current
                ? current
                : null;
        }
    }

    // Requisitions the workbook does not list get a new MOCK-SAP-6xxxx id.
    public string? DocumentIdFor(string reference)
    {
        lock (_gate)
        {
            if (Dataset != Dataset.Workbook) return null;
            return _documentIds.TryGetValue(reference, out var id) ? id : $"MOCK-SAP-{_nextFallback++}";
        }
    }
}
