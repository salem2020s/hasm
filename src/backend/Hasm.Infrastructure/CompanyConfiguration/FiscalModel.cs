using Npgsql;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>Fiscal period states. The numeric values are persisted and constrained by PostgreSQL; never renumber.</summary>
public enum FiscalPeriodState : short { Open = 1, SoftClosed = 2, FinalClosed = 3 }

/// <summary>How a fiscal year is divided. Persisted; never renumber.</summary>
public enum FiscalPeriodType : short { Monthly = 1, Quarterly = 2 }

/// <summary>Raw client input for a new fiscal year: a name, the first day (always the first of a month) and the division of the year.</summary>
public sealed record FiscalYearInput(string? Name, DateOnly? StartDate, string? PeriodType);

/// <summary>The reason recorded for an exceptional reopening (at least 10 characters).</summary>
public sealed record ReasonInput(string? Reason);

public sealed record FiscalPeriodView(
    Guid Id, Guid YearId, int Number, DateOnly StartDate, DateOnly EndDate, FiscalPeriodState State,
    DateTimeOffset? StatusChangedAtUtc, long Revision);

/// <summary>A fiscal year with its periods. <see cref="IsClosed"/> is the year-level closing (all periods Final Closed); a year is exactly 12 months.</summary>
public sealed record FiscalYearView(
    Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, FiscalPeriodType PeriodType, string TimeZone, bool IsClosed,
    DateTimeOffset? ClosedAtUtc, long Revision, DateTimeOffset CreatedAtUtc, IReadOnlyList<FiscalPeriodView> Periods);

/// <summary>
/// A module that can hold unresolved material differences or pending adjustments that must block closing a period (SRS §5 and §9: pending
/// accounting postings, inventory/cost differences, unapproved adjustment documents). Those modules do not exist yet, so today only
/// placeholders are registered: they never block and never claim to be implemented, and the API reports <c>ChecksImplemented = false</c> so
/// "closable" is never presented as "verified". A real module replaces its placeholder and runs on the SAME connection and transaction as the close.
/// </summary>
public interface IPeriodCloseProbe
{
    string Domain { get; }
    bool Implemented { get; }
    /// <summary>True for a domain whose owning module is not part of this release (nothing can exist to check): reported as "not applicable", NEVER as passed, and it does not make closing refuse.</summary>
    bool ModuleAbsent => false;
    Task<bool> HasBlockersAsync(Guid companyId, FiscalPeriodView period, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct);
}

public sealed class UnimplementedPeriodCloseProbe(string domain) : IPeriodCloseProbe
{
    public string Domain => domain;
    public bool Implemented => false;
    public Task<bool> HasBlockersAsync(Guid companyId, FiscalPeriodView period, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Evaluates every REQUIRED domain; a registered real probe replaces the placeholder for its domain.</summary>
public sealed class PeriodCloseGuard
{
    public static readonly string[] RequiredDomains = ["pending_accounting_postings", "inventory_valuation_differences", "pending_adjustments", "year_end_closing"];

    private readonly Dictionary<string, IPeriodCloseProbe> probes;

    public PeriodCloseGuard(IEnumerable<IPeriodCloseProbe>? registered = null)
    {
        probes = RequiredDomains.ToDictionary(d => d, d => (IPeriodCloseProbe)new UnimplementedPeriodCloseProbe(d));
        foreach (var p in registered ?? []) probes[p.Domain] = p;
    }

    public IReadOnlyList<string> UnimplementedDomains => [.. probes.Values.Where(p => !p.Implemented && !p.ModuleAbsent).Select(p => p.Domain).Order(StringComparer.Ordinal)];

    /// <summary>Domains whose module does not exist in this release; listed honestly by the API and the year-end closing, never presented as passed.</summary>
    public IReadOnlyList<string> NotApplicableDomains => [.. probes.Values.Where(p => !p.Implemented && p.ModuleAbsent).Select(p => p.Domain).Order(StringComparer.Ordinal)];

    public async Task<bool> IsBlockedAsync(Guid companyId, FiscalPeriodView period, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        foreach (var probe in probes.Values)
            if (await probe.HasBlockersAsync(companyId, period, connection, transaction, ct)) return true;
        return false;
    }
}

/// <summary>Why a posting date cannot be used, or the period it falls in. Backend-enforced; the frontend never decides this.</summary>
public sealed record PostingPeriodCheck(bool Allowed, string? Reason, Guid? PeriodId, FiscalPeriodState? State)
{
    public const string NoPeriod = "no_fiscal_period";
    public const string FinalClosed = "fiscal_period_final_closed";
    public const string SoftClosed = "fiscal_period_soft_closed";
}

/// <summary>
/// The single backend decision point the future General Ledger and posting engine must call INSIDE their posting transaction, on their own
/// connection, before writing anything dated into the books. It locks the period row FOR SHARE: a concurrent close/reopen (which updates the
/// row) waits for in-flight postings to commit, and any posting that starts afterwards sees the new state — so a closing period can never accept a
/// posting it was not supposed to. Final Closed never accepts postings; Soft Closed only for a caller the posting engine has authorized for it
/// (<paramref name="allowSoftClosed"/>); a date outside every period is refused. Automatic jobs do not get a bypass.
/// </summary>
public static class FiscalPeriodGate
{
    public static async Task<PostingPeriodCheck> CheckAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateOnly postingDate, bool allowSoftClosed, CancellationToken ct = default)
    {

        await using var cmd = new NpgsqlCommand(
            $"SELECT {CompanyDatabase.CompanySchema.FiscalPeriod.Id}, {CompanyDatabase.CompanySchema.FiscalPeriod.Status} FROM {CompanyDatabase.CompanySchema.FiscalPeriod.Table} WHERE @d BETWEEN {CompanyDatabase.CompanySchema.FiscalPeriod.StartDate} AND {CompanyDatabase.CompanySchema.FiscalPeriod.EndDate} FOR SHARE", connection, transaction);
        cmd.Parameters.AddWithValue("d", postingDate);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return Evaluate(null, null, allowSoftClosed);
        var state = (FiscalPeriodState)r.GetInt16(1);
        return Evaluate(r.GetGuid(0), state, allowSoftClosed);
    }

    internal static PostingPeriodCheck Evaluate(Guid? periodId, FiscalPeriodState? state, bool allowSoftClosed)
    {
        if (periodId is null || state is null) return new(false, PostingPeriodCheck.NoPeriod, null, null);
        return state switch
        {
            FiscalPeriodState.FinalClosed => new(false, PostingPeriodCheck.FinalClosed, periodId, state),
            FiscalPeriodState.SoftClosed when !allowSoftClosed => new(false, PostingPeriodCheck.SoftClosed, periodId, state),
            _ => new(true, null, periodId, state),
        };
    }
}

public static partial class ConfigurationValidation
{
    public static bool TryNormalizeFiscalYear(FiscalYearInput? input, out string name, out DateOnly start, out FiscalPeriodType type, out List<FieldError> errors)
    {
        errors = [];
        name = ""; start = default; type = FiscalPeriodType.Monthly;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var n = input.Name?.Trim();
        if (string.IsNullOrEmpty(n)) errors.Add(new("name", "required"));
        else if (n.Length > 100) errors.Add(new("name", "too_long"));
        else name = n;
        if (input.StartDate is not { } s) errors.Add(new("startDate", "required"));
        else if (s.Day != 1) errors.Add(new("startDate", "must_be_first_of_month"));
        else if (s.Year < 1900 || s.Year > 2200) errors.Add(new("startDate", "out_of_range"));
        else start = s;
        switch (input.PeriodType?.Trim().ToLowerInvariant())
        {
            case "monthly": type = FiscalPeriodType.Monthly; break;
            case "quarterly": type = FiscalPeriodType.Quarterly; break;
            case null or "": errors.Add(new("periodType", "required")); break;
            default: errors.Add(new("periodType", "invalid_value")); break;
        }
        return errors.Count == 0;
    }

    public static bool TryNormalizeReason(ReasonInput? input, out string reason, out List<FieldError> errors)
    {
        errors = [];
        reason = input?.Reason?.Trim() ?? "";
        if (reason.Length == 0) errors.Add(new("reason", "required"));
        else if (reason.Length < 10) errors.Add(new("reason", "too_short"));
        else if (reason.Length > 500) errors.Add(new("reason", "too_long"));
        return errors.Count == 0;
    }
}

public enum FiscalReopenTarget { Period = 1, Year = 2 }

/// <summary>Persisted; never renumber. Pending is the only non-final state.</summary>
public enum FiscalReopenStatus : short { Pending = 1, Approved = 2, Denied = 3, Cancelled = 4 }

/// <summary>
/// A request for the exceptional reopening of a Final Closed period or a closed year. It is bound to the exact object and the revision the requester saw;
/// approval re-validates both. Only a DIFFERENT authorized user can approve it (never the requester), and approval executes the reopening.
/// </summary>
public sealed record FiscalReopenRequestView(
    Guid Id, FiscalReopenTarget Target, Guid TargetId, string YearName, int? PeriodNumber, DateOnly? StartDate, DateOnly? EndDate,
    long ExpectedRevision, string Reason, FiscalReopenStatus Status, Guid RequestedBy, DateTimeOffset RequestedAtUtc,
    Guid? DecidedBy, DateTimeOffset? DecidedAtUtc, string? DecisionNote, long Revision);
