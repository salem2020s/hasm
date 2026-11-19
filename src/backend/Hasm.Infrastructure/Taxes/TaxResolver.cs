namespace Hasm.Infrastructure.Taxes;

/// <summary>A tax of the company (code is stable; an inactive tax cannot be applied by any rule).</summary>
public sealed record TaxDefinitionRecord(Guid Id, string Code, bool IsActive);

/// <summary>
/// One DATED version of a tax's treatment and rate, effective from <paramref name="EffectiveFrom"/> until the next version. Versions are never edited: a change is a new
/// later version (the persistence slice enforces append-only), so a document dated in the past always resolves to the rate that was in force.
/// </summary>
public sealed record TaxRateRecord(Guid TaxId, DateOnly EffectiveFrom, TaxTreatment Treatment, decimal RatePercent);

/// <summary>
/// A rule: which tax applies to which operation of which document type, optionally narrowed to an item classification and/or a party classification (null = any),
/// with its application priority and the earlier taxes whose amount joins its base. Valid from/to are dates (inclusive). <paramref name="TreatmentOverride"/> is how a
/// classification yields an exemption, zero rating or "not applicable" (the SRS "exception"): when set, the line is treated that way at rate 0 instead of the dated rate
/// (only ZeroRated, Exempt or NotApplicable are meaningful overrides).
/// </summary>
public sealed record TaxRuleRecord(Guid Id, string DocumentType, string Operation, Guid? ItemClassId, Guid? PartyClassId, Guid TaxId, int Priority,
    IReadOnlyList<Guid>? IncludesTaxIds, DateOnly ValidFrom, DateOnly? ValidTo, bool IsActive, TaxTreatment? TreatmentOverride = null);

/// <summary>What a single line is being taxed as. <see cref="TaxDate"/> is the date the document's tax point falls on (the caller decides it; the resolver never reads a clock).</summary>
public sealed record TaxContext(string DocumentType, string Operation, Guid? ItemClassId, Guid? PartyClassId, DateOnly TaxDate);

/// <summary>
/// Pure, deterministic rule resolution (SRS 6.1/6.2): turns a <see cref="TaxContext"/> and the company's definitions, dated rates and rules into the ordered
/// <see cref="TaxComponent"/>s the engine takes. No database or clock; every ambiguity is a refusal, never a guess:
/// <list type="bullet">
/// <item>Candidate rules are active, dated around the tax date, and match document type and operation; item/party constraints match only an equal classification (null matches any).</item>
/// <item>For each tax the MOST SPECIFIC candidate wins (two constrained dimensions beat one, one beats none). Two candidates of the same specificity for the same tax are
/// <c>ambiguous_tax_rule</c> (item-only against party-only included): the company must fix its rules.</item>
/// <item>The treatment and rate come from the latest dated version at or before the tax date; none is <c>no_tax_rate</c>. A rule's treatment override (exemption, zero rating, not applicable) replaces them.</item>
/// <item>A base reference may only name an EARLIER tax (lower priority); a referenced tax that does not apply to this line is simply not part of its base.</item>
/// </list>
/// </summary>
public static class TaxResolver
{
    private static TaxException Refuse(string code) => new(code);
    private static int Specificity(TaxRuleRecord rule) => (rule.ItemClassId is null ? 0 : 1) + (rule.PartyClassId is null ? 0 : 1);

    public static IReadOnlyList<TaxComponent> Resolve(TaxContext context, IReadOnlyCollection<TaxDefinitionRecord> definitions, IReadOnlyCollection<TaxRateRecord> rates,
        IReadOnlyCollection<TaxRuleRecord> rules)
    {
        if (context is null || definitions is null || rates is null || rules is null || string.IsNullOrWhiteSpace(context.DocumentType) || string.IsNullOrWhiteSpace(context.Operation))
            throw Refuse("invalid_tax_context");
        if (definitions.GroupBy(d => d.Id).Any(g => g.Count() > 1)) throw Refuse("invalid_tax_data");
        var date = context.TaxDate;
        var candidates = rules.Where(r => r.IsActive && r.ValidFrom <= date && (r.ValidTo is null || date <= r.ValidTo)
            && string.Equals(r.DocumentType, context.DocumentType, StringComparison.Ordinal) && string.Equals(r.Operation, context.Operation, StringComparison.Ordinal)
            && (r.ItemClassId is null || r.ItemClassId == context.ItemClassId) && (r.PartyClassId is null || r.PartyClassId == context.PartyClassId)).ToArray();

        var chosen = new List<(TaxRuleRecord Rule, TaxDefinitionRecord Tax, TaxRateRecord Rate)>();
        foreach (var group in candidates.GroupBy(r => r.TaxId).OrderBy(g => g.Key))
        {
            var best = group.Max(Specificity);
            var top = group.Where(r => Specificity(r) == best).ToArray();
            if (top.Length != 1) throw Refuse("ambiguous_tax_rule");
            var rule = top[0];
            var tax = definitions.SingleOrDefault(d => d.Id == rule.TaxId) ?? throw Refuse("unknown_tax");
            if (!tax.IsActive) throw Refuse("tax_inactive");
            if (rule.TreatmentOverride is { } over && (over == TaxTreatment.Standard || !Enum.IsDefined(over))) throw Refuse("invalid_tax_data");
            var rate = rates.Where(r => r.TaxId == rule.TaxId && r.EffectiveFrom <= date).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault() ?? throw Refuse("no_tax_rate");
            if (rates.Count(r => r.TaxId == rule.TaxId && r.EffectiveFrom == rate.EffectiveFrom) != 1) throw Refuse("invalid_tax_data");   // two versions from the same date
            chosen.Add((rule, tax, rate));
        }
        if (chosen.GroupBy(c => c.Rule.Priority).Any(g => g.Count() > 1)) throw Refuse("duplicate_priority");
        var ordered = chosen.OrderBy(c => c.Rule.Priority).ToArray();
        var components = new List<TaxComponent>(ordered.Length);
        foreach (var (rule, tax, rate) in ordered)
        {
            var references = (rule.IncludesTaxIds ?? []).Distinct().ToArray();
            foreach (var id in references)
            {
                var referenced = ordered.FirstOrDefault(c => c.Tax.Id == id);
                if (id == rule.TaxId || (referenced.Rule is not null && referenced.Rule.Priority >= rule.Priority)) throw Refuse("invalid_base_reference");
            }
            var includes = references.Where(id => ordered.Any(c => c.Tax.Id == id)).ToArray();   // an inapplicable referenced tax is not part of THIS line's base
            var overridden = rule.TreatmentOverride;                                                  // an override replaces BOTH treatment and rate (non-standard means rate 0)
            components.Add(new TaxComponent(tax.Id, tax.Code, overridden ?? rate.Treatment, overridden is null ? rate.RatePercent : 0m, rule.Priority, includes,
                $"rule:{rule.Id:N};rate:{tax.Id:N}@{rate.EffectiveFrom:yyyy-MM-dd}"));
        }
        return components;
    }
}
