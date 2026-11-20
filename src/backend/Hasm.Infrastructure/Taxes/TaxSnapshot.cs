using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hasm.Infrastructure.Taxes;

/// <summary>
/// The immutable snapshot of a tax calculation (SRS 6.3): it stores VALUES - the policy, the resolved components with their opaque rule/rate references, the inputs and the
/// results - never live references, so a later change of a rule or rate cannot alter an existing snapshot. <see cref="ContentHash"/> is a SHA-256 over the canonical
/// (scale-normalized, culture-independent) JSON of everything else. It is TAMPER-EVIDENCE (an unkeyed digest that Verify also cross-checks by recalculation), not authentication:
/// the authenticity of a stored snapshot is the job of the document store and its audit chain.
/// </summary>
public sealed record TaxSnapshot(string Version, TaxPolicy Policy, IReadOnlyList<TaxLineInput> Lines, TaxDocumentResult Result, string ContentHash);

public static class TaxSnapshots
{
    public const string CurrentVersion = "tax-snapshot-v1";

    // decimal is written without its scale so 1.0 and 1.00 are the same evidence
    private sealed class CanonicalDecimal : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue((value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture));
    }
    private static readonly JsonSerializerOptions Canonical = new() { Converters = { new CanonicalDecimal() } };

    private static string Hash(string version, TaxPolicy policy, IReadOnlyList<TaxLineInput> lines, TaxDocumentResult result) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Version = version, Policy = policy, Lines = lines, Result = result }, Canonical))).ToLowerInvariant();

    /// <summary>Calculates and seals. The snapshot holds exactly what produced the result, in defensive read-only copies (a later change to the caller's arrays cannot alter it); a missing base list is normalized to empty.</summary>
    public static TaxSnapshot Create(TaxPolicy policy, IReadOnlyList<TaxLineInput> lines, CancellationToken ct = default)
    {
        var result = TaxEngine.Calculate(policy, lines, ct);
        var copy = lines.Select(l => l with { Taxes = l.Taxes.Select(t => t with { IncludesInBase = (t.IncludesInBase ?? []).ToArray().AsReadOnly() }).ToArray().AsReadOnly() }).ToArray().AsReadOnly();
        return new(CurrentVersion, policy, copy, result, Hash(CurrentVersion, policy, copy, result));
    }

    /// <summary>True only when the version is known, the stored hash matches the stored content AND recalculating the engine from the stored inputs reproduces the stored results exactly.</summary>
    public static bool Verify(TaxSnapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot is null || snapshot.Version != CurrentVersion || snapshot.Policy is null || snapshot.Lines is null || snapshot.Result is null) return false;
        if (!string.Equals(snapshot.ContentHash, Hash(snapshot.Version, snapshot.Policy, snapshot.Lines, snapshot.Result), StringComparison.Ordinal)) return false;
        try
        {
            var recomputed = TaxEngine.Calculate(snapshot.Policy, snapshot.Lines, ct);
            return string.Equals(Hash(snapshot.Version, snapshot.Policy, snapshot.Lines, recomputed), snapshot.ContentHash, StringComparison.Ordinal);
        }
        catch (TaxException) { return false; }
    }
}
