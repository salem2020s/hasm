namespace Hasm.Infrastructure.Taxes;

/// <summary>A classification a localization proposes to ADD (never to change or remove an existing one).</summary>
public sealed record TaxClassProposal(TaxClassKind Kind, string Code, string NameAr, string? NameEn);

/// <summary>A rule template a localization proposes to ADD. It names taxes and classes by CODE; applying it goes through the normal audited, validated rule service.</summary>
public sealed record TaxRuleProposal(string DocumentType, string Operation, string TaxCode, string? ItemClassCode, string? PartyClassCode, int Priority, TaxTreatment? TreatmentOverride = null);

/// <summary>What a localization contributes. Additions only.</summary>
public sealed record TaxLocalizationContribution(IReadOnlyList<TaxClassProposal> Classes, IReadOnlyList<TaxRuleProposal> Rules)
{
    public static TaxLocalizationContribution Empty { get; } = new([], []);
}

/// <summary>A read-only view of what is about to be calculated, given to a localization for validation.</summary>
public sealed record TaxCalculationContext(string DocumentType, string Operation, DateOnly TaxDate, Guid? ItemId, Guid? PartyId, TaxResolution Resolution);

/// <summary>A validation problem raised by a localization. The code is stable and namespaced by the localization id when reported.</summary>
public sealed record TaxLocalizationProblem(string Code, string? Field = null);

/// <summary>
/// The extension point for national tax localization (SRS 6.4: no country-specific compliance is built into HASM core). A localization is registered once per (id, version) and may ONLY:
/// (1) propose ADDITIONAL classifications and rule templates (they still pass the normal validated and audited configuration path, so a localization can never bypass an invariant), and
/// (2) VALIDATE a prepared calculation, returning problems that refuse it. It receives an immutable view and returns no components, rates, amounts or rounding choices: the arithmetic
/// of the engine and its snapshot are unreachable from here by construction, and <see cref="TaxLocalizationHost"/> verifies that nothing changed. No country module exists in this repository.
/// </summary>
public interface ITaxLocalization
{
    /// <summary>Stable lowercase identifier, for example a future "xx-vat". 2-40 characters of a-z, 0-9, dot, dash.</summary>
    string Id { get; }
    /// <summary>Positive and increased for every behavioural change: a document snapshot may cite the (id, version) that validated it.</summary>
    int Version { get; }
    TaxLocalizationContribution Contribute();
    IReadOnlyList<TaxLocalizationProblem> Validate(TaxCalculationContext context);
}

/// <summary>Registry and safe host of the registered localizations. Deterministic: ordered by id then version; refuses duplicates and malformed registrations.</summary>
public sealed class TaxLocalizationHost
{
    private readonly IReadOnlyList<ITaxLocalization> _items;

    public TaxLocalizationHost(IEnumerable<ITaxLocalization>? localizations = null)
    {
        var list = (localizations ?? []).ToList();
        foreach (var l in list)
        {
            if (l.Id is null || !System.Text.RegularExpressions.Regex.IsMatch(l.Id, "^[a-z0-9][a-z0-9.-]{1,39}$")) throw new ArgumentException($"invalid localization id '{l.Id}'");
            if (l.Version < 1) throw new ArgumentException($"localization '{l.Id}' needs a positive version");
        }
        var duplicate = list.GroupBy(l => l.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"localization '{duplicate.Key}' is registered more than once (one version per id is active)");
        _items = [.. list.OrderBy(l => l.Id, StringComparer.Ordinal)];
    }

    /// <summary>The registered localizations as (id, version), in a stable order.</summary>
    public IReadOnlyList<(string Id, int Version)> Registered => [.. _items.Select(l => (l.Id, l.Version))];

    /// <summary>
    /// The additions proposed by all localizations. A proposal is dropped and reported when it is malformed or collides with an existing code or with another proposal; it never
    /// replaces anything. Throwing localizations are isolated: their id is reported and nothing from them is used.
    /// </summary>
    public (IReadOnlyList<TaxClassProposal> Classes, IReadOnlyList<TaxRuleProposal> Rules, IReadOnlyList<string> Rejected) Additions(IReadOnlySet<string> existingItemClassCodes, IReadOnlySet<string> existingPartyClassCodes)
    {
        var classes = new List<TaxClassProposal>(); var rules = new List<TaxRuleProposal>(); var rejected = new List<string>();
        var item = new HashSet<string>(existingItemClassCodes, StringComparer.OrdinalIgnoreCase); var party = new HashSet<string>(existingPartyClassCodes, StringComparer.OrdinalIgnoreCase);
        foreach (var l in _items)
        {
            TaxLocalizationContribution c;
            try { c = l.Contribute(); }
            catch (Exception) { rejected.Add($"{l.Id}:contribution_failed"); continue; }
            foreach (var k in c.Classes)
            {
                var set = k.Kind == TaxClassKind.Item ? item : k.Kind == TaxClassKind.Party ? party : null;
                if (set is null || string.IsNullOrWhiteSpace(k.Code) || string.IsNullOrWhiteSpace(k.NameAr)) { rejected.Add($"{l.Id}:class_invalid"); continue; }
                if (!set.Add(k.Code.Trim())) { rejected.Add($"{l.Id}:class_exists:{k.Code}"); continue; }
                classes.Add(k with { Code = k.Code.Trim() });
            }
            foreach (var r in c.Rules)
            {
                if (string.IsNullOrWhiteSpace(r.DocumentType) || string.IsNullOrWhiteSpace(r.Operation) || string.IsNullOrWhiteSpace(r.TaxCode) || r.Priority < 1) { rejected.Add($"{l.Id}:rule_invalid"); continue; }
                rules.Add(r);
            }
        }
        return (classes, rules, rejected);
    }

    /// <summary>
    /// Runs every localization's validation over an immutable copy of the context. Returns the problems, each prefixed with the localization id (<c>id:code</c>). A localization that throws
    /// refuses the calculation (fail closed: <c>id:validation_failed</c>). The resolution passed in is compared before and after: any mutation is refused as <c>id:mutated_input</c>.
    /// </summary>
    public IReadOnlyList<string> Validate(TaxCalculationContext context)
    {
        var problems = new List<string>();
        foreach (var l in _items)
        {
            var before = Fingerprint(context.Resolution);
            IReadOnlyList<TaxLocalizationProblem> result;
            try { result = l.Validate(context with { Resolution = context.Resolution with { Components = new List<TaxComponent>(context.Resolution.Components) } }); }
            catch (Exception) { problems.Add($"{l.Id}:validation_failed"); continue; }
            if (Fingerprint(context.Resolution) != before) { problems.Add($"{l.Id}:mutated_input"); continue; }
            foreach (var p in result ?? []) problems.Add($"{l.Id}:{p.Code}");
        }
        return problems;
    }

    private static string Fingerprint(TaxResolution r) =>
        $"{r.PolicyVersion}|{r.Policy}|" + string.Join(";", r.Components.Select(c => $"{c.TaxId}/{c.Code}/{(int)c.Treatment}/{c.RatePercent}/{c.Priority}/{string.Join(",", c.IncludesInBase ?? [])}/{c.RuleRef}"));
}
