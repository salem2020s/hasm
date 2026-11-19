using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hasm.Infrastructure.Costing;

/// <summary>One proposed compensation line. Dated at the affected ORIGINAL operation and booked to the ORIGINAL accounts/branch/period (D-044, TM-D8(a)).</summary>
internal sealed record CompensationLine(Guid PostingId, DateOnly Date, Guid? PeriodId, Guid BranchId, Guid AccountId, decimal Debit, decimal Credit);
internal sealed record CompensationPlan(ImmutableArray<CompensationLine> Lines, ImmutableArray<Guid> AffectedPostingIds, string Fingerprint);

/// <summary>
/// Pure internal calculation of the compensation of a back-dated ordinary event (matrix cells M1 and M2 of
/// <c>PHASE-3.8D-TRANSITION-MATRIX-DECISIONS.md</c>, working baseline: the conservative recommendations). No database, API, persistence, approval or
/// execution authority and no journal is created: the result is a PROPOSAL input.
/// <list type="bullet">
/// <item>Only re-costed ISSUES produce lines: Dr cost-of-goods account / Cr inventory account by the delta (mirrored for a negative delta), taken from the
/// original posted journal, dated at the original operation, in the original period and branch. Receipts keep their entered amounts. Transfers book no
/// journal (the inventory account is identical on both legs); their effect arrives through the downstream issues' deltas.</item>
/// <item>Everything else fails closed: obligations and settlements (a replay that would create, resize or remove one), reversals (not yet replayable),
/// journals that are not Posted (an unposted effect would be double-booked), journals with more than one debit or credit account (item-group provenance is
/// not provable, TM-D6), operations without evidence.</item>
/// </list>
/// Final Closed / Soft Closed periods are NOT decided here: the caller must check every <see cref="CompensationLine.Date"/> against the fiscal evidence.
/// </summary>
internal static class CompensationCalculator
{
    private const short Posted = 4;
    private static CostingException Refuse(string code) => new(code);

    internal static CompensationPlan Calculate(Guid company, string comparisonFingerprint, ImmutableArray<CostFlowEntryImpact> changes, ClassificationEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(comparisonFingerprint) || evidence is null || changes.IsDefault) throw Refuse("invalid_compensation_input");
        if (!evidence.Obligations.IsEmpty || !evidence.Settlements.IsEmpty) throw Refuse("compensation_obligation_unsupported");   // TM-D13
        if (evidence.Postings.Any(p => p.ReversalId is not null)) throw Refuse("compensation_reversal_unsupported");

        var deltaByOperation = new Dictionary<Guid, decimal>();
        foreach (var change in changes)
            deltaByOperation[change.OperationId] = deltaByOperation.GetValueOrDefault(change.OperationId) + (change.ProposedAmount - change.OriginalAmount);

        var lines = ImmutableArray.CreateBuilder<CompensationLine>();
        var affected = new List<Guid>();
        foreach (var (operation, delta) in deltaByOperation.OrderBy(x => x.Key))
        {
            if (delta == 0m) continue;   // a trajectory change whose own cost did not change needs no ledger line
            var posting = evidence.Postings.FirstOrDefault(p => p.CostOperationId == operation) ?? throw Refuse("classification_unavailable");
            switch (posting.DocType)
            {
                case 1: throw Refuse("compensation_inconsistent");               // a receipt's entered amount cannot be re-costed
                case 3:
                    if (posting.JournalId is not null) throw Refuse("compensation_inconsistent");   // transfers book no journal
                    continue;                                                     // TM-D7: no line, downstream issues carry the effect
                case 2: break;
                default: throw Refuse("classification_unavailable");
            }
            var journal = posting.JournalId is { } id ? evidence.Journals.FirstOrDefault(j => j.Id == id) : null;
            if (journal is null) throw Refuse("compensation_inconsistent");
            if (journal.Status != Posted) throw Refuse("unposted_effect");        // never compensate against a draft
            var debits = journal.Lines.Where(l => l.BaseDebit > 0m).ToArray();
            var credits = journal.Lines.Where(l => l.BaseCredit > 0m).ToArray();
            if (debits.Length != 1 || credits.Length != 1) throw Refuse("classification_ambiguous");   // TM-D6: not provable per item
            var (k, i) = (debits[0], credits[0]);
            if (k.BaseDebit != posting.TotalValue || i.BaseCredit != posting.TotalValue) throw Refuse("compensation_inconsistent");
            if (k.Allocations.Length != 0 || i.Allocations.Length != 0) throw Refuse("classification_unexpected_line");
            var amount = Math.Abs(delta);
            // Positive delta: the goods cost more than booked, K rises and I falls. Negative delta mirrors.
            var (debit, credit) = delta > 0m ? (k, i) : (i, k);
            lines.Add(new(posting.Id, posting.Date, journal.PeriodId, debit.BranchId, debit.AccountId, amount, 0m));
            lines.Add(new(posting.Id, posting.Date, journal.PeriodId, credit.BranchId, credit.AccountId, 0m, amount));
            affected.Add(posting.Id);
        }
        var ordered = lines.OrderBy(l => l.PostingId).ThenByDescending(l => l.Debit).ToImmutableArray();
        if (ordered.Sum(l => l.Debit) != ordered.Sum(l => l.Credit)) throw Refuse("compensation_inconsistent");
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { Contract = "stock-compensation-plan-v1", Company = company, Comparison = comparisonFingerprint, Evidence = evidence.EvidenceFingerprint, Lines = ordered },
            ClassificationEvidenceService.Canonical))).ToLowerInvariant();
        return new(ordered, [.. affected.Order()], fingerprint);
    }
}
