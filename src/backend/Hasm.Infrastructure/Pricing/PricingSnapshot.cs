using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hasm.Infrastructure.Pricing;

/// <summary>
/// The immutable snapshot of a priced line (SRS 8.4): it stores VALUES - the policy, the request, the selected price and discount with their opaque rule references and trace, the optional override and
/// the result - never live references, so a later change of a price list or rule cannot re-price an issued document. <see cref="ContentHash"/> is a SHA-256 over the canonical (scale-normalized,
/// culture-independent) JSON of everything else: TAMPER-EVIDENCE that Verify also cross-checks by recalculation, not authentication (that is the document store's and the audit chain's job).
/// </summary>
public sealed record PricingSnapshot(string Version, PricingPolicy Policy, PricingRequest Request, ResolvedPricing Resolved, PriceOverride? Override, PricingResult Result, string ContentHash);

public static class PricingSnapshots
{
    public const string CurrentVersion = "pricing-snapshot-v1";
    /// <summary>The form of a snapshot whose policy has a NON-default precedence (the precedences are part of the sealed content). Snapshots of default-precedence policies stay v1 with their historical hash.</summary>
    public const string PrecedenceVersion = "pricing-snapshot-v2";
    /// <summary>The policy steps a resolution under this policy records (none for the default precedences). A snapshot whose trace disagrees was resolved under another policy and is refused.</summary>
    private static string[] ExpectedPolicySteps(PricingPolicy p) =>
    [
        .. p.ListPrecedence != PriceListPrecedence.ScopeThenPriority ? new[] { "list-precedence:priority-then-scope" } : [],
        .. p.DiscountPrecedence != DiscountPrecedence.SpecificityThenPriority ? new[] { "discount-precedence:priority-then-specificity" } : [],
    ];
    private static bool PolicyStepsMatch(PricingPolicy p, ResolvedPricing r) =>
        r.Trace.Where(s => s.Kind == "policy").Select(s => s.RuleRef).SequenceEqual(ExpectedPolicySteps(p)) && r.Trace.Where(s => s.Kind == "policy").All(s => s.Outcome == "applied");
    private static string VersionFor(PricingPolicy p) => p.HasDefaultPrecedence ? CurrentVersion : PrecedenceVersion;

    private sealed class CanonicalDecimal : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => decimal.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue((value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture));
    }
    private static readonly JsonSerializerOptions Canonical = new() { Converters = { new CanonicalDecimal() } };

    // The v1 hash is computed over EXPLICIT field lists, not over the live record types: adding a property to a record later can never change (and so never invalidate) a stored v1 snapshot.
    // A new field needs a new snapshot version. A golden-hash test pins this.
    private static object Trace(IReadOnlyList<TraceStep> t) => t.Select(x => new { x.Kind, x.RuleRef, x.Outcome }).ToArray();
    private static string Hash(string version, PricingPolicy policy, PricingRequest request, ResolvedPricing resolved, PriceOverride? over, PricingResult result) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = version,
            Policy = version == CurrentVersion
                ? (object)new { policy.PriceDecimals, policy.MoneyDecimals, policy.MinMarginPercent, Action = (int)policy.Action }
                : new { policy.PriceDecimals, policy.MoneyDecimals, policy.MinMarginPercent, Action = (int)policy.Action, ListPrecedence = (int)policy.ListPrecedence, DiscountPrecedence = (int)policy.DiscountPrecedence },
            Request = new { request.Date, request.Currency, request.ItemId, request.ItemGroupId, request.UnitId, request.Quantity, request.CustomerId, request.CustomerGroupId, request.UnitCost },
            Resolved = new { resolved.ListUnitPrice, resolved.PriceRuleRef, resolved.DiscountPercent, resolved.DiscountRuleRef, Trace = Trace(resolved.Trace) },
            Override = over is null ? null : new { over.UnitPrice, over.DiscountPercent, over.Reason },
            Result = new { result.ListUnitPrice, result.PriceRuleRef, result.DiscountPercent, result.DiscountRuleRef, result.NetUnitPrice, result.LineNet, result.Overridden, result.OverrideReason, result.MarginPercent, Margin = (int)result.Margin, result.RequiresApproval, Trace = Trace(result.Trace) },
        }, Canonical))).ToLowerInvariant();

    /// <summary>Calculates and seals, keeping read-only copies of the trace.</summary>
    public static PricingSnapshot Create(PricingPolicy policy, PricingRequest request, ResolvedPricing resolved, PriceOverride? over = null)
    {
        if (!PolicyStepsMatch(policy, resolved)) throw new PricingException("policy_mismatch");   // Resolve must have been called with the same policy
        var result = PricingEngine.Calculate(policy, request, resolved, over);
        var copy = resolved with { Trace = resolved.Trace.ToArray().AsReadOnly() };
        var version = VersionFor(policy);
        return new(version, policy, request, copy, over, result, Hash(version, policy, request, copy, over, result));
    }

    /// <summary>True only when the version is known, the stored hash matches the stored content AND recalculating from the stored values reproduces the stored result exactly.</summary>
    public static bool Verify(PricingSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Policy is null || snapshot.Version != VersionFor(snapshot.Policy) || snapshot.Request is null || snapshot.Resolved?.Trace is null || snapshot.Result?.Trace is null) return false;
        try
        {
            if (!PolicyStepsMatch(snapshot.Policy, snapshot.Resolved)) return false;
            if (!string.Equals(snapshot.ContentHash, Hash(snapshot.Version, snapshot.Policy, snapshot.Request, snapshot.Resolved, snapshot.Override, snapshot.Result), StringComparison.Ordinal)) return false;
            var recomputed = PricingEngine.Calculate(snapshot.Policy, snapshot.Request, snapshot.Resolved, snapshot.Override);
            return string.Equals(Hash(snapshot.Version, snapshot.Policy, snapshot.Request, snapshot.Resolved, snapshot.Override, recomputed), snapshot.ContentHash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is PricingException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException) { return false; }   // a malformed snapshot is invalid evidence, never a crash
    }
}
