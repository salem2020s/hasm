using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using A = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using PR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRole;
using SR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.SalesRepresentative;

namespace Hasm.Infrastructure.Parties;

/// <summary>Business audit events of customers, suppliers and sales representatives (company stream, 1500-1509). Persisted codes: never renumber.</summary>
public static class PartyRoleAuditEvents
{
    public const short RepresentativeCreated = 1500;
    public const short RepresentativeUpdated = 1501;
    public const short RoleCreated = 1502;
    public const short RoleUpdated = 1503;
}

public enum PartyRoleKind : short { Customer = 1, Supplier = 2 }
public enum PartyPaymentMode : short { Cash = 1, Credit = 2, Mixed = 3 }

/// <summary>IsActive: null = true on create and "unchanged" on update. UserId optionally links the representative to a user of the system (no commission calculation exists, OD-3 default).</summary>
public sealed record SalesRepresentativeInput(string? Code, string? NameAr, string? NameEn, Guid? UserId, bool? IsActive = null);
public sealed record SalesRepresentativeView(Guid Id, string Code, string NameAr, string? NameEn, Guid? UserId, bool IsActive, long Revision);

/// <summary>
/// A role of a local party. On create PartyId, Role and PaymentMode are required; on update party and kind never change (null or equal). TermDays is required unless the mode is cash (then 0).
/// CreditLimit (company base currency, null = none recorded) is DATA: nothing is enforced against it until the sales documents exist. The control account is the receivables (customer, asset) or
/// payables (supplier, liability) account the party's open items will post to.
/// </summary>
public sealed record PartyRoleInput(Guid? PartyId, PartyRoleKind? Role, PartyPaymentMode? PaymentMode, int? TermDays, decimal? CreditLimit, Guid? ControlAccountId, Guid? RepresentativeId, bool? IsActive = null);
public sealed record PartyRoleView(Guid Id, Guid PartyId, string PartyCode, string PartyName, PartyRoleKind Role, bool IsActive, PartyPaymentMode PaymentMode, int TermDays, decimal? CreditLimit,
    Guid? ControlAccountId, string? ControlAccountCode, Guid? RepresentativeId, long Revision);
public sealed record PartyRoleCapabilities(bool RolesView, bool RolesConfigure, bool RepsView, bool RepsConfigure);

/// <summary>
/// The audited company-side store of customer and supplier roles and sales representatives (Phase 3.9B). <b>No authorization here by design:</b> <see cref="PartyRoleManagementService"/> wraps it.
/// Every write is one company transaction with its audit event; roles and representatives advance by revision-fenced updates and are never removed (PostgreSQL refuses it).
/// The shared identity of a party stays in the system database and carries none of this.
/// </summary>
public sealed partial class PartyRoleService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }
    private static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, code, [new FieldError(field, code)]);

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,29}$")] private static partial Regex CodePattern();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static object Db<T>(T? v) where T : class => (object?)v ?? DBNull.Value;
    private static object Db<T>(T? v, bool _) where T : struct => v is null ? DBNull.Value : v.Value;

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == SR.UqCode) { return ConfigResult<T>.Invalid([new FieldError("code", "code_taken")]); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == SR.UqUser) { return ConfigResult<T>.Invalid([new FieldError("userId", "user_taken")]); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == PR.UqPartyRole) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "role_exists"); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invariant_violation"); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.ForeignKeyViolation) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "concurrent_change"); }   // a database guard refused a racing or inconsistent change: nothing was written
    }

    // ================================================================================================================ sales representatives

    private static async Task<List<SalesRepresentativeView>> LoadReps(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var rows = new List<SalesRepresentativeView>();
        await using var q = new NpgsqlCommand($"SELECT {SR.Id}, {SR.Code}, {SR.NameAr}, {SR.NameEn}, {SR.UserId}, {SR.IsActive}, {SR.Revision} FROM {SR.Table}{(id is null ? "" : $" WHERE {SR.Id} = @id")} ORDER BY {SR.Code}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        if (id is { } g) q.Parameters.AddWithValue("id", g);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(new SalesRepresentativeView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetBoolean(5), r.GetInt64(6)));
        return rows;
    }

    public async Task<IReadOnlyList<SalesRepresentativeView>> ListRepresentativesAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadReps(conn, null, null, false, ct);
    }

    private static (List<FieldError> Errors, string Code, string NameAr, string? NameEn) RepNames(string? code, string? nameAr, string? nameEn)
    {
        var errors = new List<FieldError>();
        var c = code?.Trim().ToUpperInvariant() ?? "";
        if (!CodePattern().IsMatch(c)) errors.Add(new FieldError("code", "code_invalid"));
        var ar = nameAr?.Trim() ?? "";
        if (ar.Length is < 1 or > 200) errors.Add(new FieldError("nameAr", "name_invalid"));
        var en = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim();
        if (en is { Length: > 200 }) errors.Add(new FieldError("nameEn", "name_invalid"));
        return (errors, c, ar, en);
    }

    public async Task<ConfigResult<SalesRepresentativeView>> CreateRepresentativeAsync(Guid actor, Guid companyId, SalesRepresentativeInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<SalesRepresentativeView>.Invalid([new FieldError("input", "required")]);
        var n = RepNames(input.Code, input.NameAr, input.NameEn);
        if (n.Errors.Count > 0) return ConfigResult<SalesRepresentativeView>.Invalid(n.Errors);
        var id = Guid.NewGuid();
        var r = await Mutate(companyId, async work =>
        {
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {SR.Table} ({SR.Id}, {SR.Code}, {SR.NameAr}, {SR.NameEn}, {SR.UserId}, {SR.IsActive}, {SR.Revision}, {SR.CreatedBy}, {SR.CreatedAtUtc}, {SR.UpdatedAtUtc}) VALUES (@id, @c, @ar, @en, @u, @a, 1, @by, @now, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("c", n.Code); ins.Parameters.AddWithValue("ar", n.NameAr); ins.Parameters.AddWithValue("en", Db(n.NameEn));
                ins.Parameters.AddWithValue("u", Db(input.UserId, true)); ins.Parameters.AddWithValue("a", input.IsActive ?? true); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PartyRoleAuditEvents.RepresentativeCreated, ActorUserId: actor, SubjectId: id, Detail: $"sales representative created code={n.Code}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<SalesRepresentativeView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return ConfigResult<SalesRepresentativeView>.Ok((await LoadReps(conn, null, id, false, ct)).Single());
    }

    /// <summary>Updates names, user link and the active flag (revision-fenced). The code never changes. An omitted flag never changes the state.</summary>
    public async Task<ConfigResult<SalesRepresentativeView>> UpdateRepresentativeAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, SalesRepresentativeInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<SalesRepresentativeView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<SalesRepresentativeView>.Invalid([new FieldError("input", "required")]);
        var r = await Mutate(companyId, async work =>
        {
            var cur = (await LoadReps(work.Connection, work.Transaction, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "representative_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            var n = RepNames(input.Code ?? cur.Code, input.NameAr, input.NameEn);
            if (n.Errors.Count > 0) throw new Abort(ConfigStatus.Invalid, "invalid", n.Errors);
            if (!string.Equals(n.Code, cur.Code, StringComparison.OrdinalIgnoreCase)) throw Invalid("code", "code_immutable");
            var active = input.IsActive ?? cur.IsActive;
            await using (var upd = new NpgsqlCommand($"UPDATE {SR.Table} SET {SR.NameAr} = @ar, {SR.NameEn} = @en, {SR.UserId} = @u, {SR.IsActive} = @a, {SR.Revision} = {SR.Revision} + 1, {SR.UpdatedAtUtc} = @now WHERE {SR.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("ar", n.NameAr); upd.Parameters.AddWithValue("en", Db(n.NameEn)); upd.Parameters.AddWithValue("u", Db(input.UserId, true));
                upd.Parameters.AddWithValue("a", active); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PartyRoleAuditEvents.RepresentativeUpdated, ActorUserId: actor, SubjectId: id, Detail: $"sales representative updated r{cur.Revision + 1} code={cur.Code} active={active} user={(input.UserId is { } u ? u.ToString("N") : "none")}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<SalesRepresentativeView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return ConfigResult<SalesRepresentativeView>.Ok((await LoadReps(conn, null, id, false, ct)).Single());
    }

    // ================================================================================================================ party roles

    private const string RoleSelect = $"SELECT r.{PR.Id}, r.{PR.PartyId}, p.{LP.Code}, p.{LP.DisplayName}, r.{PR.Role}, r.{PR.IsActive}, r.{PR.PaymentMode}, r.{PR.TermDays}, r.{PR.CreditLimit}, r.{PR.ControlAccountId}, a.{A.Code}, r.{PR.RepresentativeId}, r.{PR.Revision} " +
        $"FROM {PR.Table} r JOIN {LP.Table} p ON p.{LP.Id} = r.{PR.PartyId} LEFT JOIN {A.Table} a ON a.{A.Id} = r.{PR.ControlAccountId}";

    private static async Task<List<PartyRoleView>> LoadRoles(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, PartyRoleKind? kind, bool forUpdate, CancellationToken ct)
    {
        var rows = new List<PartyRoleView>();
        var where = new List<string>();
        await using var q = new NpgsqlCommand { Connection = conn, Transaction = tx };
        if (id is { } g) { where.Add($"r.{PR.Id} = @id"); q.Parameters.AddWithValue("id", g); }
        if (kind is { } k) { where.Add($"r.{PR.Role} = @k"); q.Parameters.AddWithValue("k", (short)k); }
        q.CommandText = RoleSelect + (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where)) + $" ORDER BY p.{LP.Code}, r.{PR.Role}" + (forUpdate ? $" FOR UPDATE OF r" : "");
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new PartyRoleView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), (PartyRoleKind)r.GetInt16(4), r.GetBoolean(5), (PartyPaymentMode)r.GetInt16(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetDecimal(8),
                r.IsDBNull(9) ? null : r.GetGuid(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetGuid(11), r.GetInt64(12)));
        return rows;
    }

    /// <summary>All roles, or only the customers or the suppliers.</summary>
    public async Task<IReadOnlyList<PartyRoleView>> ListRolesAsync(Guid companyId, PartyRoleKind? kind, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadRoles(conn, null, null, kind, false, ct);
    }

    /// <summary>Validates the terms and the references of a role; throws the field errors. Locks the referenced account and representative against concurrent change.</summary>
    private static async Task ValidateTerms(CompanyAuditedWork work, PartyRoleKind kind, PartyRoleInput input, PartyRoleView? current, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        if (input.PaymentMode is not { } mode || !Enum.IsDefined(mode)) errors.Add(new FieldError("paymentMode", "payment_mode_invalid"));
        else if (mode == PartyPaymentMode.Cash)
        {
            if (input.TermDays is not (null or 0)) errors.Add(new FieldError("termDays", "terms_not_allowed_for_cash"));
        }
        else if (input.TermDays is not { } days) errors.Add(new FieldError("termDays", "required"));
        else if (days is < 0 or > 3650) errors.Add(new FieldError("termDays", "term_days_invalid"));
        if (input.CreditLimit is { } limit && (limit < 0 || limit > 999_999_999_999_999m || decimal.Round(limit, 4) != limit)) errors.Add(new FieldError("creditLimit", "credit_limit_invalid"));
        if (input.RepresentativeId is not null && kind != PartyRoleKind.Customer) errors.Add(new FieldError("representativeId", "representative_not_allowed"));
        if (errors.Count > 0) throw new Abort(ConfigStatus.Invalid, "invalid", errors);

        // a reference that is UNCHANGED is not re-validated: a role whose account or representative was deactivated later must still be editable and deactivatable (PostgreSQL checks the account whenever it changes)
        if (input.ControlAccountId is { } acc && acc != current?.ControlAccountId)
        {
            await using var q = new NpgsqlCommand($"SELECT {A.Type}, {A.IsGroup}, {A.IsActive} FROM {A.Table} WHERE {A.Id} = @a FOR SHARE", work.Connection, work.Transaction);
            q.Parameters.AddWithValue("a", acc);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw Invalid("controlAccountId", "account_not_found");
            var type = r.GetInt16(0); var group = r.GetBoolean(1); var active = r.GetBoolean(2);
            if (group) throw Invalid("controlAccountId", "account_not_postable");
            if (!active) throw Invalid("controlAccountId", "account_inactive");
            if (kind == PartyRoleKind.Customer && type != 1) throw Invalid("controlAccountId", "customer_account_must_be_asset");
            if (kind == PartyRoleKind.Supplier && type != 2) throw Invalid("controlAccountId", "supplier_account_must_be_liability");
        }
        if (input.RepresentativeId is { } rep && rep != current?.RepresentativeId)
        {
            var reps = await LoadReps(work.Connection, work.Transaction, rep, true, ct);
            if (reps.Count == 0) throw Invalid("representativeId", "representative_not_found");
            if (!reps[0].IsActive) throw Invalid("representativeId", "representative_inactive");
        }
    }

    public async Task<ConfigResult<PartyRoleView>> CreateRoleAsync(Guid actor, Guid companyId, PartyRoleInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<PartyRoleView>.Invalid([new FieldError("input", "required")]);
        var errors = new List<FieldError>();
        if (input.PartyId is null) errors.Add(new FieldError("partyId", "required"));
        if (input.Role is not { } kind || !Enum.IsDefined(kind)) errors.Add(new FieldError("role", "role_invalid"));
        if (errors.Count > 0) return ConfigResult<PartyRoleView>.Invalid(errors);
        var id = Guid.NewGuid();
        var r = await Mutate(companyId, async work =>
        {
            var partyId = input.PartyId!.Value; var role = input.Role!.Value;
            await using (var pq = new NpgsqlCommand($"SELECT {LP.IsActive} FROM {LP.Table} WHERE {LP.Id} = @p FOR SHARE", work.Connection, work.Transaction))
            {
                pq.Parameters.AddWithValue("p", partyId);
                if (await pq.ExecuteScalarAsync(ct) is not bool active) throw Invalid("partyId", "party_not_found");
                if (!active) throw Invalid("partyId", "party_inactive");
            }
            await ValidateTerms(work, role, input, null, ct);
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {PR.Table} ({PR.Id}, {PR.PartyId}, {PR.Role}, {PR.IsActive}, {PR.PaymentMode}, {PR.TermDays}, {PR.CreditLimit}, {PR.ControlAccountId}, {PR.RepresentativeId}, {PR.Revision}, {PR.CreatedBy}, {PR.CreatedAtUtc}, {PR.UpdatedAtUtc}) " +
                "VALUES (@id, @p, @k, @a, @m, @d, @l, @acc, @rep, 1, @by, @now, @now)", work.Connection, work.Transaction))
            {
                Bind(ins, input, input.IsActive ?? true); ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("p", partyId); ins.Parameters.AddWithValue("k", (short)role);
                ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PartyRoleAuditEvents.RoleCreated, ActorUserId: actor, SubjectId: id, Detail: $"party role created party={partyId:N} role={(short)role} mode={(short)input.PaymentMode!.Value} days={input.TermDays ?? 0} limit={Fmt(input.CreditLimit)} account={Short(input.ControlAccountId)} rep={Short(input.RepresentativeId)}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<PartyRoleView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return ConfigResult<PartyRoleView>.Ok((await LoadRoles(conn, null, id, null, false, ct)).Single());
    }

    /// <summary>Replaces the terms of a role (revision-fenced; party and kind never change). The audit records the previous terms.</summary>
    public async Task<ConfigResult<PartyRoleView>> UpdateRoleAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, PartyRoleInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<PartyRoleView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<PartyRoleView>.Invalid([new FieldError("input", "required")]);
        var r = await Mutate(companyId, async work =>
        {
            var cur = (await LoadRoles(work.Connection, work.Transaction, id, null, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "role_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (input.PartyId is { } p && p != cur.PartyId) throw Invalid("partyId", "party_immutable");
            if (input.Role is { } k && k != cur.Role) throw Invalid("role", "role_immutable");
            await ValidateTerms(work, cur.Role, input, cur, ct);
            var active = input.IsActive ?? cur.IsActive;
            await using (var upd = new NpgsqlCommand($"UPDATE {PR.Table} SET {PR.IsActive} = @a, {PR.PaymentMode} = @m, {PR.TermDays} = @d, {PR.CreditLimit} = @l, {PR.ControlAccountId} = @acc, {PR.RepresentativeId} = @rep, {PR.Revision} = {PR.Revision} + 1, {PR.UpdatedAtUtc} = @now WHERE {PR.Id} = @id", work.Connection, work.Transaction))
            {
                Bind(upd, input, active); upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PartyRoleAuditEvents.RoleUpdated, ActorUserId: actor, SubjectId: id,
                Detail: $"party role updated r{cur.Revision + 1} role={(short)cur.Role} active={active} mode={(short)input.PaymentMode!.Value} days={input.TermDays ?? 0} limit={Fmt(input.CreditLimit)} account={Short(input.ControlAccountId)} rep={Short(input.RepresentativeId)} " +
                $"was: mode={(short)cur.PaymentMode} days={cur.TermDays} limit={Fmt(cur.CreditLimit)} account={Short(cur.ControlAccountId)} rep={Short(cur.RepresentativeId)} active={cur.IsActive}"), ct);
            return true;
        }, ct);
        if (!r.IsOk) return ConfigResult<PartyRoleView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return ConfigResult<PartyRoleView>.Ok((await LoadRoles(conn, null, id, null, false, ct)).Single());
    }

    private static string Fmt(decimal? v) => v is null ? "none" : v.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    private static string Short(Guid? v) => v is null ? "none" : v.Value.ToString("N")[..8];

    private static void Bind(NpgsqlCommand c, PartyRoleInput i, bool active)
    {
        c.Parameters.AddWithValue("a", active); c.Parameters.AddWithValue("m", (short)i.PaymentMode!.Value); c.Parameters.AddWithValue("d", i.TermDays ?? 0);
        c.Parameters.AddWithValue("l", Db(i.CreditLimit, true)); c.Parameters.AddWithValue("acc", Db(i.ControlAccountId, true)); c.Parameters.AddWithValue("rep", Db(i.RepresentativeId, true));
    }
}

/// <summary>Permission-checked entry: <c>company.partyroles.view|configure</c> for roles, <c>company.salesreps.view|configure</c> for representatives (company-wide).</summary>
public sealed class PartyRoleManagementService(AccessService access, PartyRoleService roles)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Can(Guid user, Guid company, string code, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct);
    private async Task<ConfigResult<T>> Read<T>(Guid user, Guid company, string perm, Func<Task<T>> read, CancellationToken ct) => await Can(user, company, perm, ct) ? ConfigResult<T>.Ok(await read()) : Denied<T>();
    private async Task<ConfigResult<T>> Write<T>(Guid user, Guid company, string perm, Func<Task<ConfigResult<T>>> write, CancellationToken ct) => await Can(user, company, perm, ct) ? await write() : Denied<T>();

    public async Task<PartyRoleCapabilities> CapabilitiesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var e = await access.GetEffectivePermissionsAsync(user, company, ct);
        var c = PermissionCatalog.Codes.PartyRolesView;
        return new PartyRoleCapabilities(e.HasCompanyWide(c), e.HasCompanyWide(PermissionCatalog.Codes.PartyRolesConfigure), e.HasCompanyWide(PermissionCatalog.Codes.SalesRepsView), e.HasCompanyWide(PermissionCatalog.Codes.SalesRepsConfigure));
    }

    public Task<ConfigResult<IReadOnlyList<PartyRoleView>>> ListRolesAsync(Guid user, Guid company, PartyRoleKind? kind, CancellationToken ct = default) => Read(user, company, PermissionCatalog.Codes.PartyRolesView, () => roles.ListRolesAsync(company, kind, ct), ct);
    public Task<ConfigResult<PartyRoleView>> CreateRoleAsync(Guid user, Guid company, PartyRoleInput? input, CancellationToken ct = default) => Write(user, company, PermissionCatalog.Codes.PartyRolesConfigure, () => roles.CreateRoleAsync(user, company, input, ct), ct);
    public Task<ConfigResult<PartyRoleView>> UpdateRoleAsync(Guid user, Guid company, Guid id, long? revision, PartyRoleInput? input, CancellationToken ct = default) => Write(user, company, PermissionCatalog.Codes.PartyRolesConfigure, () => roles.UpdateRoleAsync(user, company, id, revision, input, ct), ct);
    public Task<ConfigResult<IReadOnlyList<SalesRepresentativeView>>> ListRepresentativesAsync(Guid user, Guid company, CancellationToken ct = default) => Read(user, company, PermissionCatalog.Codes.SalesRepsView, () => roles.ListRepresentativesAsync(company, ct), ct);
    /// <summary>
    /// A representative's optional user link must name an ACTIVE user with an active membership of this company (system database; there is no cross-database foreign key).
    /// On update, an unchanged link is not re-checked, so a representative whose user was later deactivated or removed stays editable and deactivatable.
    /// </summary>
    private async Task<IReadOnlyList<FieldError>?> CheckUserLinkAsync(Guid company, Guid? userId, Guid? unchangedFrom, CancellationToken ct)
    {
        if (userId is null || userId == unchangedFrom) return null;
        var companies = await access.ListAccessibleCompaniesAsync(userId.Value, ct);
        return companies.Contains(company) ? null : [new FieldError("userId", "user_not_member")];
    }

    public async Task<ConfigResult<SalesRepresentativeView>> CreateRepresentativeAsync(Guid user, Guid company, SalesRepresentativeInput? input, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.SalesRepsConfigure, ct)) return Denied<SalesRepresentativeView>();
        if (await CheckUserLinkAsync(company, input?.UserId, null, ct) is { } bad) return ConfigResult<SalesRepresentativeView>.Invalid(bad);
        return await roles.CreateRepresentativeAsync(user, company, input, ct);
    }

    public async Task<ConfigResult<SalesRepresentativeView>> UpdateRepresentativeAsync(Guid user, Guid company, Guid id, long? revision, SalesRepresentativeInput? input, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.SalesRepsConfigure, ct)) return Denied<SalesRepresentativeView>();
        if (input?.UserId is { } linked)
        {
            var current = (await roles.ListRepresentativesAsync(company, ct)).FirstOrDefault(r => r.Id == id)?.UserId;
            if (await CheckUserLinkAsync(company, linked, current, ct) is { } bad) return ConfigResult<SalesRepresentativeView>.Invalid(bad);
        }
        return await roles.UpdateRepresentativeAsync(user, company, id, revision, input, ct);
    }
}
