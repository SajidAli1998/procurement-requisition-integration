using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Procurement.Contracts;

namespace Procurement.Sync.Validation;

public sealed record ValidationProblem(QuarantineReason Reason, string Field, string Message);

/// <summary>Identity fields read leniently from any record, valid or not.</summary>
public sealed record SourceEnvelope(string Key, bool HasId, long Version, string? Status, string Raw);

/// <summary>
/// Reads a raw source record into the canonical model, collecting every schema problem with its field path
/// instead of failing on the first. Tolerant reader: unknown extra fields are ignored.
/// </summary>
public static class SourceRecordParser
{
    public static SourceEnvelope ReadEnvelope(JsonElement record)
    {
        var raw = record.GetRawText();
        var id = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idProp.GetString())
            ? idProp.GetString()!
            : null;
        var version = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("version", out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var parsed) ? parsed : 0;
        var status = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("status", out var s)
            && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

        // Records without an id still need a stable key so they can be quarantined and reported.
        var key = id ?? "NOID-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
        return new SourceEnvelope(key, id is not null, version, status, raw);
    }

    public static (Requisition? Requisition, List<ValidationProblem> Problems) Parse(JsonElement record, SourceEnvelope envelope)
    {
        var problems = new List<ValidationProblem>();
        if (record.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new(QuarantineReason.SchemaInvalid, "$", "Record is not a JSON object"));
            return (null, problems);
        }

        var reader = new FieldReader(problems);
        if (!envelope.HasId) problems.Add(new(QuarantineReason.MissingRequiredField, "id", "id is missing"));
        var requester = reader.RequiredString(record, "requester");
        var costCenter = reader.RequiredString(record, "costCenter");
        var companyCode = reader.RequiredString(record, "companyCode");
        var currency = reader.RequiredString(record, "currency");
        var neededBy = reader.RequiredDate(record, "neededBy");
        var justification = reader.OptionalString(record, "justification");
        var approvedAt = reader.OptionalTimestamp(record, "approvedAt");
        var total = reader.RequiredDecimal(record, "totalAmount");

        var lines = new List<RequisitionLine>();
        if (!record.TryGetProperty("lines", out var linesProp) || linesProp.ValueKind == JsonValueKind.Null)
        {
            problems.Add(new(QuarantineReason.MissingRequiredField, "lines", "lines is missing"));
        }
        else if (linesProp.ValueKind != JsonValueKind.Array)
        {
            problems.Add(new(QuarantineReason.SchemaInvalid, "lines", $"lines must be an array, got {linesProp.ValueKind}"));
        }
        else
        {
            var index = 0;
            foreach (var line in linesProp.EnumerateArray())
            {
                var path = $"lines[{index++}]";
                if (line.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(new(QuarantineReason.SchemaInvalid, path, "line must be an object"));
                    continue;
                }
                var lineReader = new FieldReader(problems, path + ".");
                var lineNo = lineReader.RequiredInt(line, "lineNo");
                var material = lineReader.OptionalString(line, "materialCode");
                var description = lineReader.RequiredString(line, "description");
                var group = lineReader.OptionalString(line, "materialGroup");
                var quantity = lineReader.RequiredDecimal(line, "quantity");
                var uom = lineReader.RequiredString(line, "uom");
                var price = lineReader.RequiredDecimal(line, "unitPrice");
                var plant = lineReader.RequiredString(line, "plant");
                if (lineReader.Ok)
                {
                    lines.Add(new RequisitionLine(lineNo!.Value, material, description!, group, quantity!.Value, uom!, price!.Value, plant!));
                }
            }
        }

        if (problems.Count > 0) return (null, problems);

        return (new Requisition(envelope.Key, envelope.Version, requester!, costCenter!, companyCode!, currency!,
            neededBy!.Value, justification, approvedAt, total!.Value, lines), problems);
    }

    private sealed class FieldReader(List<ValidationProblem> problems, string prefix = "")
    {
        private readonly int _start = problems.Count;

        public bool Ok => problems.Count == _start;

        private bool TryGet(JsonElement obj, string name, bool required, out JsonElement value)
        {
            if (obj.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null) return true;
            if (required) problems.Add(new(QuarantineReason.MissingRequiredField, prefix + name, $"{prefix}{name} is missing"));
            return false;
        }

        private void WrongType(string name, string expected, JsonElement value) =>
            problems.Add(new(QuarantineReason.SchemaInvalid, prefix + name,
                $"{prefix}{name} must be {expected}, got {value.ValueKind} {Truncate(value.GetRawText())}"));

        public string? RequiredString(JsonElement obj, string name) => ReadString(obj, name, required: true);

        public string? OptionalString(JsonElement obj, string name) => ReadString(obj, name, required: false);

        private string? ReadString(JsonElement obj, string name, bool required)
        {
            if (!TryGet(obj, name, required, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) { WrongType(name, "a string", v); return null; }
            var s = v.GetString();
            if (required && string.IsNullOrWhiteSpace(s))
            {
                problems.Add(new(QuarantineReason.MissingRequiredField, prefix + name, $"{prefix}{name} is empty"));
                return null;
            }
            return s;
        }

        public decimal? RequiredDecimal(JsonElement obj, string name)
        {
            if (!TryGet(obj, name, true, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            WrongType(name, "a number", v);
            return null;
        }

        public int? RequiredInt(JsonElement obj, string name)
        {
            if (!TryGet(obj, name, true, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            WrongType(name, "an integer", v);
            return null;
        }

        public DateOnly? RequiredDate(JsonElement obj, string name)
        {
            var s = RequiredString(obj, name);
            if (s is null) return null;
            if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
            problems.Add(new(QuarantineReason.SchemaInvalid, prefix + name, $"{prefix}{name} '{s}' is not a valid yyyy-MM-dd date"));
            return null;
        }

        public DateTimeOffset? OptionalTimestamp(JsonElement obj, string name)
        {
            var s = OptionalString(obj, name);
            if (s is null) return null;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)) return ts;
            problems.Add(new(QuarantineReason.SchemaInvalid, prefix + name, $"{prefix}{name} '{s}' is not a valid timestamp"));
            return null;
        }

        private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";
    }
}
