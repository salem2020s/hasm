using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hasm.Infrastructure.Costing;

/// <summary>The seven application fingerprints a plan carries (design 3.1). The closure hash is the eighth member and is computed by PostgreSQL only.</summary>
internal sealed record CompensationPlanFingerprints(string Plan, string Comparison, string Evidence, string Periods, string Position, string Policy, string Accounting)
{
    internal string[] All => [Plan, Comparison, Evidence, Periods, Position, Policy, Accounting];
}

/// <summary>
/// The pure binding of a read-only planner result to the stored plan (Phase 3.8D step 5, slices 1 and 2): the seven fingerprints, the closure and its byte-exact hash (design 3.1, the same form PostgreSQL
/// computes), and the canonical payload of the proposed event. Nothing here reads or writes a database; the storage and approval services compare what it computes with what PostgreSQL stored.
/// </summary>
internal static class CompensationPlanBinding
{
    private static readonly System.Text.RegularExpressions.Regex Hex64 = new("^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string Digest(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ClassificationEvidenceService.Canonical))).ToLowerInvariant();

    /// <summary>A planner fingerprint is lowercase hex already (the column requires it); anything else (for example the "no-postings" marker of an empty evidence) is bound through a digest of its name and value.</summary>
    private static string Norm(string name, string value) => Hex64.IsMatch(value) ? value : Digest(new { Contract = "stock-compensation-plan-fingerprint-v1", Name = name, Value = value });

    internal static CompensationPlanFingerprints Fingerprint(Guid company, Guid item, CompensationPlanResult r) => new(
        Plan: Norm("plan", r.PlanFingerprint),
        Comparison: Norm("comparison", r.Comparison.ComparisonFingerprint),
        Evidence: Norm("evidence", r.Evidence.EvidenceFingerprint),
        Periods: Digest(new { Contract = "stock-compensation-plan-periods-v1", Company = company, r.Periods }),
        Position: Norm("position", r.Comparison.HistoryFingerprint),
        Policy: Digest(new { Contract = "stock-compensation-plan-policy-v1", Company = company, Item = item, r.Method, r.CurrencyCode, r.MoneyDecimals }),
        Accounting: Digest(new
        {
            Contract = "stock-compensation-plan-accounting-v1", Company = company,
            Accounts = r.Evidence.Accounts.OrderBy(a => a.Id).ToArray(),
            Versions = r.Evidence.Postings.Where(p => p.VersionId is not null).Select(p => p.VersionId!.Value).Distinct().Order().ToArray(),
        }));

    /// <summary>True when what the planner computes now is exactly what the plan stored: all seven fingerprints AND the closure hash. A plan for which this is false is stale.</summary>
    internal static bool Matches(StoredCompensationPlan plan, CompensationPlanFingerprints fresh, string freshClosureHash) =>
        fresh == plan.Fingerprints && string.Equals(freshClosureHash, plan.ClosureHash, StringComparison.Ordinal);

    /// <summary>The closure of a plan: the planned item on every position of the planner's snapshot (the roots and every position reached through transfers), sorted.</summary>
    internal static ImmutableArray<(Guid Item, Guid Warehouse)> Closure(Guid item, CompensationPlanResult r) => [.. r.PositionWarehouses.Distinct().Order().Select(w => (item, w))];

    /// <summary>Design 3.1, byte for byte: lowercase hyphenated item uuid, a colon, lowercase hyphenated warehouse uuid; sorted in uuid order (the ordinal order of the lowercase hyphenated text); one newline between pairs;
    /// UTF-8; lowercase hex SHA-256. An empty closure hashes the empty string.</summary>
    internal static string ClosureHash(IEnumerable<(Guid Item, Guid Warehouse)> pairs)
    {
        var lines = pairs.Select(x => (I: x.Item.ToString("D").ToLowerInvariant(), W: x.Warehouse.ToString("D").ToLowerInvariant()))
            .OrderBy(x => x.I, StringComparer.Ordinal).ThenBy(x => x.W, StringComparer.Ordinal).Select(x => x.I + ":" + x.W);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
    }

    // ---------------------------------------------------------------------------------------------------------------- the canonical payload of the proposed event
    private static string Scale(decimal v) => (v / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
    private sealed record PayloadLine(Guid Warehouse, string Quantity, string? ReceiptAmount, Guid? Destination);
    private sealed record Payload(string Contract, Guid Id, string Date, short Kind, PayloadLine[] Lines);

    internal static string PayloadOf(ProposedFlowOperation p) => JsonSerializer.Serialize(new Payload("stock-compensation-plan-payload-v1", p.Id, p.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (short)p.Kind,
        [.. p.Lines.Select(l => new PayloadLine(l.WarehouseId, Scale(l.Quantity), l.ReceiptAmount is { } a ? Scale(a) : null, l.DestinationWarehouseId))]));

    /// <summary>The proposed event stored in a plan; null when the stored text is not a payload of this contract (the approval then refuses).</summary>
    internal static ProposedFlowOperation? Proposal(string payload)
    {
        try
        {
            var p = JsonSerializer.Deserialize<Payload>(payload);
            if (p is null || p.Contract != "stock-compensation-plan-payload-v1" || p.Lines is null) return null;
            return new ProposedFlowOperation(p.Id, DateOnly.ParseExact(p.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), (CostOperationKind)p.Kind,
                [.. p.Lines.Select(l => new ProposedFlowLine(l.Warehouse, decimal.Parse(l.Quantity, CultureInfo.InvariantCulture),
                    l.ReceiptAmount is null ? null : decimal.Parse(l.ReceiptAmount, CultureInfo.InvariantCulture), l.Destination))]);
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { return null; }
    }
}
