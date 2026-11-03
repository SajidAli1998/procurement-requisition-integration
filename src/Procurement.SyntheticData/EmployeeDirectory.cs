using System.Text.Json;

namespace Procurement.SyntheticData;

public sealed record Employee(string Id, string Name, string Department, string CostCentre, bool CanRaise, bool ProcurementDelegate);

/// <summary>
/// Who raised which workbook requisition, and what each employee may do through the assistant.
/// The client's workbook does not contain this, so data/employee-directory.json records our assumption
/// (see its "_about" note and scripts/generate_employee_directory.py).
/// </summary>
public sealed class EmployeeDirectory
{
    public const string FileName = "employee-directory.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public required IReadOnlyList<Employee> Employees { get; init; }
    public required IReadOnlyDictionary<string, string> RequisitionRequesters { get; init; }

    public Employee? Find(string id) => Employees.FirstOrDefault(e => e.Id == id);

    public static EmployeeDirectory Load(string? path = null)
    {
        var file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(DataFiles.Resolve(path, FileName)), Json)
            ?? throw new InvalidDataException("The employee directory is empty");
        return new EmployeeDirectory { Employees = file.Employees, RequisitionRequesters = file.RequisitionRequesters };
    }

    private sealed record FileShape(List<Employee> Employees, Dictionary<string, string> RequisitionRequesters);
}
