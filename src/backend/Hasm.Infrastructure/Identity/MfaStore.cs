using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;
using TO = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTotp;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using SM = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionMfa;
using UT = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTag;

namespace Hasm.Infrastructure.Identity;

/// <summary>System-stream audit event codes of the MFA storage layer (Phase 4.1 S1). Persisted codes: never renumbered, never shared.</summary>
public static class MfaAuditEvents
{
    public const short StateChanged = 1330, SecretCreated = 1331, SecretActivated = 1332, SecretsRevoked = 1333, SecretsRewrapped = 1334, RestoreApplied = 1335;
    /// <summary>Phase 4.1 S3a (D-163): a step-up succeeded / was refused for a wrong or replayed code (bounded by the shared MFA throttle). Detail never carries a code.</summary>
    public const short StepUpSucceeded = 1336, StepUpFailed = 1337;
    /// <summary>Phase 4.1 S3b-1b (D-167): a company's MFA policy was changed (detail: the kinds and the role count; never a secret).</summary>
    public const short PolicyChanged = 1338;
    /// <summary>Identity integrity S-1 (PROVISIONAL code, allocated by the lead): a session row failed its integrity check (tag or security revision) and was revoked. Detail: the session id and the cause (tag or revision); never a key or a tag.</summary>
    public const short IdentityRecordRejected = 1339;
    /// <summary>Identity integrity S-2a (PROVISIONAL code, to be allocated by the lead): the one-time owner tagging of untagged users ran (detail: the counts and the key id).</summary>
    public const short IdentityRecordsTagged = 1340;
}

public sealed record RewrapResult(int Rewrapped, int Remaining);
public sealed record RestoreMfaResult(int UsersMarkedRecoveryRequired, int EnrolmentCodesInvalidated);

/// <summary>
/// Storage of the MFA state (user row, backed up) and of the TOTP secrets (<c>user_totp</c>, a secret store: excluded from unencrypted runs and included as ring ciphertext in encrypted runs, D-120). Every mutation commits with its system-stream audit event in ONE
/// transaction: a mutation without its audit event does not happen. Secrets are encrypted by <see cref="TotpSecretProtector"/> before they reach the database. This layer holds NO policy (who may enrol or recover is
/// slices S2 to S7); it guarantees the invariants of the tables: one Active and one Pending secret per user, a monotonic accepted step, an MFA state that follows <see cref="MfaStateGraph"/>.
/// </summary>
public sealed class MfaStore(Func<string> connectionStringFactory, TimeProvider clock, AuditTrail audit, TotpSecretProtector protector, SystemRuntimeGate? gate = null, IdentityKeys? keys = null) : IMfaSecretStore, IAsyncDisposable
{
    /// <summary>Every connection of the store passes the least-privilege gate when one is given (the API always gives one; owner decision D-235): a runtime account that is not least-privilege
    /// for the secret store never reads or writes a secret. The administrative tool runs under the owner account and gives none.</summary>
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = await dataSource.Value.OpenConnectionAsync(ct);
        try { if (gate is not null) await gate.EnsureAsync(conn, ct); }
        catch { await conn.DisposeAsync(); throw; }
        return conn;
    }

    private readonly Lazy<NpgsqlDataSource> dataSource = new(() => NpgsqlDataSource.Create(connectionStringFactory()));

    public MfaStore(string connectionString, TimeProvider clock, AuditTrail audit, TotpSecretProtector protector, IdentityKeys? keys = null) : this(() => connectionString, clock, audit, protector, null, keys) { }

    /// <summary>TEST SEAM (internal): called inside the activation transaction at "after_activate" and "after_revoke", so tests can inject a failure between the steps and prove nothing is half done.</summary>
    internal Func<string, Task>? FaultProbe { get; set; }

    public ValueTask DisposeAsync() => dataSource.IsValueCreated ? dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;

    private IdentityKeys RequireKeys() => keys ?? throw new EncryptionUnavailableException("encryption_key_unavailable", "No identity key ring is configured.");

    /// <summary>A change of a covered field of the user row through the tagged primitive (identity integrity S-2a): verified row, the statement, revision +1, re-tag under CAS.</summary>
    private Task<bool> ChangeUserAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, Func<Task<bool>> writer, CancellationToken ct, bool requireValid = true) =>
        UserRow.ChangeAsync(conn, tx, RequireKeys(), userId, writer, bump: true, requireValid, ct);

    private Task<AuditReceipt> AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, short type, Guid? actor, Guid? target, string detail, CancellationToken ct) =>
        audit.AppendAsync(conn, tx, AuditStream.System, AuditCanonical.FromMicros(AuditCanonical.ToMicros(clock.GetUtcNow())), new AuditEventData(type, actor, target, null, detail), ct);

    // ---- State of a user ----

    public async Task<MfaState?> GetStateAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await UserRow.LoadAsync(conn, tx, userId, forUpdate: false, ct) is not { } row) return null;
        return row.IsValid(RequireKeys()) ? (MfaState)row.MfaState : MfaState.RecoveryRequired;                   // from the VERIFIED row; a row that does not verify is the strictest state (review L1 of PR 235)
    }

    /// <summary>
    /// Moves the user from <paramref name="from"/> to <paramref name="to"/> only if the graph allows it for <paramref name="cause"/> AND the stored state is still <paramref name="from"/> (compare-and-set).
    /// Returns false when the transition is not allowed or the stored state differs. Audited.
    /// </summary>
    public async Task<bool> TryChangeStateAsync(Guid userId, MfaState from, MfaState to, MfaStateCause cause, Guid? actor, CancellationToken ct = default)
    {
        if (!MfaStateGraph.IsAllowed(from, to, cause)) return false;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await SetStateAsync(conn, tx, userId, from, to, ct)) return false;
        var receipt = await AuditAsync(conn, tx, MfaAuditEvents.StateChanged, actor, userId, $"mfa state {from} -> {to} ({cause})", ct);
        await audit.CommitAsync(tx, receipt, ct);
        return true;
    }

    private async Task<bool> SetStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, MfaState from, MfaState to, CancellationToken ct) =>
        await ChangeUserAsync(conn, tx, userId, async () =>
        {
            await using var cmd = new NpgsqlCommand($"UPDATE {U.Table} SET {M.State} = @to, {M.StateChangedAtUtc} = @n WHERE {U.Id} = @u AND {M.State} = @from", conn, tx);
            cmd.Parameters.AddWithValue("to", (short)to);
            cmd.Parameters.AddWithValue("from", (short)from);
            cmd.Parameters.AddWithValue("n", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("u", userId);
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }, ct);

    // ---- The verifier's seam (IMfaSecretStore) ----

    public Task<MfaSecret?> FindActiveSecretAsync(Guid userId, CancellationToken ct) => FindSecretAsync(userId, 1, ct);

    private async Task<MfaSecret?> FindSecretAsync(Guid userId, short state, CancellationToken ct)
    {
        ProtectedTotpSecret stored; Guid secretId; long? lastStep;
        await using (var conn = await OpenAsync(ct))
        await using (var cmd = new NpgsqlCommand($"SELECT {TO.SecretId}, {TO.Cipher}, {TO.Nonce}, {TO.Tag}, {TO.KekId}, {TO.LastStep} FROM {TO.Table} WHERE {TO.UserId} = @u AND {TO.State} = @st", conn))
        {
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("st", state);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            secretId = r.GetGuid(0);
            stored = new ProtectedTotpSecret(r.GetFieldValue<byte[]>(1), r.GetFieldValue<byte[]>(2), r.GetFieldValue<byte[]>(3), r.GetString(4));
            lastStep = r.IsDBNull(5) ? null : r.GetInt64(5);
        }
        return new MfaSecret(secretId, protector.Unprotect(userId, secretId, stored), lastStep);          // MfaUnavailableException on a missing key or a failed authentication (no downgrade)
    }

    public async Task<MfaSecret?> FindPendingSecretAsync(Guid userId, CancellationToken ct) => await FindSecretAsync(userId, 0, ct);

    public async Task<bool> TryAdvanceLastStepAsync(Guid secretId, long matchedStep, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(                                                               // Active (a sign-in) or Pending (an enrolment being confirmed): both are replay-protected
            $"UPDATE {TO.Table} SET {TO.LastStep} = @s WHERE {TO.SecretId} = @id AND {TO.State} IN (0, 1) AND ({TO.LastStep} IS NULL OR {TO.LastStep} < @s)", conn);
        cmd.Parameters.AddWithValue("s", matchedStep);
        cmd.Parameters.AddWithValue("id", secretId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // ---- Secrets ----

    /// <summary>Stores a new Pending secret for the user (any earlier Pending secret is revoked) and returns its id. The caller keeps the plaintext only long enough to show it once.</summary>
    public async Task<Guid> CreatePendingAsync(Guid userId, byte[] secret, Guid? actor, CancellationToken ct = default)
    {
        var secretId = Guid.NewGuid();
        var stored = protector.Protect(userId, secretId, secret);
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockRow = new NpgsqlCommand($"SELECT 1 FROM {U.Table} WHERE {U.Id} = @u FOR UPDATE", conn, tx))     // serializes concurrent pending creations of one user (no unhandled unique violation)
        {
            lockRow.Parameters.AddWithValue("u", userId);
            if (await lockRow.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("user_not_found");
        }
        await using (var revoke = new NpgsqlCommand($"UPDATE {TO.Table} SET {TO.State} = 2 WHERE {TO.UserId} = @u AND {TO.State} = 0", conn, tx))
        {
            revoke.Parameters.AddWithValue("u", userId);
            await revoke.ExecuteNonQueryAsync(ct);
        }
        await using (var ins = new NpgsqlCommand(
            $"INSERT INTO {TO.Table} ({TO.SecretId}, {TO.UserId}, {TO.Cipher}, {TO.Nonce}, {TO.Tag}, {TO.KekId}, {TO.State}, {TO.CreatedAtUtc}) VALUES (@id, @u, @c, @n, @t, @k, 0, @at)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", secretId);
            ins.Parameters.AddWithValue("u", userId);
            ins.Parameters.AddWithValue("c", stored.Cipher);
            ins.Parameters.AddWithValue("n", stored.Nonce);
            ins.Parameters.AddWithValue("t", stored.Tag);
            ins.Parameters.AddWithValue("k", stored.KekId);
            ins.Parameters.AddWithValue("at", clock.GetUtcNow());
            await ins.ExecuteNonQueryAsync(ct);
        }
        var receipt = await AuditAsync(conn, tx, MfaAuditEvents.SecretCreated, actor, userId, $"pending TOTP secret {secretId:N} created", ct);
        await audit.CommitAsync(tx, receipt, ct);
        return secretId;
    }

    /// <summary>
    /// Activates the user's Pending secret <paramref name="secretId"/> after the caller verified a code for <paramref name="matchedStep"/>: the previous Active secret (a re-enrolment replaces it) is revoked, the user's MFA state becomes
    /// <see cref="MfaState.Enrolled"/> (from <see cref="MfaState.EnrolmentRequired"/>, or from <see cref="MfaState.None"/> for a voluntary enrolment), all in ONE transaction. A user who is <see cref="MfaState.RecoveryRequired"/> can NEVER
    /// activate a secret here (only recovery or DR moves them): returns false, as it does when the pending secret is gone.
    /// </summary>
    public async Task<bool> ActivatePendingAsync(Guid userId, Guid secretId, long matchedStep, Guid? actor, CancellationToken ct = default, bool consumeEnrolmentCode = false, SessionIssue? reissue = null)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // the row is verified under its lock before anything is decided from it (a user row that does not verify never activates a secret)
        if (await UserRow.LoadAsync(conn, tx, userId, forUpdate: true, ct) is not { } locked || !locked.IsValid(RequireKeys())) return false;
        var current = (MfaState)locked.MfaState;
        if (current == MfaState.RecoveryRequired) return false;
        if (consumeEnrolmentCode)                                                                                // the one-time code of the user row is spent by the confirmation, atomically with it
        {
            var spent = await ChangeUserAsync(conn, tx, userId, async () =>
            {
                await using var spend = new NpgsqlCommand($"UPDATE {U.Table} SET {M.EnrolmentCodeConsumedAtUtc} = @n WHERE {U.Id} = @u AND {M.EnrolmentCodeHash} IS NOT NULL AND {M.EnrolmentCodeConsumedAtUtc} IS NULL AND {M.EnrolmentCodeExpiresAtUtc} > @n", conn, tx);
                spend.Parameters.AddWithValue("n", clock.GetUtcNow());
                spend.Parameters.AddWithValue("u", userId);
                return await spend.ExecuteNonQueryAsync(ct) == 1;
            }, ct);
            if (!spent) return false;
        }
        await using (var revoke = new NpgsqlCommand($"UPDATE {TO.Table} SET {TO.State} = 2 WHERE {TO.UserId} = @u AND {TO.State} = 1", conn, tx))
        {
            revoke.Parameters.AddWithValue("u", userId);
            await revoke.ExecuteNonQueryAsync(ct);
        }
        await using (var act = new NpgsqlCommand(
            $"UPDATE {TO.Table} SET {TO.State} = 1, {TO.ConfirmedAtUtc} = @n, {TO.LastStep} = @s, {TO.FailedConfirmations} = 0 WHERE {TO.SecretId} = @id AND {TO.UserId} = @u AND {TO.State} = 0", conn, tx))
        {
            act.Parameters.AddWithValue("n", clock.GetUtcNow());
            act.Parameters.AddWithValue("s", matchedStep);
            act.Parameters.AddWithValue("id", secretId);
            act.Parameters.AddWithValue("u", userId);
            if (await act.ExecuteNonQueryAsync(ct) != 1) return false;                                           // the transaction is disposed without a commit
        }
        var receipts = new List<AuditReceipt>();
        if (current is MfaState.None or MfaState.EnrolmentRequired)                                               // None is a voluntary enrolment: one audited step None -> Enrolled
        {
            Assert(MfaStateGraph.IsAllowed(current, MfaState.Enrolled, MfaStateCause.EnrolmentConfirmed) && await SetStateAsync(conn, tx, userId, current, MfaState.Enrolled, ct));
            receipts.Add(await AuditAsync(conn, tx, MfaAuditEvents.StateChanged, actor, userId, $"mfa state {current} -> Enrolled (EnrolmentConfirmed)", ct));
        }
        receipts.Add(await AuditAsync(conn, tx, MfaAuditEvents.SecretActivated, actor, userId, $"TOTP secret {secretId:N} activated", ct));
        if (FaultProbe is { } afterActivate) await afterActivate("after_activate");
        if (reissue is not null)
        {
            // D-142 (d), review M2/M3: the new factor invalidates EVERY session of the user (the confirming one too) and a NEW session, verified now, replaces them, all in this ONE transaction with the activation
            await IdentityStore.RevokeUserSessionsAsync(conn, tx, RequireKeys(), userId, SessionRevocation.Administrative, null, clock.GetUtcNow(), ct);
            if (FaultProbe is { } afterRevoke) await afterRevoke("after_revoke");
            await IdentityStore.InsertSessionAsync(conn, tx, keys ?? throw new EncryptionUnavailableException("encryption_key_unavailable", "No identity key ring is configured."), reissue, userId, clock.GetUtcNow(), ct);
        }
        await audit.CommitAsync(tx, receipts, ct);
        return true;
    }

    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("mfa_state_changed_concurrently"); }

    // ---- The one-time enrolment code of the user row (bootstrap administrator; every other code lives in its approval request) ----

    public static byte[] EnrolmentCodeHash(string code) => SessionToken.HashKey("mfa-enrol-code:" + code.Trim().ToUpperInvariant());

    /// <summary>
    /// Records a successful step-up (Phase 4.1 S3a, D-160): sets the live session's second-factor proof time to now and writes the audit event in ONE transaction.
    /// Null when the session is not live or does not belong to the user (nothing is written). The proof time only moves forward, never back.
    /// </summary>
    public async Task<DateTimeOffset?> RecordStepUpAsync(Guid sessionId, Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // identity integrity S-1: the proof time is a tagged field, so it is written by compare-and-swap on the session's tag, under the row lock, after the row itself verified
        var k = keys ?? throw new EncryptionUnavailableException("encryption_key_unavailable", "No identity key ring is configured.");
        var before = await SessionRow.LoadForUpdateAsync(conn, tx, sessionId, ct);
        if (before is null || before.UserId != userId || !before.IsValid(k)) { await tx.RollbackAsync(ct); return null; }
        var stamp = MacInput.Truncate(now);
        if (before.VerifiedAt is { } prior && prior >= stamp) { await tx.RollbackAsync(ct); return null; }
        if (!await SessionRow.UpdateAsync(conn, tx, k, before, before with { AuthMethods = 3, VerifiedAt = stamp }, ct)) { await tx.RollbackAsync(ct); return null; }
        var receipt = await AuditAsync(conn, tx, MfaAuditEvents.StepUpSucceeded, userId, userId, $"step-up succeeded session {sessionId:N}", ct);
        await audit.CommitAsync(tx, receipt, ct);
        return stamp;
    }

    /// <summary>Audits a step-up refused for a wrong or replayed code (never the code). Called only for verifier-counted failures, so the shared MFA throttle bounds how many rows one session can append; throttled and unavailable answers are not audited.</summary>
    public async Task RecordStepUpFailureAsync(Guid sessionId, Guid userId, string cause, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var receipt = await AuditAsync(conn, tx, MfaAuditEvents.StepUpFailed, userId, userId, $"step-up refused ({cause}) session {sessionId:N}", ct);
        await audit.CommitAsync(tx, receipt, ct);
    }

    /// <summary>Generates a one-time enrolment code, stores only its hash with the expiry (at most 24 hours), and returns the plaintext once. Audited.</summary>
    public async Task<string> IssueEnrolmentCodeAsync(Guid userId, TimeSpan validFor, Guid? actor, CancellationToken ct = default)
    {
        if (validFor <= TimeSpan.Zero || validFor > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(validFor));
        var code = Totp.ToBase32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(10));              // 16 characters
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var issued = await ChangeUserAsync(conn, tx, userId, async () =>
        {
            await using var cmd = new NpgsqlCommand($"UPDATE {U.Table} SET {M.EnrolmentCodeHash} = @h, {M.EnrolmentCodeExpiresAtUtc} = @e, {M.EnrolmentCodeConsumedAtUtc} = NULL WHERE {U.Id} = @u", conn, tx);
            cmd.Parameters.AddWithValue("h", EnrolmentCodeHash(code));
            cmd.Parameters.AddWithValue("e", clock.GetUtcNow().Add(validFor));
            cmd.Parameters.AddWithValue("u", userId);
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }, ct);
        if (!issued) throw new InvalidOperationException("user_not_found");
        var receipt = await AuditAsync(conn, tx, MfaAuditEvents.StateChanged, actor, userId, "one-time enrolment code issued", ct);
        await audit.CommitAsync(tx, receipt, ct);
        return code;
    }

    /// <summary>True when <paramref name="code"/> matches the user's unexpired, unconsumed enrolment code (it is NOT consumed here: the confirmation consumes it).</summary>
    public async Task<bool> CheckEnrolmentCodeAsync(Guid userId, string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64) return false;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // the code columns are covered by the user tag (plan review H1): a row that does not verify opens no enrolment, whatever its code columns say
        if (await UserRow.LoadAsync(conn, tx, userId, forUpdate: false, ct) is not { } row || !row.IsValid(RequireKeys())) return false;
        return row.CodeHash is { } stored && IdentityKeys.Equal(stored, EnrolmentCodeHash(code)) && row.CodeConsumedAt is null && row.CodeExpiresAt > clock.GetUtcNow();
    }

    /// <summary>Revokes every Pending and Active secret of the user (recovery, DR reset). The user's state is NOT touched here: the caller moves it through <see cref="TryChangeStateAsync"/> in the same procedure. Returns the number revoked. Audited.</summary>
    public async Task<int> RevokeAllAsync(Guid userId, Guid? actor, string reason, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int count;
        await using (var cmd = new NpgsqlCommand($"UPDATE {TO.Table} SET {TO.State} = 2 WHERE {TO.UserId} = @u AND {TO.State} IN (0, 1)", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", userId);
            count = await cmd.ExecuteNonQueryAsync(ct);
        }
        var receipt = await AuditAsync(conn, tx, MfaAuditEvents.SecretsRevoked, actor, userId, $"{count} TOTP secret(s) revoked: {reason}", ct);
        await audit.CommitAsync(tx, receipt, ct);
        return count;
    }

    // ---- Rotation ----

    /// <summary>The key ids that still protect a usable (Pending or Active) secret, with how many secrets each: what <c>totp-rewrap --check</c> prints and what key retirement checks. Revoked secrets are never decrypted and do not count.</summary>
    public async Task<IReadOnlyDictionary<string, int>> ReferencedKeysAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {TO.KekId}, count(*) FROM {TO.Table} WHERE {TO.State} IN (0, 1) GROUP BY {TO.KekId}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetString(0)] = (int)r.GetInt64(1);
        return result;
    }

    /// <summary>
    /// Re-encrypts up to <paramref name="batchSize"/> usable secrets that are not under <paramref name="targetKekId"/> (the active key), each in the transaction of its batch with the transaction-local rewrap flag the guard trigger
    /// requires. Idempotent and resumable: a row already under the target key is skipped, a row changed concurrently is left for the next call. Returns how many were rewrapped and how many remain.
    /// </summary>
    public async Task<RewrapResult> RewrapAsync(string targetKekId, int batchSize, Guid? actor, CancellationToken ct = default)
    {
        if (!Security.KekRing.IsValidKeyId(targetKekId)) throw new ArgumentException("Invalid key id.", nameof(targetKekId));
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var flag = new NpgsqlCommand("SELECT set_config('hasm.totp_rewrap', 'on', true)", conn, tx)) await flag.ExecuteNonQueryAsync(ct);
        var rows = new List<(Guid SecretId, Guid UserId, ProtectedTotpSecret Stored)>();
        await using (var sel = new NpgsqlCommand(
            $"SELECT {TO.SecretId}, {TO.UserId}, {TO.Cipher}, {TO.Nonce}, {TO.Tag}, {TO.KekId} FROM {TO.Table} WHERE {TO.State} IN (0, 1) AND {TO.KekId} <> @k ORDER BY {TO.CreatedAtUtc}, {TO.SecretId} LIMIT @b FOR UPDATE", conn, tx))
        {
            sel.Parameters.AddWithValue("k", targetKekId);
            sel.Parameters.AddWithValue("b", batchSize);
            await using var r = await sel.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetGuid(0), r.GetGuid(1), new ProtectedTotpSecret(r.GetFieldValue<byte[]>(2), r.GetFieldValue<byte[]>(3), r.GetFieldValue<byte[]>(4), r.GetString(5))));
        }
        var done = 0;
        foreach (var (secretId, userId, stored) in rows)
        {
            var moved = protector.Rewrap(userId, secretId, stored);
            if (moved.KekId != targetKekId) throw new InvalidOperationException("totp_rewrap_target_is_not_the_active_key");   // the ring's active key must be the target, or the loop would never converge
            await using var upd = new NpgsqlCommand(
                $"UPDATE {TO.Table} SET {TO.Cipher} = @c, {TO.Nonce} = @n, {TO.Tag} = @t, {TO.KekId} = @k WHERE {TO.SecretId} = @id AND {TO.KekId} = @old", conn, tx);
            upd.Parameters.AddWithValue("c", moved.Cipher);
            upd.Parameters.AddWithValue("n", moved.Nonce);
            upd.Parameters.AddWithValue("t", moved.Tag);
            upd.Parameters.AddWithValue("k", moved.KekId);
            upd.Parameters.AddWithValue("id", secretId);
            upd.Parameters.AddWithValue("old", stored.KekId);
            done += await upd.ExecuteNonQueryAsync(ct);
        }
        int remaining;
        await using (var cnt = new NpgsqlCommand($"SELECT count(*) FROM {TO.Table} WHERE {TO.State} IN (0, 1) AND {TO.KekId} <> @k", conn, tx))
        {
            cnt.Parameters.AddWithValue("k", targetKekId);
            remaining = (int)(long)(await cnt.ExecuteScalarAsync(ct))!;
        }
        var receipt = await AuditAsync(conn, tx, MfaAuditEvents.SecretsRewrapped, actor, null, $"{done} TOTP secret(s) rewrapped to key {targetKekId}; {remaining} remaining", ct);
        await audit.CommitAsync(tx, receipt, ct);
        return new RewrapResult(done, remaining);
    }

    // ---- Restore (plan section 4, D-106, D-107) ----

    /// <summary>
    /// The state fix-up after a SYSTEM restore: every Enrolled user without an Active secret (the secret store is not in an unencrypted dump) becomes RecoveryRequired, and with <paramref name="differentInstallation"/>
    /// EVERY Enrolled user does (D-107); every unconsumed one-time enrolment code is invalidated. Never touches EnrolmentRequired or None users, never creates an enrolment path. Audited. The caller (the restore procedure) keeps
    /// logins blocked until its own audited completion step.
    /// </summary>
    public async Task<RestoreMfaResult> ApplyRestoreAsync(bool differentInstallation, Guid? actor, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // per row through the tagged primitive (security-reducing, so allowed on a row that does not verify: after a restore under another ring every tag is invalid and the fix-up must still run)
        var toMark = new List<Guid>(); var withCode = new List<Guid>();
        await using (var sel = new NpgsqlCommand(
            $"SELECT u.{U.Id}, (u.{M.State} = 2 AND (@all OR NOT EXISTS (SELECT 1 FROM {TO.Table} t WHERE t.{TO.UserId} = u.{U.Id} AND t.{TO.State} = 1))), (u.{M.EnrolmentCodeHash} IS NOT NULL AND u.{M.EnrolmentCodeConsumedAtUtc} IS NULL) FROM {U.Table} u", conn, tx))
        {
            sel.Parameters.AddWithValue("all", differentInstallation);
            await using var r = await sel.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { if (r.GetBoolean(1)) toMark.Add(r.GetGuid(0)); if (r.GetBoolean(2)) withCode.Add(r.GetGuid(0)); }
        }
        int marked = 0, codes = 0;
        foreach (var id in toMark)
            if (await ChangeUserAsync(conn, tx, id, async () =>
            {
                await using var mark = new NpgsqlCommand($"UPDATE {U.Table} SET {M.State} = 3, {M.StateChangedAtUtc} = @n WHERE {U.Id} = @id AND {M.State} = 2", conn, tx);
                mark.Parameters.AddWithValue("n", clock.GetUtcNow());
                mark.Parameters.AddWithValue("id", id);
                return await mark.ExecuteNonQueryAsync(ct) == 1;
            }, ct, requireValid: false)) marked++;
        foreach (var id in withCode)
            if (await ChangeUserAsync(conn, tx, id, async () =>
            {
                await using var inv = new NpgsqlCommand($"UPDATE {U.Table} SET {M.EnrolmentCodeHash} = NULL, {M.EnrolmentCodeExpiresAtUtc} = NULL, {M.EnrolmentCodeConsumedAtUtc} = NULL WHERE {U.Id} = @id AND {M.EnrolmentCodeHash} IS NOT NULL AND {M.EnrolmentCodeConsumedAtUtc} IS NULL", conn, tx);
                inv.Parameters.AddWithValue("id", id);
                return await inv.ExecuteNonQueryAsync(ct) == 1;
            }, ct, requireValid: false)) codes++;
        var receipt = await AuditAsync(conn, tx, MfaAuditEvents.RestoreApplied, actor, null, $"restore applied: {marked} user(s) set to RecoveryRequired{(differentInstallation ? " (different installation)" : "")}; {codes} enrolment code(s) invalidated", ct);
        await audit.CommitAsync(tx, receipt, ct);
        return new RestoreMfaResult(marked, codes);
    }
}
