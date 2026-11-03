using System.Globalization;

namespace Procurement.SyntheticData;

public sealed record PackLine(
    string RequisitionId, int LineNumber, string ItemCode, string Description, decimal Quantity, string Unit,
    decimal UnitPrice, string CostCentre, string? TaxCode, decimal LineTotal);

public sealed record PackRequisition(
    string Id, long Version, string ApprovalStatus, string Department, string Purpose, string Currency,
    decimal? TotalAmount, DateOnly? RequiredBy, int PageNumber, string RecordQuality, string ExpectedOutcome,
    IReadOnlyList<PackLine> Lines);

public sealed record PackFailure(
    string RequisitionId, string Mode, int TriggerAttempts, int HttpStatus, int RetryAfterSeconds,
    long? RemoteCurrentVersion, string ExpectedBehaviour);

public sealed record PackRemoteStatus(
    string RequisitionId, string RemoteDocumentId, string RemoteStatus, long RemoteVersion, int LastResponseCode,
    string LastResponseMessage, string ExpectedFinalState);

public sealed record PackQuarantineExpectation(
    string RequisitionId, string FieldName, string RejectedValue, string ReasonCode, string ReasonText, bool Retryable);

public sealed record PackReconciliationExpectation(
    string RequisitionId, long SourceVersion, string ProcessingResult, string? RemoteDocumentId, int Attempts,
    string FailureCategory, string Explanation);

public sealed record PackApiTest(
    string TestId, string Operation, string EmployeeId, string RequestSummary, string AuthorisationRule,
    bool ConfirmationRequired, int ExpectedHttpStatus, string ExpectedResult);

public sealed record PackSupplier(string Id, string Name, string Category, string Country, bool Active, string PaymentTerms);

/// <summary>
/// The client's synthetic data pack ("Challenge 2: Procurement Integration"), read straight from the Excel workbook.
/// The source and SAP mocks serve it, and the scenario tests compare our results with its expected-outcome sheets.
/// </summary>
public sealed class DataPack
{
    public const string FileName = "Sajid_Procurement_Integration_Synthetic_Data_Expanded.xlsx";

    /// <summary>
    /// The workbook has no company code or plant. Every requisition belongs to one synthetic Qatar entity;
    /// these codes are our assumption and are only used to satisfy SAP's organisational fields.
    /// </summary>
    public const string CompanyCode = "3000";
    public const string Plant = "3100";

    public required IReadOnlyList<PackRequisition> Requisitions { get; init; }
    public required IReadOnlyList<PackFailure> Failures { get; init; }
    public required IReadOnlyList<PackRemoteStatus> RemoteStatus { get; init; }
    public required IReadOnlyList<PackQuarantineExpectation> QuarantineExpected { get; init; }
    public required IReadOnlyList<PackReconciliationExpectation> ReconciliationExpected { get; init; }
    public required IReadOnlyList<PackApiTest> ApiTests { get; init; }
    public required IReadOnlyList<PackSupplier> Suppliers { get; init; }

    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<string, DataPack> Cache = new(StringComparer.Ordinal);

    /// <summary>Loads (and caches) the workbook at <paramref name="path"/>, or the bundled copy when null.</summary>
    public static DataPack Load(string? path = null)
    {
        var resolved = DataFiles.Resolve(path, FileName);
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(resolved, out var pack))
            {
                pack = Parse(WorkbookReader.ReadAll(resolved));
                Cache[resolved] = pack;
            }
            return pack;
        }
    }

    private static DataPack Parse(Dictionary<string, List<Dictionary<string, object?>>> sheets)
    {
        List<Dictionary<string, object?>> Sheet(string name) =>
            sheets.TryGetValue(name, out var rows) ? rows : throw new InvalidDataException($"The workbook has no '{name}' sheet");

        var lines = Sheet("Requisition_Lines").Select(r => new PackLine(
                Text(r, "Source_Requisition_ID")!, Int(r, "Line_Number"), Text(r, "Item_Code")!, Text(r, "Description")!,
                Dec(r, "Quantity") ?? 0, Text(r, "Unit")!, Dec(r, "Unit_Price_QAR") ?? 0, Text(r, "Cost_Centre")!,
                Text(r, "Tax_Code"), Dec(r, "Line_Total_QAR") ?? 0))
            .ToLookup(l => l.RequisitionId);

        return new DataPack
        {
            Requisitions = Sheet("Approved_Requisitions").Select(r =>
            {
                var id = Text(r, "Source_Requisition_ID")!;
                return new PackRequisition(id, Int(r, "Source_Version"), Text(r, "Approval_Status")!, Text(r, "Requester_Dept")!,
                    Text(r, "Business_Purpose")!, Text(r, "Currency")!, Dec(r, "Total_Amount"), Date(r, "Required_By"),
                    Int(r, "Page_Number"), Text(r, "Record_Quality")!, Text(r, "Expected_Outcome")!,
                    lines[id].OrderBy(l => l.LineNumber).ToList());
            }).ToList(),
            Failures = Sheet("Failure_Injection").Select(r => new PackFailure(
                Text(r, "Source_Requisition_ID")!, Text(r, "Failure_Mode")!, Int(r, "Trigger_Attempts"), Int(r, "HTTP_Status"),
                Int(r, "Retry_After_Seconds"), Long(r, "Remote_Current_Version"), Text(r, "Expected_Handler_Behaviour")!)).ToList(),
            RemoteStatus = Sheet("Mock_Remote_Status").Select(r => new PackRemoteStatus(
                Text(r, "Source_Requisition_ID")!, Text(r, "Remote_Document_ID")!, Text(r, "Remote_Status")!, Int(r, "Remote_Version"),
                Int(r, "Last_Response_Code"), Text(r, "Last_Response_Message")!, Text(r, "Expected_Final_State")!)).ToList(),
            QuarantineExpected = Sheet("Quarantine_Expected").Select(r => new PackQuarantineExpectation(
                Text(r, "Source_Requisition_ID")!, Text(r, "Field_Name")!, Text(r, "Rejected_Value") ?? "", Text(r, "Reason_Code")!,
                Text(r, "Reason_Text")!, Bool(r, "Retryable"))).ToList(),
            ReconciliationExpected = Sheet("Reconciliation_Expected").Select(r => new PackReconciliationExpectation(
                Text(r, "Source_Requisition_ID")!, Int(r, "Source_Version"), Text(r, "Processing_Result")!,
                Text(r, "Remote_Document_ID"), Int(r, "Attempts"), Text(r, "Failure_Category")!, Text(r, "Explanation")!)).ToList(),
            ApiTests = Sheet("AI_API_Contract_Tests").Select(r => new PackApiTest(
                Text(r, "Test_ID")!, Text(r, "Operation")!, Text(r, "SignedIn_Employee_ID")!, Text(r, "Request_Payload_Summary")!,
                Text(r, "Authorisation_Rule")!, Bool(r, "Confirmation_Required"), Int(r, "Expected_HTTP_Status"), Text(r, "Expected_Result")!)).ToList(),
            Suppliers = Sheet("Mock_Suppliers").Select(r => new PackSupplier(
                Text(r, "Supplier_ID")!, Text(r, "Supplier_Name")!, Text(r, "Category")!, Text(r, "Country")!, Bool(r, "Active"),
                Text(r, "Payment_Terms")!)).ToList(),
        };
    }

    private static object? Value(Dictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) ? v : throw new InvalidDataException($"Column '{column}' is missing");

    private static string? Text(Dictionary<string, object?> row, string column) => Value(row, column) switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        double d => d.ToString(CultureInfo.InvariantCulture),
        var v => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    private static decimal? Dec(Dictionary<string, object?> row, string column) => Value(row, column) switch
    {
        double d => (decimal)d,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static int Int(Dictionary<string, object?> row, string column) => (int)(Dec(row, column) ?? 0);

    private static long? Long(Dictionary<string, object?> row, string column) => Dec(row, column) is { } d ? (long)d : null;

    private static bool Bool(Dictionary<string, object?> row, string column) => Value(row, column) switch
    {
        bool b => b,
        double d => d != 0,
        string s => bool.TryParse(s, out var b) && b,
        _ => false,
    };

    /// <summary>Excel stores dates as serial numbers; a blank cell (or the text "blank") means no date.</summary>
    private static DateOnly? Date(Dictionary<string, object?> row, string column) => Value(row, column) switch
    {
        double d => DateOnly.FromDateTime(DateTime.FromOADate(d)),
        string s when DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}
