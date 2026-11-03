using System.Globalization;
using Procurement.SyntheticData;

namespace Mocks.SapApi;

/// <summary>Which data set the SAP mock mirrors: the client's workbook (default) or none (empty SAP, generated tests).</summary>
public enum Dataset { Workbook, Generated }

/// <summary>
/// The client's "mock remote" behaviour from the workbook: document numbers from Mock_Remote_Status, documents that
/// already exist (DUPLICATE), and the scheduled 429 / 503 / 409 responses from Failure_Injection.
/// With the Generated dataset none of this applies and SAP starts empty, behaving like real SAP (duplicates allowed).
/// </summary>
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

    /// <summary>The workbook's remote answers a create for an existing reference with 409 and the existing document id.</summary>
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

            // DUPLICATE: the document is already in SAP before the integration ever runs.
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

    /// <summary>
    /// Counts a create for this reference and returns the scheduled failure for this attempt, if any.
    /// RATE_LIMIT and TRANSIENT_5XX fail the first Trigger_Attempts creates, before anything is processed.
    /// </summary>
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

    /// <summary>VERSION_CONFLICT: the remote knows a newer version of the requisition than the one being sent.</summary>
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

    /// <summary>The workbook's document id for this reference, or a new MOCK-SAP-6xxxx id for requisitions it does not list.</summary>
    public string? DocumentIdFor(string reference)
    {
        lock (_gate)
        {
            if (Dataset != Dataset.Workbook) return null;
            return _documentIds.TryGetValue(reference, out var id) ? id : $"MOCK-SAP-{_nextFallback++}";
        }
    }
}
