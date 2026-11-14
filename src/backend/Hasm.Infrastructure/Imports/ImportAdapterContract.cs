using System.Text.Json.Nodes;
using Npgsql;

namespace Hasm.Infrastructure.Imports;

public enum ImportFieldType { Text = 1, Code = 2, Integer = 3, Decimal = 4, Date = 5, Boolean = 6, Enum = 7 }

/// <summary>
/// One target field of an adapter. The generic framework normalizes values by TYPE only (trim, Unicode NFC, Arabic-Indic digits, decimal and thousands separators from the mapping options, exact date
/// formats, boolean and enum aliases, maximum length, required); everything that depends on the meaning of a field belongs to the adapter. <see cref="IsKey"/> marks the fields whose normalized values
/// identify a row inside the file (duplicate detection); <see cref="EnumValues"/> maps each canonical value to the aliases (any language) accepted in a file.
/// </summary>
public sealed record ImportField(string Key, string NameAr, string NameEn, ImportFieldType Type, bool Required = false, int MaxLength = 200, bool IsKey = false, bool UpperCase = false,
    IReadOnlyDictionary<string, string[]>? EnumValues = null, string[]? Aliases = null);

/// <summary>A problem with one field of one row (<c>Field</c> may be empty for a row-level problem). <c>Code</c> is a stable machine-readable reason.</summary>
public sealed record RowIssue(string Field, string Code, string? Detail = null);

/// <summary>Outcome of the analysis of one row. The numeric values are persisted as the row status.</summary>
public enum RowOutcome : short
{
    /// <summary>Will be created / applied (carries an operation).</summary>
    Valid = 2,
    /// <summary>A blocking validation error: nothing can be applied until the file is corrected.</summary>
    Invalid = 3,
    /// <summary>A resolvable conflict awaiting an explicit decision (one of <see cref="RowAnalysis.AllowedResolutions"/>); blocks application until resolved.</summary>
    Conflict = 4,
    /// <summary>A conflict with a recorded, applicable resolution (the plan reflects it).</summary>
    Resolved = 5,
    /// <summary>The row matches an existing record exactly: nothing to do.</summary>
    Exact = 7,
}

/// <summary>A row after mapping and normalization. <c>Values</c> holds canonical text per field key (null = empty). <c>Resolution</c> is the explicit decision recorded for this row, if any.</summary>
public sealed class NormalizedRow(int rowNo, Dictionary<string, string?> values, string? resolution)
{
    public int RowNo { get; } = rowNo;
    public Dictionary<string, string?> Values { get; } = values;
    public string? Resolution { get; } = resolution;
    public string? Get(string key) => Values.GetValueOrDefault(key);
}

/// <summary>What an adapter says about one row. <c>Operation</c> is adapter-defined JSON describing exactly what application would do (it is part of the hashed plan); <c>Sequence</c> orders operations.</summary>
public sealed record RowAnalysis(int RowNo, RowOutcome Outcome, IReadOnlyList<RowIssue>? Issues = null, IReadOnlyList<string>? AllowedResolutions = null, JsonObject? Operation = null, string? Explanation = null, int Sequence = 0);

/// <summary>A reconciliation figure (for example "accounts to create: 12"); part of the hashed plan.</summary>
public sealed record ReconLine(string Key, string Value);

/// <summary>
/// The result of analysing a whole batch. <c>Baseline</c> is a deterministic fingerprint of the business data the analysis depended on (for example the sorted existing codes): when it changes the plan is
/// stale. Rows not mentioned are treated as invalid by the framework (an adapter must answer for every row it received).
/// </summary>
public sealed record AnalysisResult(IReadOnlyList<RowAnalysis> Rows, string Baseline, IReadOnlyList<ReconLine>? Reconciliation = null);

/// <summary>The consistent, READ-ONLY view of the company database an analysis runs in (one repeatable-read snapshot). An adapter must not write in it.</summary>
public sealed record ImportAnalysisContext(Guid CompanyId, NpgsqlConnection Connection, NpgsqlTransaction Transaction);
