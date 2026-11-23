using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Security;
using Npgsql;

namespace Hasm.Infrastructure.Identity;

/// <summary>One line of the identity audit report: a user row and what the authenticated audit chain says about it.</summary>
public sealed record IdentityReportEntry(Guid UserId, string Login, string Disposition, string Tag, string Detail)
{
    /// <summary>What the audit chain can say about the password hash: nothing (no event carries it). Every user needs an explicit recorded disposition before tagging (owner decision OD-3; the tagging command of S-2c part 2 refuses without it).</summary>
    public const string PasswordHashSupport = "NOT_SUPPORTED_BY_EVENTS_DISPOSITION_REQUIRED";

    public bool Eligible => Disposition is IdentityAuditReport.Matched or IdentityAuditReport.MatchedAfterRestore;
}

/// <summary>The report: the verified chain it was reconciled against, one entry per user, a hash over its canonical text (the identity of the review) and, for refusals, why.</summary>
public sealed record IdentityAuditReportResult(string? Refusal, long ChainEvents, long ChainLastSeq, IReadOnlyList<IdentityReportEntry> Entries, string Hash)
{
    public bool Refused => Refusal is not null;
    public int Count(string disposition) => Entries.Count(e => e.Disposition == disposition);
}

/// <summary>
/// The READ-ONLY reconciliation of the user rows against the NET state of the authenticated system audit stream (identity integrity S-2c, plan section 7, review M3 of PR 220, owner decision OD-3). It first verifies the
/// whole chain (a stream that does not verify refuses the report), then replays the ordered events per user (created as administrator or not, lifecycle state changes, MFA state changes) to the state they produce, and
/// compares each live row with it: a row with no creation event (an orphan), a row whose administrator flag, lifecycle state or MFA state differs from what the events produced. Only matched rows are eligible for tagging.
/// The password hash is not carried by any event (it can only be reviewed by a person). The report prints codes, counts and ids only, never a secret, and writes nothing. Its hash is SHA-256 over its canonical text, so the
/// operator's review has an identity that the tagging command (slice S-2c part 2) will have to be bound to.
/// </summary>
public static class IdentityAuditReport
{
    public const string Matched = "MATCHED", MatchedAfterRestore = "MATCHED_AFTER_RESTORE", OrphanNoCreationEvent = "ORPHAN_NO_CREATION_EVENT", MismatchAdministrator = "MISMATCH_ADMINISTRATOR",
        MismatchState = "MISMATCH_STATE", MismatchMfa = "MISMATCH_MFA", MismatchCode = "MISMATCH_ENROLMENT_CODE";

    private sealed class Net { public bool Created; public bool Administrator; public short State; public short Mfa; public bool Unparsed; public int Issues; public long LastIssueSeq; public long LastActivationSeq; }

    public static Task<IdentityAuditReportResult> BuildAsync(string readConnectionString, AuditVerifier verifier, IdentityKeys? keys, CancellationToken ct = default) => BuildAsync(readConnectionString, verifier, keys, null, ct);

    /// <param name="afterVerification">TEST SEAM: runs between the verification and the replay, so a test can append an event the verification never saw.</param>
    internal static async Task<IdentityAuditReportResult> BuildAsync(string readConnectionString, AuditVerifier verifier, IdentityKeys? keys, Func<Task>? afterVerification, CancellationToken ct = default)
    {
        var verification = await verifier.VerifyAsync(AuditStream.System, ct);
        if (afterVerification is not null) await afterVerification();
        if (verification.Verdict != AuditVerdict.Verified)
            return new IdentityAuditReportResult($"the system audit stream does not verify ({verification.Verdict}); the report is refused: run audit-verify and resolve it first", verification.Events, verification.LastSeq ?? 0, [], "");

        var net = new Dictionary<Guid, Net>();
        var restoreSeen = false;
        var map = AuditTableMap.For(AuditStream.System);
        await using var conn = new NpgsqlConnection(readConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await ro.ExecuteNonQueryAsync(ct);
        long lastSeq = 0, events = 0;
        await using (var cmd = new NpgsqlCommand($"SELECT {map.SelectColumns} FROM {map.Table} WHERE {map.StreamKey} = @s AND {map.Seq} <= @max ORDER BY {map.Seq}", conn, tx))   // only what the verification covered (the rule of the backup plan S8d): an event appended afterwards is never replayed
        {
            cmd.Parameters.AddWithValue("s", AuditStream.System.Key);
            cmd.Parameters.AddWithValue("max", verification.VerifiedThroughSeq);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var e = AuditStorage.Read(rd);
                lastSeq = e.Seq; events++;
                if (e.EventType == MfaAuditEvents.RestoreApplied) { restoreSeen = true; continue; }
                if (e.TargetUserId is not { } user) continue;
                switch (e.EventType)
                {
                    case AuditEventTypes.UserCreated:
                        net[user] = new Net { Created = true, Administrator = e.Detail == "administrator: yes", Unparsed = e.Detail is not ("administrator: yes" or "administrator: no") };
                        break;
                    case AuditEventTypes.InitialAdministratorCreated:
                        net[user] = new Net { Created = true, Administrator = true };
                        break;
                    case MfaAuditEvents.StateChanged when net.TryGetValue(user, out var iss) && e.Detail == "one-time enrolment code issued":
                        iss.Issues++; iss.LastIssueSeq = e.Seq;
                        break;
                    case MfaAuditEvents.SecretActivated when net.TryGetValue(user, out var act):
                        act.LastActivationSeq = e.Seq;
                        break;
                    case AuditEventTypes.UserStateChanged when net.TryGetValue(user, out var n):
                        if (e.Detail is { } d && d.StartsWith("state: ", StringComparison.Ordinal) && Enum.TryParse<UserState>(d["state: ".Length..], out var st)) n.State = (short)st; else n.Unparsed = true;
                        break;
                    case MfaAuditEvents.StateChanged when net.TryGetValue(user, out var m) && e.Detail is { } md && md.StartsWith("mfa state ", StringComparison.Ordinal):
                        // "mfa state <from> -> <to> (<cause>)"
                        var arrow = md.IndexOf("-> ", StringComparison.Ordinal);
                        var rest = arrow < 0 ? "" : md[(arrow + 3)..];
                        var name = rest.Split(' ', 2)[0];
                        if (Enum.TryParse<MfaState>(name, out var ms)) m.Mfa = (short)ms; else m.Unparsed = true;
                        break;
                }
            }
        }

        var entries = new List<IdentityReportEntry>();
        var digests = new Dictionary<Guid, string>();
        var ids = new List<Guid>();
        await using (var sel = new NpgsqlCommand($"SELECT {UserRow.Columns("u")} FROM {SystemDatabase.SystemSchema.User.Table} u ORDER BY u.{SystemDatabase.SystemSchema.User.Id}", conn, tx))
        await using (var rd = await sel.ExecuteReaderAsync(ct))
        {
            var rows = new List<UserRow>();
            while (await rd.ReadAsync(ct)) rows.Add(UserRow.Read(rd));
            foreach (var row in rows)
            {
                digests[row.Id] = Convert.ToHexStringLower(row.ReportDigest());                                    // everything the user tag covers, in the hash (not printed)
                var tag = keys is null ? "unknown" : row.Tag is null ? "untagged" : row.IsValid(keys) ? "valid" : "invalid";
                if (!net.TryGetValue(row.Id, out var n) || !n.Created) { entries.Add(new(row.Id, row.Login, OrphanNoCreationEvent, tag, $"administrator={row.IsAdministrator}")); continue; }
                string disposition = Matched; string detail = $"administrator={row.IsAdministrator} state={row.State} mfa={row.MfaState}";
                if (n.Administrator != row.IsAdministrator) { disposition = MismatchAdministrator; detail += $" events say administrator={n.Administrator}"; }
                else if (n.Unparsed) { disposition = MismatchState; detail += " an audit event of this user could not be interpreted"; }
                else if (n.State != row.State) { disposition = MismatchState; detail += $" events say state={n.State}"; }
                else if (!CodeReconciles(row, n, restoreSeen, out var codeProblem)) { disposition = MismatchCode; detail += " " + codeProblem; }
                else if (n.Mfa != row.MfaState)
                {
                    // a system restore marks users RecoveryRequired in bulk and names no user: that single difference is explained by a restore event, and still listed
                    if (restoreSeen && row.MfaState == (short)MfaState.RecoveryRequired && n.Mfa == (short)MfaState.Enrolled) { disposition = MatchedAfterRestore; detail += " (a restore event explains RecoveryRequired)"; }
                    else { disposition = MismatchMfa; detail += $" events say mfa={n.Mfa}"; }
                }
                entries.Add(new(row.Id, row.Login, disposition, tag, detail));
            }
        }

        var text = new StringBuilder();
        text.Append("hasm identity audit report v1\n").Append($"chain events {events} last seq {lastSeq}\n");
        foreach (var x in entries) text.Append($"{x.UserId:N}|{x.Login}|{x.Disposition}|{x.Tag}|{x.Detail}|{IdentityReportEntry.PasswordHashSupport}|{digests[x.UserId]}\n");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        return new IdentityAuditReportResult(null, events, lastSeq, entries, hash);
    }

    /// <summary>The one-time enrolment-code columns against their issue and activation events (review M3 of PR 239): a code that no issue event supports, a code marked consumed with no activation after its issue, or a code cleared without a restore, is not supported.</summary>
    private static bool CodeReconciles(UserRow row, Net n, bool restoreSeen, out string problem)
    {
        problem = "";
        if (row.CodeHash is not null && n.Issues == 0) problem = "an enrolment code is present but no audit event issued one";
        else if (row.CodeConsumedAt is not null && n.LastActivationSeq <= n.LastIssueSeq) problem = "the enrolment code is marked spent but no activation follows its issue";
        else if (row.CodeHash is null && n.Issues > 0 && !restoreSeen) problem = "an issued enrolment code was cleared without a restore event";
        return problem.Length == 0;
    }

    public static string Render(IdentityAuditReportResult r)
    {
        var sb = new StringBuilder();
        if (r.Refused) return $"identity-audit-report REFUSED: {r.Refusal}\n";
        sb.Append($"Identity audit report (users). Chain verified: {r.ChainEvents} events, last sequence {r.ChainLastSeq}.\n");
        foreach (var group in r.Entries.GroupBy(e => e.Disposition).OrderBy(g => g.Key)) sb.Append($"  {group.Key}: {group.Count()}\n");
        foreach (var e in r.Entries.Where(e => !e.Eligible)) sb.Append($"  ! {e.Disposition} user {e.UserId:N} login '{e.Login}' tag {e.Tag}: {e.Detail}\n");
        foreach (var e in r.Entries.Where(e => e.Eligible && e.Detail.StartsWith("administrator=True", StringComparison.Ordinal))) sb.Append($"  administrator {e.UserId:N} login '{e.Login}' tag {e.Tag}\n");
        sb.Append($"Password hashes: {r.Entries.Count} user(s), {IdentityReportEntry.PasswordHashSupport}: no audit event carries a password hash, so every user needs an explicit recorded disposition before tagging (owner decision OD-3); for an administrator the default disposition is a mandatory password reset and second-factor re-enrolment.\n");
        sb.Append($"Report hash: {r.Hash}\n");
        return sb.ToString();
    }
}
