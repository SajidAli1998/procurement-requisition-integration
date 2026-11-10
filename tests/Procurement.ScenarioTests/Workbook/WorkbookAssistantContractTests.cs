using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Procurement.AssistantApi.Infrastructure;
using Procurement.ScenarioTests.Assistant;
using Procurement.ScenarioTests.Infrastructure;
using Procurement.SyntheticData;

namespace Procurement.ScenarioTests.Workbook;

/// <summary>
/// Runs every row of the workbook's AI_API_Contract_Tests sheet. The workbook does not say who owns what or who may
/// raise requests, so that comes from our own data/employee-directory.json.
/// </summary>
public sealed partial class WorkbookAssistantContractTests
{
    private static readonly DataPack Pack = DataPack.Load();
    private static readonly EmployeeDirectory Directory = EmployeeDirectory.Load();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "W07 All 60 AI_API_Contract_Tests rows return the expected status")]
    public async Task Contract_tests_from_the_workbook()
    {
        await using var h = new SyncHarness(new() { ["Sync:Resilience:BreakerMinimumThroughput"] = "100" }, dataset: "Workbook");
        await h.RunAsync(); // so status reads include the transfer to SAP
        var results = new List<string>();

        foreach (var test in Pack.ApiTests)
        {
            var client = h.Assistant.CreateClient();
            var employee = Directory.Find(test.EmployeeId);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                DevTokens.Issue(AssistantApiFactory.Auth, test.EmployeeId, DevTokens.ScopesFor(employee)));

            HttpStatusCode status;
            if (test.Operation == "GET status")
            {
                var id = RequisitionId().Match(test.RequestSummary).Value;
                status = (await client.GetAsync($"/v1/requisition-requests/{id}", Ct)).StatusCode;
            }
            else
            {
                var body = DraftFor(test, employee);
                if (test.ConfirmationRequired && test.ExpectedHttpStatus == 201)
                {
                    // Confirmation is enforced: without it nothing is created.
                    var unconfirmed = await AssistantApiTests.CreateAsync(client, body, key: test.TestId + "-unconfirmed", confirm: false);
                    if (unconfirmed.StatusCode != HttpStatusCode.PreconditionRequired)
                        results.Add($"{test.TestId}: without confirmation expected 428, got {(int)unconfirmed.StatusCode}");
                }
                status = (await AssistantApiTests.CreateAsync(client, body, key: test.TestId)).StatusCode;
            }

            if ((int)status != test.ExpectedHttpStatus)
                results.Add($"{test.TestId} {test.Operation} as {test.EmployeeId}: expected {test.ExpectedHttpStatus}, got {(int)status}");
        }

        results.ShouldBeEmpty(string.Join(Environment.NewLine, results));
    }

    /// <summary>"Create synthetic draft for Route control displays" becomes one item of that purpose, charged to the employee's cost centre.</summary>
    private static object DraftFor(PackApiTest test, Employee? employee)
    {
        var purpose = test.RequestSummary.Replace("Create synthetic draft for ", "", StringComparison.Ordinal);
        var item = Pack.Requisitions.Where(r => r.Purpose == purpose).SelectMany(r => r.Lines).OrderBy(l => l.UnitPrice).First();
        return new
        {
            costCenter = employee?.CostCentre ?? item.CostCentre,
            plant = DataPack.Plant,
            neededBy = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"),
            justification = $"{purpose} requested through the assistant ({test.TestId})",
            lines = new[] { new { materialCode = item.ItemCode, quantity = 1 } },
            assistantSessionRef = test.TestId,
        };
    }

    [GeneratedRegex(@"REQ-\d+")]
    private static partial Regex RequisitionId();
}
