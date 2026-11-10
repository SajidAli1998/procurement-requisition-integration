using System.Globalization;
using System.Text.Json.Nodes;

namespace Procurement.Scenarios.Fixtures;

/// <summary>Valid by default (company 1000, plant 1100, USD, approved, needed in 30 days); each scenario bends one thing.</summary>
public static class Req
{
    public static string Today(int days = 0) => DateTime.UtcNow.Date.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static JsonObject Line(int lineNo = 10, string? material = "MAT-100", string description = "Laptop 14-inch", decimal quantity = 2,
        string uom = "EA", decimal price = 1200m, string plant = "1100", string? group = "IT") => new()
    {
        ["lineNo"] = lineNo,
        ["materialCode"] = material,
        ["description"] = description,
        ["materialGroup"] = group,
        ["quantity"] = quantity,
        ["uom"] = uom,
        ["unitPrice"] = price,
        ["plant"] = plant,
    };

    public static JsonObject Valid(string id, int lines = 1, string status = "Approved", string currency = "USD", string company = "1000",
        string plant = "1100", decimal quantity = 2, decimal price = 1200m)
    {
        var array = new JsonArray();
        for (var i = 1; i <= lines; i++) array.Add(Line(i * 10, $"MAT-{100 + i}", $"Item {i}", quantity, "EA", price, plant));
        var record = new JsonObject
        {
            ["id"] = id,
            ["status"] = status,
            ["requester"] = "user01@example.test",
            ["costCenter"] = "CC-1000-10",
            ["companyCode"] = company,
            ["currency"] = currency,
            ["neededBy"] = Today(30),
            ["justification"] = $"Lab scenario {id}",
            ["approvedAt"] = status == "Approved" ? DateTime.UtcNow.AddHours(-2).ToString("O") : null,
            ["lines"] = array,
            ["erpSync"] = null,
        };
        return record.Recalculate();
    }

    public static JsonObject Recalculate(this JsonObject record)
    {
        decimal total = 0;
        if (record["lines"] is JsonArray lines)
        {
            foreach (var l in lines.OfType<JsonObject>())
            {
                if (Number(l["quantity"]) is { } qty && Number(l["unitPrice"]) is { } price) total += qty * price;
            }
        }
        record["totalAmount"] = Math.Round(total, 2);
        return record;
    }

    public static decimal? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;

    /// <summary>Path examples: "currency", "lines[0].quantity". A null value stores JSON null.</summary>
    public static JsonObject Set(this JsonObject record, string path, JsonNode? value)
    {
        var (parent, name, index) = Resolve(record, path);
        if (index is { } i) ((JsonArray)parent)[i] = value;
        else ((JsonObject)parent)[name!] = value;
        return record;
    }

    /// <summary>Removes the property, so it is missing rather than null.</summary>
    public static JsonObject Drop(this JsonObject record, string path)
    {
        var (parent, name, index) = Resolve(record, path);
        if (index is { } i) ((JsonArray)parent).RemoveAt(i);
        else ((JsonObject)parent).Remove(name!);
        return record;
    }

    public static JsonObject Clone(this JsonObject record) => record.DeepClone().AsObject();

    private static (JsonNode Parent, string? Name, int? Index) Resolve(JsonObject root, string path)
    {
        var parts = path.Replace("]", "").Split('.');
        JsonNode current = root;
        for (var p = 0; p < parts.Length; p++)
        {
            var last = p == parts.Length - 1;
            var segment = parts[p];
            var bracket = segment.IndexOf('[');
            if (bracket >= 0)
            {
                var name = segment[..bracket];
                var index = int.Parse(segment[(bracket + 1)..], CultureInfo.InvariantCulture);
                var array = (JsonArray)current[name]!;
                if (last) return (array, null, index);
                current = array[index]!;
            }
            else
            {
                if (last) return (current, segment, null);
                current = current[segment]!;
            }
        }
        throw new InvalidOperationException($"Bad path {path}");
    }
}
