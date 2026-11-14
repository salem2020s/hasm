using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Imports;

/// <summary>How numbers and dates are written in the file. Never inferred from the machine's locale: the same file always normalizes the same way.</summary>
public sealed record MappingOptions(string DecimalSeparator = ".", string ThousandsSeparator = ",", string DateFormat = "yyyy-MM-dd")
{
    public static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy/MM/dd", "dd-MM-yyyy", "dd.MM.yyyy"];
    public static readonly string[] Decimals = [".", ","];
    public static readonly string[] Thousands = ["", ",", ".", " ", "'"];
}

/// <summary>Where a field comes from: a source column (0-based index into the staged cells) and/or a constant default used when the cell is empty.</summary>
public sealed record FieldMap(int? Column, string? Default);

/// <summary>A validated mapping of adapter fields to file columns. Canonical JSON round trip (stored on the batch and part of the hashed plan).</summary>
public sealed record ImportMapping(IReadOnlyDictionary<string, FieldMap> Fields, MappingOptions Options)
{
    public JsonObject ToJson()
    {
        var fields = new JsonObject();
        foreach (var (key, map) in Fields.OrderBy(k => k.Key, StringComparer.Ordinal))
            fields[key] = new JsonObject { ["column"] = map.Column, ["default"] = map.Default };
        return new JsonObject { ["fields"] = fields, ["options"] = new JsonObject { ["decimalSeparator"] = Options.DecimalSeparator, ["thousandsSeparator"] = Options.ThousandsSeparator, ["dateFormat"] = Options.DateFormat } };
    }

    /// <summary>Validates a client mapping against the adapter fields and the staged header count. Returns the field errors (empty = valid).</summary>
    public static (ImportMapping? Mapping, List<FieldError> Errors) Parse(JsonNode? json, IReadOnlyList<ImportField> fields, int columnCount)
    {
        var errors = new List<FieldError>();
        if (json is not JsonObject root) return (null, [new("mapping", "required")]);
        var map = new Dictionary<string, FieldMap>(StringComparer.Ordinal);
        var known = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var used = new Dictionary<int, string>();
        if (root["fields"] is not JsonObject fieldsNode) errors.Add(new("mapping.fields", "required"));
        else
            foreach (var (key, node) in fieldsNode)
            {
                if (!known.ContainsKey(key)) { errors.Add(new($"mapping.fields.{key}", "unknown_field")); continue; }
                int? column = null; string? def = null;
                if (node is JsonObject o)
                {
                    if (o["column"] is { } c && c is JsonValue cv)
                    {
                        if (!cv.TryGetValue<int>(out var ci) || ci < 0 || ci >= columnCount) { errors.Add(new($"mapping.fields.{key}.column", "invalid_column")); continue; }
                        column = ci;
                    }
                    if (o["default"] is JsonValue dv && dv.TryGetValue<string>(out var ds) && !string.IsNullOrWhiteSpace(ds)) def = ds.Trim();
                }
                else if (node is not null) { errors.Add(new($"mapping.fields.{key}", "invalid")); continue; }
                if (column is { } col && used.TryGetValue(col, out var other)) { errors.Add(new($"mapping.fields.{key}.column", "duplicate_column")); continue; }
                if (column is { } c2) used[c2] = key;
                map[key] = new FieldMap(column, def);
            }
        foreach (var f in fields)
            if (f.Required && (!map.TryGetValue(f.Key, out var m) || (m.Column is null && m.Default is null))) errors.Add(new($"mapping.fields.{f.Key}", "required_field_unmapped"));
        var decimalSep = "."; var thousands = ","; var dateFormat = "yyyy-MM-dd";
        if (root["options"] is JsonObject opt)
        {
            string? Str(string name) => opt[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (Str("decimalSeparator") is { } d) { if (!MappingOptions.Decimals.Contains(d)) errors.Add(new("mapping.options.decimalSeparator", "invalid")); else decimalSep = d; }
            if (Str("thousandsSeparator") is { } t) { if (!MappingOptions.Thousands.Contains(t)) errors.Add(new("mapping.options.thousandsSeparator", "invalid")); else thousands = t; }
            else if (decimalSep == ",") thousands = ".";          // an unspecified thousands separator follows the decimal one (never the same character)
            if (Str("dateFormat") is { } df) { if (!MappingOptions.DateFormats.Contains(df)) errors.Add(new("mapping.options.dateFormat", "invalid")); else dateFormat = df; }
        }
        if (thousands == decimalSep && thousands.Length > 0) errors.Add(new("mapping.options.thousandsSeparator", "same_as_decimal"));
        return errors.Count > 0 ? (null, errors) : (new ImportMapping(map, new MappingOptions(decimalSep, thousands, dateFormat)), errors);
    }

    /// <summary>A suggestion from the headers: a header that equals (after normalization) a field key, a name in either language or an alias maps to that field; each column and field is used once.</summary>
    public static JsonObject Suggest(IReadOnlyList<string> headers, IReadOnlyList<ImportField> fields)
    {
        static string Norm(string s) => Regex.Replace(ImportNormalizer.Digits(s.Normalize(NormalizationForm.FormC)).Trim().ToLowerInvariant(), @"[\s_\-./]+", " ");
        var usedColumns = new HashSet<int>(); var result = new JsonObject();
        foreach (var f in fields)
        {
            var names = new List<string> { f.Key, f.NameAr, f.NameEn };
            if (f.Aliases is not null) names.AddRange(f.Aliases);
            var wanted = names.Select(Norm).ToHashSet();
            for (var i = 0; i < headers.Count; i++)
                if (!usedColumns.Contains(i) && wanted.Contains(Norm(headers[i]))) { usedColumns.Add(i); result[f.Key] = new JsonObject { ["column"] = i, ["default"] = null }; break; }
        }
        return new JsonObject { ["fields"] = result, ["options"] = new JsonObject { ["decimalSeparator"] = ".", ["thousandsSeparator"] = ",", ["dateFormat"] = "yyyy-MM-dd" } };
    }
}

/// <summary>Type-driven normalization of a single cell. Deterministic and culture-independent.</summary>
public static class ImportNormalizer
{
    private static readonly string[] TrueWords = ["true", "yes", "y", "1", "نعم", "صح", "صحيح"];
    private static readonly string[] FalseWords = ["false", "no", "n", "0", "لا", "خطأ", "خطا"];

    /// <summary>Arabic-Indic (٠-٩) and Persian (۰-۹) digits to ASCII; the Arabic decimal and thousands separators to . and ,</summary>
    public static string Digits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch switch { >= '٠' and <= '٩' => (char)('0' + (ch - '٠')), >= '۰' and <= '۹' => (char)('0' + (ch - '۰')), '٫' => '.', '٬' => ',', _ => ch });
        return sb.ToString();
    }

    private static string CanonicalDecimal(decimal d) => (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    private static string? Number(string raw, MappingOptions o, bool integer, out string? error)
    {
        error = null;
        var s = Digits(raw).Trim();
        // spaces and non-breaking spaces are always accepted as thousands separators (workbook number formats use them)
        s = s.Replace(" ", "").Replace(" ", "").Replace(" ", "");
        var dec = Regex.Escape(o.DecimalSeparator);
        if (o.ThousandsSeparator.Length > 0 && o.ThousandsSeparator != " " && s.Contains(o.ThousandsSeparator))
        {
            // a configured thousands separator must group correctly (1,250 / 12,345,678): "1,25" is ambiguous and refused, never guessed
            var sep = Regex.Escape(o.ThousandsSeparator);
            if (!Regex.IsMatch(s, $@"^[+-]?\d{{1,3}}(?:{sep}\d{{3}})+(?:{dec}\d+)?$")) { error = integer ? "invalid_integer" : "invalid_decimal"; return null; }
            s = s.Replace(o.ThousandsSeparator, "");
        }
        if (o.DecimalSeparator != ".")
        {
            if (s.Contains('.')) { error = integer ? "invalid_integer" : "invalid_decimal"; return null; }      // a stray point when the decimal separator is a comma
            s = s.Replace(o.DecimalSeparator, ".");
        }
        if (integer)
        {
            if (!Regex.IsMatch(s, @"^[+-]?\d{1,18}$") || !long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) { error = "invalid_integer"; return null; }
            return l.ToString(CultureInfo.InvariantCulture);
        }
        if (!Regex.IsMatch(s, @"^[+-]?\d{1,20}(\.\d{1,12})?$") || !decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)) { error = "invalid_decimal"; return null; }
        return CanonicalDecimal(d);
    }

    /// <summary>Normalizes one cell for a field. An empty cell is null (and an issue when the field is required).</summary>
    public static (string? Value, RowIssue? Issue) Normalize(ImportField field, string? raw, MappingOptions options)
    {
        var text = raw?.Normalize(NormalizationForm.FormC).Trim();
        if (string.IsNullOrEmpty(text)) return (null, field.Required ? new RowIssue(field.Key, "required") : null);
        switch (field.Type)
        {
            case ImportFieldType.Text:
            case ImportFieldType.Code:
                if (field.Type == ImportFieldType.Code) { text = Digits(text); if (field.UpperCase) text = text.ToUpperInvariant(); }
                if (field.MaxLength > 0 && text.Length > field.MaxLength) return (null, new RowIssue(field.Key, "too_long", field.MaxLength.ToString(CultureInfo.InvariantCulture)));
                return (text, null);
            case ImportFieldType.Integer or ImportFieldType.Decimal:
                var n = Number(text, options, field.Type == ImportFieldType.Integer, out var err);
                return n is null ? (null, new RowIssue(field.Key, err!)) : (n, null);
            case ImportFieldType.Date:
                var d = Digits(text);
                if (Regex.IsMatch(d, @"^\d{4}-\d{2}-\d{2}T00:00:00$")) d = d[..10];                       // a date-only cell of a workbook
                var formats = new[] { options.DateFormat, "yyyy-MM-dd" };
                return DateTime.TryParseExact(d, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? (dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null) : (null, new RowIssue(field.Key, "invalid_date"));
            case ImportFieldType.Boolean:
                var b = text.ToLowerInvariant();
                return TrueWords.Contains(b) ? ("true", null) : FalseWords.Contains(b) ? ("false", null) : (null, new RowIssue(field.Key, "invalid_boolean"));
            case ImportFieldType.Enum:
                var wanted = Digits(text).ToLowerInvariant();
                foreach (var (canonical, aliases) in field.EnumValues ?? new Dictionary<string, string[]>())
                    if (canonical.ToLowerInvariant() == wanted || aliases.Any(a => a.Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant() == wanted)) return (canonical, null);
                return (null, new RowIssue(field.Key, "invalid_value"));
            default: return (null, new RowIssue(field.Key, "invalid"));
        }
    }

    /// <summary>Applies a mapping to one staged row: column cells (or defaults) normalized per field type.</summary>
    public static (Dictionary<string, string?> Values, List<RowIssue> Issues) NormalizeRow(IReadOnlyList<ImportField> fields, ImportMapping mapping, IReadOnlyList<string?> cells)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal); var issues = new List<RowIssue>();
        foreach (var f in fields)
        {
            mapping.Fields.TryGetValue(f.Key, out var map);
            var raw = map?.Column is { } c && c < cells.Count ? cells[c] : null;
            if (string.IsNullOrWhiteSpace(raw)) raw = map?.Default;
            var (v, issue) = Normalize(f, raw, mapping.Options);
            values[f.Key] = v;
            if (issue is not null) issues.Add(issue);
        }
        return (values, issues);
    }

    /// <summary>Compact JSON with every object's properties in ordinal order: the same value always hashes the same.</summary>
    public static string CanonicalJson(JsonNode? node)
    {
        var sb = new StringBuilder();
        void Write(JsonNode? n)
        {
            switch (n)
            {
                case null: sb.Append("null"); break;
                case JsonObject o:
                    sb.Append('{'); var first = true;
                    foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal)) { if (!first) sb.Append(','); first = false; sb.Append(System.Text.Json.JsonSerializer.Serialize(k)).Append(':'); Write(v); }
                    sb.Append('}'); break;
                case JsonArray a:
                    sb.Append('['); for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(a[i]); }
                    sb.Append(']'); break;
                default: sb.Append(n.ToJsonString()); break;
            }
        }
        Write(node);
        return sb.ToString();
    }
}
