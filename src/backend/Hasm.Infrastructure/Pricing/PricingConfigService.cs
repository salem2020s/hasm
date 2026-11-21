using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using PV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PricingPolicyVersion;
using PL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceList;
using PE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceEntry;
using DR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DiscountRule;
using CG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CustomerGroup;
using PLG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceListGroupScope;
using DRG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DiscountRuleGroupScope;
using Itm = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Item;
using CGM = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CustomerGroupMember;
using PartyRoleTable = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRole;

namespace Hasm.Infrastructure.Pricing;

/// <summary>
/// The audited company-side store of the pricing configuration (Phase 3.11 slice 2b): the versioned pricing policy (scales, margin control and the company-configurable precedences of owner decision P),
/// price lists with their entries, and percentage discount rules. <b>No authorization here by design:</b> <see cref="PricingManagementService"/> wraps it. Every write is one company transaction with its
/// audit event; lists, entries and rules advance by revision-fenced updates and are never removed (PostgreSQL refuses it); a policy change appends a version. Nothing here touches a document: a final
/// document stores an immutable <see cref="PricingSnapshot"/>, so no change of this configuration can re-price it. <see cref="QuoteAsync"/> feeds the pure <see cref="PricingEngine"/> from these rows.
/// Customer groups (company migration 0027, PricingConfigService.Groups.cs): the group master, the one-group-per-customer assignment and the group scope of lists and rules; a quote derives the customer group from the customer.
/// </summary>
public sealed partial class PricingConfigService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    private sealed class Abort(ConfigStatus status, string code, IReadOnlyList<FieldError>? fields = null, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => status == ConfigStatus.Invalid && fields is not null ? ConfigResult<T>.Invalid(fields) : ConfigResult<T>.Fail(status, code, currentRevision);
    }

    private static Abort Invalid(string field, string code) => new(ConfigStatus.Invalid, code, [new FieldError(field, code)]);

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,29}$")] private static partial Regex CodePattern();
    [GeneratedRegex("^[A-Z]{3}$")] private static partial Regex CurrencyPattern();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static object Db<T>(T? v) where T : class => (object?)v ?? DBNull.Value;
    private static object Db<T>(T? v, bool _) where T : struct => v is null ? DBNull.Value : v.Value;

    private static NpgsqlParameter Uuid(string name, Guid? v) => new(name, NpgsqlDbType.Uuid) { Value = v is { } g ? g : DBNull.Value };
    private static NpgsqlParameter Date(string name, DateOnly? v) => new(name, NpgsqlDbType.Date) { Value = v is { } d ? d : DBNull.Value };

    private async Task<ConfigResult<T>> Mutate<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort a) { return a.To<T>(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is PL.UqCode or DR.UqCode or CG.UqCode) { return ConfigResult<T>.Invalid([new FieldError("code", "code_taken")]); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is DR.UqActiveKey) { return ConfigResult<T>.Invalid([new FieldError("priority", "ambiguous_rule")]); }   // an active rule with the same scope, priority, minimum quantity and start already exists
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is PE.UqItemTier or PE.UqGroupTier) { return ConfigResult<T>.Invalid([new FieldError("minQuantity", "tier_exists")]); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is CGM.Pk) { return ConfigResult<T>.Fail(ConfigStatus.PreconditionFailed, "revision_conflict"); }   // a racing first assignment of the same customer
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is PV.UqVersion) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "version_conflict"); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return ConfigResult<T>.Invalid([new FieldError("reference", "reference_not_found")]); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException) { return ConfigResult<T>.Fail(ConfigStatus.Conflict, "invariant_violation"); }   // a database guard refused a racing or inconsistent change: nothing was written
    }

    /// <summary>A price list or discount rule for a customer needs the party to HAVE a customer role (SRS 8.1: prices by customer); a later deactivation of the role does not invalidate it.</summary>
    private static async Task RequireCustomerRole(CompanyAuditedWork work, Guid? partyId, CancellationToken ct)
    {
        if (partyId is not { } p) return;
        await using var q = new NpgsqlCommand($"SELECT 1 FROM {PartyRoleTable.Table} WHERE {PartyRoleTable.PartyId} = @p AND {PartyRoleTable.Role} = 1 FOR SHARE", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("p", p);
        if (await q.ExecuteScalarAsync(ct) is null) throw Invalid("customerId", "customer_role_required");
    }

    // ================================================================================================================ policy

    private static PricingPolicy DefaultPolicy => new(2, 2);

    private static async Task<PricingPolicyView?> LoadPolicy(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {PV.Version}, {PV.PriceDecimals}, {PV.MoneyDecimals}, {PV.MinMarginPercent}, {PV.MarginAction}, {PV.ListPrecedence}, {PV.DiscountPrecedence}, {PV.CreatedAtUtc} FROM {PV.Table} ORDER BY {PV.Version} DESC LIMIT 1", conn, tx);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new PricingPolicyView(r.GetInt32(0), false, new PricingPolicy(r.GetInt16(1), r.GetInt16(2), r.IsDBNull(3) ? null : r.GetDecimal(3), (MarginAction)r.GetInt16(4), (PriceListPrecedence)r.GetInt16(5), (DiscountPrecedence)r.GetInt16(6)), r.GetFieldValue<DateTimeOffset>(7));
    }

    /// <summary>The current policy, or the defaults of slice 1 (version 0) when none was ever saved: nothing is activated silently.</summary>
    public async Task<PricingPolicyView> GetPolicyAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadPolicy(conn, null, ct) ?? new PricingPolicyView(0, true, DefaultPolicy, null);
    }

    /// <summary>Appends the next policy version. <paramref name="expectedVersion"/> is the version the caller looked at (0 when none was saved): a stale one is refused with the current version.</summary>
    public async Task<ConfigResult<PricingPolicyView>> SavePolicyAsync(Guid actor, Guid companyId, long? expectedVersion, PricingPolicyInput? input, CancellationToken ct = default)
    {
        if (expectedVersion is null) return ConfigResult<PricingPolicyView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<PricingPolicyView>.Invalid([new FieldError("input", "required")]);
        var errors = new List<FieldError>();
        if (input.PriceDecimals is null) errors.Add(new FieldError("priceDecimals", "required"));
        if (input.MoneyDecimals is null) errors.Add(new FieldError("moneyDecimals", "required"));
        if (input.MarginAction is null) errors.Add(new FieldError("marginAction", "required"));
        if (input.ListPrecedence is null) errors.Add(new FieldError("listPrecedence", "required"));
        if (input.DiscountPrecedence is null) errors.Add(new FieldError("discountPrecedence", "required"));
        if (errors.Count > 0) return ConfigResult<PricingPolicyView>.Invalid(errors);
        var policy = new PricingPolicy(input.PriceDecimals!.Value, input.MoneyDecimals!.Value, input.MinMarginPercent, (MarginAction)input.MarginAction!.Value, (PriceListPrecedence)input.ListPrecedence!.Value, (DiscountPrecedence)input.DiscountPrecedence!.Value);
        if (input.PriceDecimals is < 0 or > PricingEngine.MaxDecimals) errors.Add(new FieldError("priceDecimals", "out_of_range"));
        if (input.MoneyDecimals is < 0 or > PricingEngine.MaxDecimals) errors.Add(new FieldError("moneyDecimals", "out_of_range"));
        if (input.MinMarginPercent is { } m && (m < -1000m || m > 100m || decimal.Round(m, 4) != m)) errors.Add(new FieldError("minMarginPercent", "out_of_range"));
        if (!Enum.IsDefined(policy.Action)) errors.Add(new FieldError("marginAction", "invalid_value"));
        if (!Enum.IsDefined(policy.ListPrecedence)) errors.Add(new FieldError("listPrecedence", "invalid_value"));
        if (!Enum.IsDefined(policy.DiscountPrecedence)) errors.Add(new FieldError("discountPrecedence", "invalid_value"));
        if (errors.Count > 0) return ConfigResult<PricingPolicyView>.Invalid(errors);
        var r = await Mutate(companyId, async work =>
        {
            var cur = await LoadPolicy(work.Connection, work.Transaction, ct);
            var version = cur?.Version ?? 0;
            if (version != expectedVersion) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, version);
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {PV.Table} ({PV.Id}, {PV.Version}, {PV.PriceDecimals}, {PV.MoneyDecimals}, {PV.MinMarginPercent}, {PV.MarginAction}, {PV.ListPrecedence}, {PV.DiscountPrecedence}, {PV.CreatedBy}, {PV.CreatedAtUtc}) " +
                "VALUES (@id, @v, @pd, @md, @mm, @ma, @lp, @dp, @by, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", Guid.NewGuid()); ins.Parameters.AddWithValue("v", version + 1); ins.Parameters.AddWithValue("pd", (short)policy.PriceDecimals); ins.Parameters.AddWithValue("md", (short)policy.MoneyDecimals);
                ins.Parameters.Add(new NpgsqlParameter("mm", NpgsqlDbType.Numeric) { Value = policy.MinMarginPercent is { } mm ? mm : DBNull.Value });
                ins.Parameters.AddWithValue("ma", (short)policy.Action); ins.Parameters.AddWithValue("lp", (short)policy.ListPrecedence); ins.Parameters.AddWithValue("dp", (short)policy.DiscountPrecedence);
                ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PricingAuditEvents.PolicySaved, ActorUserId: actor,
                Detail: $"pricing policy saved v{version + 1} priceDecimals={policy.PriceDecimals} moneyDecimals={policy.MoneyDecimals} minMargin={policy.MinMarginPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} action={(int)policy.Action} listPrecedence={(int)policy.ListPrecedence} discountPrecedence={(int)policy.DiscountPrecedence}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<PricingPolicyView>.Ok(await GetPolicyAsync(companyId, ct)) : ConfigResult<PricingPolicyView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
    }

    // ================================================================================================================ price lists

    private const string ListCols = $"{PL.Id}, {PL.Code}, {PL.NameAr}, {PL.NameEn}, {PL.Priority}, {PL.Currency}, {PL.ValidFrom}, {PL.ValidTo}, {PL.IsActive}, {PL.CustomerId}, {PL.Revision}, {PL.UpdatedAtUtc}, {PLG.CustomerGroupId}";

    private static PriceListView ReadList(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetFieldValue<DateOnly>(6),
        r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.GetInt64(10), r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetGuid(12));

    private static async Task<List<PriceListView>> LoadLists(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var rows = new List<PriceListView>();
        await using var q = new NpgsqlCommand($"SELECT {ListCols} FROM {PL.Table}{(id is null ? "" : $" WHERE {PL.Id} = @id")} ORDER BY {PL.Code}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        if (id is { } g) q.Parameters.AddWithValue("id", g);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(ReadList(r));
        return rows;
    }

    public async Task<IReadOnlyList<PriceListView>> ListPriceListsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadLists(conn, null, null, false, ct);
    }

    public async Task<PriceListView?> GetPriceListAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await LoadLists(conn, null, id, false, ct)).SingleOrDefault();
    }

    private static List<FieldError> CheckNames(string? nameAr, string? nameEn, out string ar, out string? en)
    {
        var errors = new List<FieldError>();
        ar = nameAr?.Trim() ?? "";
        if (ar.Length is < 1 or > 200 || ar.Any(char.IsControl)) errors.Add(new FieldError("nameAr", "name_invalid"));
        en = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim();
        if (en is { Length: > 200 } || en?.Any(char.IsControl) == true) errors.Add(new FieldError("nameEn", "name_invalid"));
        return errors;
    }

    private static void CheckWindow(DateOnly? from, DateOnly? to, List<FieldError> errors)
    {
        if (from is null) errors.Add(new FieldError("validFrom", "required"));
        else if (to is { } t && t < from) errors.Add(new FieldError("validTo", "before_valid_from"));
    }

    private static void CheckPriority(int? priority, List<FieldError> errors)
    {
        if (priority is null) errors.Add(new FieldError("priority", "required"));
        else if (priority is < 0 or > 1_000_000) errors.Add(new FieldError("priority", "out_of_range"));
    }

    public async Task<ConfigResult<PriceListView>> CreatePriceListAsync(Guid actor, Guid companyId, PriceListInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<PriceListView>.Invalid([new FieldError("input", "required")]);
        var errors = CheckNames(input.NameAr, input.NameEn, out var ar, out var en);
        var code = input.Code?.Trim().ToUpperInvariant() ?? "";
        if (!CodePattern().IsMatch(code)) errors.Add(new FieldError("code", "code_invalid"));
        var currency = input.Currency?.Trim().ToUpperInvariant() ?? "";
        if (!CurrencyPattern().IsMatch(currency)) errors.Add(new FieldError("currency", "currency_invalid"));
        CheckPriority(input.Priority, errors); CheckWindow(input.ValidFrom, input.ValidTo, errors);
        if (input.CustomerId is not null && input.CustomerGroupId is not null) errors.Add(new FieldError("customerGroupId", "customer_or_group_not_both"));
        if (errors.Count > 0) return ConfigResult<PriceListView>.Invalid(errors);
        var id = Guid.NewGuid();
        var r = await Mutate(companyId, async work =>
        {
            await RequireCustomerRole(work, input.CustomerId, ct);
            await RequireActiveGroup(work, input.CustomerGroupId, ct);
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {PL.Table} ({PL.Id}, {PL.Code}, {PL.NameAr}, {PL.NameEn}, {PL.Priority}, {PL.Currency}, {PL.ValidFrom}, {PL.ValidTo}, {PL.IsActive}, {PL.CustomerId}, {PLG.CustomerGroupId}, {PL.Revision}, {PL.CreatedBy}, {PL.CreatedAtUtc}, {PL.UpdatedAtUtc}) " +
                "VALUES (@id, @c, @ar, @en, @p, @cur, @vf, @vt, @a, @cu, @cg, 1, @by, @now, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("c", code); ins.Parameters.AddWithValue("ar", ar); ins.Parameters.AddWithValue("en", Db(en)); ins.Parameters.AddWithValue("p", input.Priority!.Value);
                ins.Parameters.AddWithValue("cur", currency); ins.Parameters.Add(Date("vf", input.ValidFrom)); ins.Parameters.Add(Date("vt", input.ValidTo)); ins.Parameters.AddWithValue("a", input.IsActive ?? true);
                ins.Parameters.Add(Uuid("cu", input.CustomerId)); ins.Parameters.Add(Uuid("cg", input.CustomerGroupId)); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PricingAuditEvents.PriceListCreated, ActorUserId: actor, SubjectId: id, Detail: $"price list created code={code} currency={currency} customer={(input.CustomerId is null ? "none" : "set")} group={(input.CustomerGroupId is null ? "none" : "set")}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<PriceListView>.Ok((await GetPriceListAsync(companyId, id, ct))!) : ConfigResult<PriceListView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
    }

    /// <summary>Changes priority, dates, names and the active flag. Code, currency and customer never change (a different one is a new list).</summary>
    public async Task<ConfigResult<PriceListView>> UpdatePriceListAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, PriceListInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<PriceListView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<PriceListView>.Invalid([new FieldError("input", "required")]);
        var errors = CheckNames(input.NameAr, input.NameEn, out var ar, out var en);
        CheckPriority(input.Priority, errors); CheckWindow(input.ValidFrom, input.ValidTo, errors);
        if (errors.Count > 0) return ConfigResult<PriceListView>.Invalid(errors);
        var r = await Mutate(companyId, async work =>
        {
            var cur = (await LoadLists(work.Connection, work.Transaction, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "price_list_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (input.Code is { } c && !string.Equals(c.Trim(), cur.Code, StringComparison.OrdinalIgnoreCase)) throw Invalid("code", "code_immutable");
            if (input.Currency is { } cu && !string.Equals(cu.Trim(), cur.Currency, StringComparison.OrdinalIgnoreCase)) throw Invalid("currency", "currency_immutable");
            if (input.CustomerId is { } cid && cid != cur.CustomerId) throw Invalid("customerId", "customer_immutable");
            if (input.CustomerGroupId is { } cgid && cgid != cur.CustomerGroupId) throw Invalid("customerGroupId", "customer_group_immutable");
            await using (var upd = new NpgsqlCommand($"UPDATE {PL.Table} SET {PL.NameAr} = @ar, {PL.NameEn} = @en, {PL.Priority} = @p, {PL.ValidFrom} = @vf, {PL.ValidTo} = @vt, {PL.IsActive} = @a, {PL.Revision} = {PL.Revision} + 1, {PL.UpdatedAtUtc} = @now WHERE {PL.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("ar", ar); upd.Parameters.AddWithValue("en", Db(en)); upd.Parameters.AddWithValue("p", input.Priority!.Value);
                upd.Parameters.Add(Date("vf", input.ValidFrom)); upd.Parameters.Add(Date("vt", input.ValidTo)); upd.Parameters.AddWithValue("a", input.IsActive ?? cur.IsActive); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PricingAuditEvents.PriceListUpdated, ActorUserId: actor, SubjectId: id, Detail: $"price list updated r{cur.Revision + 1} code={cur.Code} priority={input.Priority} active={input.IsActive ?? cur.IsActive}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<PriceListView>.Ok((await GetPriceListAsync(companyId, id, ct))!) : ConfigResult<PriceListView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
    }

    // ================================================================================================================ price entries

    private const string EntryCols = $"{PE.Id}, {PE.PriceListId}, {PE.ItemId}, {PE.ItemGroupId}, {PE.UnitId}, {PE.MinQuantity}, {PE.UnitPrice}, {PE.IsActive}, {PE.Revision}, {PE.UpdatedAtUtc}";

    private static PriceEntryView ReadEntry(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetGuid(4), r.GetDecimal(5), r.GetDecimal(6), r.GetBoolean(7), r.GetInt64(8), r.GetFieldValue<DateTimeOffset>(9));

    private static async Task<List<PriceEntryView>> LoadEntries(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? listId, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var rows = new List<PriceEntryView>();
        var where = id is not null ? $" WHERE {PE.Id} = @id" : listId is not null ? $" WHERE {PE.PriceListId} = @l" : "";
        await using var q = new NpgsqlCommand($"SELECT {EntryCols} FROM {PE.Table}{where} ORDER BY {PE.PriceListId}, {PE.UnitId}, {PE.MinQuantity}, {PE.Id}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        if (id is { } g) q.Parameters.AddWithValue("id", g);
        if (id is null && listId is { } l) q.Parameters.AddWithValue("l", l);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(ReadEntry(r));
        return rows;
    }

    public async Task<IReadOnlyList<PriceEntryView>> ListEntriesAsync(Guid companyId, Guid priceListId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadEntries(conn, null, priceListId, null, false, ct);
    }

    public async Task<PriceEntryView?> GetEntryAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await LoadEntries(conn, null, null, id, false, ct)).SingleOrDefault();
    }

    private static void CheckPrice(decimal? price, List<FieldError> errors)
    {
        if (price is null) errors.Add(new FieldError("unitPrice", "required"));
        else if (price < 0 || price > 1_000_000_000_000m || decimal.Round(price.Value, 6) != price) errors.Add(new FieldError("unitPrice", "out_of_range"));
    }

    private static void CheckQuantity(decimal? q, string field, List<FieldError> errors, bool required)
    {
        if (q is null) { if (required) errors.Add(new FieldError(field, "required")); }
        else if (q < 0 || q >= 1_000_000_000m || decimal.Round(q.Value, 10) != q) errors.Add(new FieldError(field, "out_of_range"));
    }

    public async Task<ConfigResult<PriceEntryView>> CreateEntryAsync(Guid actor, Guid companyId, Guid priceListId, PriceEntryInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<PriceEntryView>.Invalid([new FieldError("input", "required")]);
        var errors = new List<FieldError>();
        if ((input.ItemId is null) == (input.ItemGroupId is null)) errors.Add(new FieldError("itemId", "item_or_group_required"));
        if (input.UnitId is null) errors.Add(new FieldError("unitId", "required"));
        CheckQuantity(input.MinQuantity, "minQuantity", errors, required: true); CheckPrice(input.UnitPrice, errors);
        if (errors.Count > 0) return ConfigResult<PriceEntryView>.Invalid(errors);
        var id = Guid.NewGuid();
        var r = await Mutate(companyId, async work =>
        {
            _ = (await LoadLists(work.Connection, work.Transaction, priceListId, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "price_list_not_found");   // locks the list: entries of one list are written one at a time
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {PE.Table} ({PE.Id}, {PE.PriceListId}, {PE.ItemId}, {PE.ItemGroupId}, {PE.UnitId}, {PE.MinQuantity}, {PE.UnitPrice}, {PE.IsActive}, {PE.Revision}, {PE.CreatedBy}, {PE.CreatedAtUtc}, {PE.UpdatedAtUtc}) " +
                "VALUES (@id, @l, @i, @g, @u, @q, @p, @a, 1, @by, @now, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("l", priceListId); ins.Parameters.Add(Uuid("i", input.ItemId)); ins.Parameters.Add(Uuid("g", input.ItemGroupId)); ins.Parameters.AddWithValue("u", input.UnitId!.Value);
                ins.Parameters.AddWithValue("q", input.MinQuantity!.Value); ins.Parameters.AddWithValue("p", input.UnitPrice!.Value); ins.Parameters.AddWithValue("a", input.IsActive ?? true); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PricingAuditEvents.PriceEntryCreated, ActorUserId: actor, SubjectId: id, Detail: $"price entry created list={priceListId:N} tier={input.MinQuantity!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} price={input.UnitPrice!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<PriceEntryView>.Ok((await GetEntryAsync(companyId, id, ct))!) : ConfigResult<PriceEntryView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
    }

    /// <summary>Changes the price and the active flag only; the item or group, unit and tier identify the entry and never change (a different tier is a new entry).</summary>
    public async Task<ConfigResult<PriceEntryView>> UpdateEntryAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, PriceEntryInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<PriceEntryView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<PriceEntryView>.Invalid([new FieldError("input", "required")]);
        var errors = new List<FieldError>();
        CheckPrice(input.UnitPrice, errors);
        if (errors.Count > 0) return ConfigResult<PriceEntryView>.Invalid(errors);
        var r = await Mutate(companyId, async work =>
        {
            var cur = (await LoadEntries(work.Connection, work.Transaction, null, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "price_entry_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (input.ItemId is { } i && i != cur.ItemId || input.ItemGroupId is { } g && g != cur.ItemGroupId) throw Invalid("itemId", "scope_immutable");
            if (input.UnitId is { } u && u != cur.UnitId) throw Invalid("unitId", "unit_immutable");
            if (input.MinQuantity is { } q && q != cur.MinQuantity) throw Invalid("minQuantity", "tier_immutable");
            await using (var upd = new NpgsqlCommand($"UPDATE {PE.Table} SET {PE.UnitPrice} = @p, {PE.IsActive} = @a, {PE.Revision} = {PE.Revision} + 1, {PE.UpdatedAtUtc} = @now WHERE {PE.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("p", input.UnitPrice!.Value); upd.Parameters.AddWithValue("a", input.IsActive ?? cur.IsActive); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PricingAuditEvents.PriceEntryUpdated, ActorUserId: actor, SubjectId: id, Detail: $"price entry updated r{cur.Revision + 1} list={cur.PriceListId:N} price={input.UnitPrice!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} active={input.IsActive ?? cur.IsActive}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<PriceEntryView>.Ok((await GetEntryAsync(companyId, id, ct))!) : ConfigResult<PriceEntryView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
    }

    // ================================================================================================================ discount rules

    private const string DiscountCols = $"{DR.Id}, {DR.Code}, {DR.NameAr}, {DR.NameEn}, {DR.Priority}, {DR.ValidFrom}, {DR.ValidTo}, {DR.IsActive}, {DR.Percent}, {DR.MinQuantity}, {DR.CustomerId}, {DR.ItemId}, {DR.ItemGroupId}, {DR.Revision}, {DR.UpdatedAtUtc}, {DRG.CustomerGroupId}";

    private static DiscountRuleView ReadDiscount(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4), r.GetFieldValue<DateOnly>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6),
        r.GetBoolean(7), r.GetDecimal(8), r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? null : r.GetGuid(12), r.GetInt64(13), r.GetFieldValue<DateTimeOffset>(14), r.IsDBNull(15) ? null : r.GetGuid(15));

    private static async Task<List<DiscountRuleView>> LoadDiscounts(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var rows = new List<DiscountRuleView>();
        await using var q = new NpgsqlCommand($"SELECT {DiscountCols} FROM {DR.Table}{(id is null ? "" : $" WHERE {DR.Id} = @id")} ORDER BY {DR.Code}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        if (id is { } g) q.Parameters.AddWithValue("id", g);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(ReadDiscount(r));
        return rows;
    }

    public async Task<IReadOnlyList<DiscountRuleView>> ListDiscountsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadDiscounts(conn, null, null, false, ct);
    }

    public async Task<DiscountRuleView?> GetDiscountAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await LoadDiscounts(conn, null, id, false, ct)).SingleOrDefault();
    }

    private static void CheckPercent(decimal? p, List<FieldError> errors)
    {
        if (p is null) errors.Add(new FieldError("percent", "required"));
        else if (p < 0 || p > 100 || decimal.Round(p.Value, 4) != p) errors.Add(new FieldError("percent", "out_of_range"));
    }

    public async Task<ConfigResult<DiscountRuleView>> CreateDiscountAsync(Guid actor, Guid companyId, DiscountRuleInput? input, CancellationToken ct = default)
    {
        if (input is null) return ConfigResult<DiscountRuleView>.Invalid([new FieldError("input", "required")]);
        var errors = CheckNames(input.NameAr, input.NameEn, out var ar, out var en);
        var code = input.Code?.Trim().ToUpperInvariant() ?? "";
        if (!CodePattern().IsMatch(code)) errors.Add(new FieldError("code", "code_invalid"));
        CheckPriority(input.Priority, errors); CheckWindow(input.ValidFrom, input.ValidTo, errors); CheckPercent(input.Percent, errors); CheckQuantity(input.MinQuantity, "minQuantity", errors, required: false);
        if (input.ItemId is not null && input.ItemGroupId is not null) errors.Add(new FieldError("itemId", "item_or_group_not_both"));
        if (input.CustomerId is not null && input.CustomerGroupId is not null) errors.Add(new FieldError("customerGroupId", "customer_or_group_not_both"));
        if (errors.Count > 0) return ConfigResult<DiscountRuleView>.Invalid(errors);
        var id = Guid.NewGuid();
        var r = await Mutate(companyId, async work =>
        {
            await RequireCustomerRole(work, input.CustomerId, ct);
            await RequireActiveGroup(work, input.CustomerGroupId, ct);
            var now = Now;
            await using (var ins = new NpgsqlCommand($"INSERT INTO {DR.Table} ({DR.Id}, {DR.Code}, {DR.NameAr}, {DR.NameEn}, {DR.Priority}, {DR.ValidFrom}, {DR.ValidTo}, {DR.IsActive}, {DR.Percent}, {DR.MinQuantity}, {DR.CustomerId}, {DRG.CustomerGroupId}, {DR.ItemId}, {DR.ItemGroupId}, {DR.Revision}, {DR.CreatedBy}, {DR.CreatedAtUtc}, {DR.UpdatedAtUtc}) " +
                "VALUES (@id, @c, @ar, @en, @p, @vf, @vt, @a, @pc, @mq, @cu, @cg, @i, @g, 1, @by, @now, @now)", work.Connection, work.Transaction))
            {
                ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("c", code); ins.Parameters.AddWithValue("ar", ar); ins.Parameters.AddWithValue("en", Db(en)); ins.Parameters.AddWithValue("p", input.Priority!.Value);
                ins.Parameters.Add(Date("vf", input.ValidFrom)); ins.Parameters.Add(Date("vt", input.ValidTo)); ins.Parameters.AddWithValue("a", input.IsActive ?? true); ins.Parameters.AddWithValue("pc", input.Percent!.Value); ins.Parameters.AddWithValue("mq", input.MinQuantity ?? 0m);
                ins.Parameters.Add(Uuid("cu", input.CustomerId)); ins.Parameters.Add(Uuid("cg", input.CustomerGroupId)); ins.Parameters.Add(Uuid("i", input.ItemId)); ins.Parameters.Add(Uuid("g", input.ItemGroupId)); ins.Parameters.AddWithValue("by", actor); ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PricingAuditEvents.DiscountCreated, ActorUserId: actor, SubjectId: id, Detail: $"discount rule created code={code} percent={input.Percent!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<DiscountRuleView>.Ok((await GetDiscountAsync(companyId, id, ct))!) : ConfigResult<DiscountRuleView>.Fail(r.Status, r.Code!) with { Fields = r.Fields };
    }

    /// <summary>Changes names, priority, dates, percentage, minimum quantity and the active flag. Code and scope never change.</summary>
    public async Task<ConfigResult<DiscountRuleView>> UpdateDiscountAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, DiscountRuleInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<DiscountRuleView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<DiscountRuleView>.Invalid([new FieldError("input", "required")]);
        var errors = CheckNames(input.NameAr, input.NameEn, out var ar, out var en);
        CheckPriority(input.Priority, errors); CheckWindow(input.ValidFrom, input.ValidTo, errors); CheckPercent(input.Percent, errors); CheckQuantity(input.MinQuantity, "minQuantity", errors, required: false);
        if (errors.Count > 0) return ConfigResult<DiscountRuleView>.Invalid(errors);
        var r = await Mutate(companyId, async work =>
        {
            var cur = (await LoadDiscounts(work.Connection, work.Transaction, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "discount_not_found");
            if (cur.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, cur.Revision);
            if (input.Code is { } c && !string.Equals(c.Trim(), cur.Code, StringComparison.OrdinalIgnoreCase)) throw Invalid("code", "code_immutable");
            if (input.CustomerId is { } cu && cu != cur.CustomerId || input.CustomerGroupId is { } cg2 && cg2 != cur.CustomerGroupId || input.ItemId is { } i && i != cur.ItemId || input.ItemGroupId is { } g && g != cur.ItemGroupId) throw Invalid("scope", "scope_immutable");
            await using (var upd = new NpgsqlCommand($"UPDATE {DR.Table} SET {DR.NameAr} = @ar, {DR.NameEn} = @en, {DR.Priority} = @p, {DR.ValidFrom} = @vf, {DR.ValidTo} = @vt, {DR.IsActive} = @a, {DR.Percent} = @pc, {DR.MinQuantity} = @mq, {DR.Revision} = {DR.Revision} + 1, {DR.UpdatedAtUtc} = @now WHERE {DR.Id} = @id", work.Connection, work.Transaction))
            {
                upd.Parameters.AddWithValue("id", id); upd.Parameters.AddWithValue("ar", ar); upd.Parameters.AddWithValue("en", Db(en)); upd.Parameters.AddWithValue("p", input.Priority!.Value);
                upd.Parameters.Add(Date("vf", input.ValidFrom)); upd.Parameters.Add(Date("vt", input.ValidTo)); upd.Parameters.AddWithValue("a", input.IsActive ?? cur.IsActive); upd.Parameters.AddWithValue("pc", input.Percent!.Value);
                upd.Parameters.AddWithValue("mq", input.MinQuantity ?? cur.MinQuantity); upd.Parameters.AddWithValue("now", Now);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await work.AppendAuditAsync(new AuditEventData(PricingAuditEvents.DiscountUpdated, ActorUserId: actor, SubjectId: id, Detail: $"discount rule updated r{cur.Revision + 1} code={cur.Code} percent={input.Percent!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} active={input.IsActive ?? cur.IsActive}"), ct);
            return true;
        }, ct);
        return r.IsOk ? ConfigResult<DiscountRuleView>.Ok((await GetDiscountAsync(companyId, id, ct))!) : ConfigResult<DiscountRuleView>.Fail(r.Status, r.Code!, r.CurrentRevision) with { Fields = r.Fields };
    }

    // ================================================================================================================ quote

    /// <summary>
    /// Prices one line from the CURRENT configuration in one repeatable-read snapshot: loads the policy and the ACTIVE lists, entries and rules, runs the pure engine under the company's policy and seals the
    /// result as an immutable <see cref="PricingSnapshot"/>. Nothing is stored (a document stores the snapshot it receives). Invalid or ambiguous configuration data is refused, never guessed.
    /// </summary>
    public async Task<ConfigResult<PricingSnapshot>> QuoteAsync(Guid companyId, PricingRequest? request, PriceOverride? over, CancellationToken ct = default)
    {
        if (request is null) return ConfigResult<PricingSnapshot>.Invalid([new FieldError("request", "required")]);
        try
        {
            await using var conn = await resolver.OpenAsync(companyId, ct);
            await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            var policy = (await LoadPolicy(conn, tx, ct))?.Policy ?? DefaultPolicy;
            var lists = (await LoadLists(conn, tx, null, false, ct)).Where(l => l.IsActive).ToList();
            var ids = lists.Select(l => l.Id).ToHashSet();
            var entries = new List<PriceEntryView>();
            foreach (var l in lists) entries.AddRange((await LoadEntries(conn, tx, l.Id, null, false, ct)).Where(e => e.IsActive));
            var discounts = (await LoadDiscounts(conn, tx, null, false, ct)).Where(d => d.IsActive).ToList();
            // the server, not the caller, decides the item group and the customer group that price the line
            Guid? itemGroup;
            await using (var iq = new NpgsqlCommand($"SELECT {Itm.GroupId} FROM {Itm.Table} WHERE {Itm.Id} = @i", conn, tx))
            {
                iq.Parameters.AddWithValue("i", request.ItemId);
                var v = await iq.ExecuteScalarAsync(ct);
                if (v is null) return ConfigResult<PricingSnapshot>.Invalid([new FieldError("itemId", "item_not_found")]);
                itemGroup = v is DBNull ? null : (Guid)v;
            }
            Guid? customerGroup = null;   // never taken from the caller: only the stored assignment of the customer decides
            if (request.CustomerId is { } cust)
            {
                await using var gq = new NpgsqlCommand($"SELECT {CGM.CustomerGroupId} FROM {CGM.Table} WHERE {CGM.PartyId} = @p", conn, tx);
                gq.Parameters.AddWithValue("p", cust);
                var assigned = await gq.ExecuteScalarAsync(ct);
                Guid? assignedGroup = assigned is null or DBNull ? null : (Guid)assigned;
                customerGroup = assignedGroup;
            }
            await tx.CommitAsync(ct);
            request = request with { ItemGroupId = itemGroup, CustomerGroupId = customerGroup };
            var resolved = PricingEngine.Resolve(request,
                [.. lists.Select(l => new PriceListRecord(l.Id, l.Code, l.Priority, l.Currency, l.ValidFrom, l.ValidTo, l.IsActive, l.CustomerId, l.CustomerGroupId))],
                [.. entries.Where(e => ids.Contains(e.PriceListId)).Select(e => new PriceEntryRecord(e.PriceListId, e.ItemId, e.ItemGroupId, e.UnitId, e.MinQuantity, e.UnitPrice))],
                [.. discounts.Select(d => new DiscountRuleRecord(d.Id, d.Code, d.Priority, d.ValidFrom, d.ValidTo, d.IsActive, d.Percent, d.MinQuantity, d.CustomerId, d.CustomerGroupId, d.ItemId, d.ItemGroupId))], policy);
            return ConfigResult<PricingSnapshot>.Ok(PricingSnapshots.Create(policy, request, resolved, over));
        }
        catch (PricingException ex) { return ConfigResult<PricingSnapshot>.Invalid([new FieldError("pricing", ex.Code)]); }
    }
}

/// <summary>Permission-checked entry: <c>company.pricing.view</c> reads and quotes, <c>company.pricing.configure</c> writes, <c>company.pricing.override</c> additionally allows a manual price or discount override in a quote (company-wide).</summary>
public sealed class PricingManagementService(AccessService access, PricingConfigService pricing)
{
    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");
    private Task<bool> Can(Guid user, Guid company, string code, CancellationToken ct) => access.IsAllowedAsync(user, company, code, ScopeTarget.Company, ct);
    private async Task<ConfigResult<T>> Read<T>(Guid user, Guid company, Func<Task<T>> read, CancellationToken ct) =>
        await Can(user, company, PermissionCatalog.Codes.PricingView, ct) ? ConfigResult<T>.Ok(await read()) : Denied<T>();
    private async Task<ConfigResult<T>> Write<T>(Guid user, Guid company, Func<Task<ConfigResult<T>>> write, CancellationToken ct) =>
        await Can(user, company, PermissionCatalog.Codes.PricingConfigure, ct) ? await write() : Denied<T>();

    public async Task<PricingCapabilities> CapabilitiesAsync(Guid user, Guid company, CancellationToken ct = default)
    {
        var eff = await access.GetEffectivePermissionsAsync(user, company, ct);
        return new PricingCapabilities(eff.HasCompanyWide(PermissionCatalog.Codes.PricingView), eff.HasCompanyWide(PermissionCatalog.Codes.PricingConfigure), eff.HasCompanyWide(PermissionCatalog.Codes.PricingOverride));
    }

    public Task<ConfigResult<PricingPolicyView>> GetPolicyAsync(Guid user, Guid company, CancellationToken ct = default) => Read(user, company, () => pricing.GetPolicyAsync(company, ct), ct);
    public Task<ConfigResult<IReadOnlyList<PriceListView>>> ListPriceListsAsync(Guid user, Guid company, CancellationToken ct = default) => Read(user, company, () => pricing.ListPriceListsAsync(company, ct), ct);
    public Task<ConfigResult<IReadOnlyList<PriceEntryView>>> ListEntriesAsync(Guid user, Guid company, Guid listId, CancellationToken ct = default) => Read(user, company, () => pricing.ListEntriesAsync(company, listId, ct), ct);
    public Task<ConfigResult<IReadOnlyList<DiscountRuleView>>> ListDiscountsAsync(Guid user, Guid company, CancellationToken ct = default) => Read(user, company, () => pricing.ListDiscountsAsync(company, ct), ct);

    public Task<ConfigResult<PricingPolicyView>> SavePolicyAsync(Guid user, Guid company, long? version, PricingPolicyInput? input, CancellationToken ct = default) => Write(user, company, () => pricing.SavePolicyAsync(user, company, version, input, ct), ct);
    public Task<ConfigResult<PriceListView>> CreatePriceListAsync(Guid user, Guid company, PriceListInput? input, CancellationToken ct = default) => Write(user, company, () => pricing.CreatePriceListAsync(user, company, input, ct), ct);
    public Task<ConfigResult<PriceListView>> UpdatePriceListAsync(Guid user, Guid company, Guid id, long? revision, PriceListInput? input, CancellationToken ct = default) => Write(user, company, () => pricing.UpdatePriceListAsync(user, company, id, revision, input, ct), ct);
    public Task<ConfigResult<PriceEntryView>> CreateEntryAsync(Guid user, Guid company, Guid listId, PriceEntryInput? input, CancellationToken ct = default) => Write(user, company, () => pricing.CreateEntryAsync(user, company, listId, input, ct), ct);
    public Task<ConfigResult<PriceEntryView>> UpdateEntryAsync(Guid user, Guid company, Guid id, long? revision, PriceEntryInput? input, CancellationToken ct = default) => Write(user, company, () => pricing.UpdateEntryAsync(user, company, id, revision, input, ct), ct);
    public Task<ConfigResult<DiscountRuleView>> CreateDiscountAsync(Guid user, Guid company, DiscountRuleInput? input, CancellationToken ct = default) => Write(user, company, () => pricing.CreateDiscountAsync(user, company, input, ct), ct);
    public Task<ConfigResult<DiscountRuleView>> UpdateDiscountAsync(Guid user, Guid company, Guid id, long? revision, DiscountRuleInput? input, CancellationToken ct = default) => Write(user, company, () => pricing.UpdateDiscountAsync(user, company, id, revision, input, ct), ct);
    public Task<ConfigResult<IReadOnlyList<CustomerGroupView>>> ListCustomerGroupsAsync(Guid user, Guid company, CancellationToken ct = default) => Read<IReadOnlyList<CustomerGroupView>>(user, company, () => pricing.ListCustomerGroupsAsync(company, ct), ct);
    /// <summary>The same permission (<c>company.pricing.view</c>) and company scoping as <see cref="ListCustomerGroupsAsync"/>; only an optional search and paging are added.</summary>
    public Task<ConfigResult<CustomerGroupPage>> SearchCustomerGroupsAsync(Guid user, Guid company, string? search, int? offset, int? limit, bool? active = null, CancellationToken ct = default) => Read<CustomerGroupPage>(user, company, () => pricing.SearchCustomerGroupsAsync(company, search, offset, limit, active, ct), ct);
    public Task<ConfigResult<IReadOnlyList<CustomerGroupAssignmentView>>> ListAssignmentsAsync(Guid user, Guid company, CancellationToken ct = default) => Read<IReadOnlyList<CustomerGroupAssignmentView>>(user, company, () => pricing.ListAssignmentsAsync(company, ct), ct);
    public Task<ConfigResult<CustomerGroupView>> CreateCustomerGroupAsync(Guid user, Guid company, CustomerGroupInput? input, CancellationToken ct = default) => Write(user, company, () => pricing.CreateCustomerGroupAsync(user, company, input, ct), ct);
    public Task<ConfigResult<CustomerGroupView>> UpdateCustomerGroupAsync(Guid user, Guid company, Guid id, long? revision, CustomerGroupInput? input, CancellationToken ct = default) => Write(user, company, () => pricing.UpdateCustomerGroupAsync(user, company, id, revision, input, ct), ct);
    public Task<ConfigResult<CustomerGroupAssignmentView>> SetAssignmentAsync(Guid user, Guid company, Guid partyId, long? revision, CustomerGroupAssignmentInput? input, CancellationToken ct = default) => Write(user, company, () => pricing.SetAssignmentAsync(user, company, partyId, revision, input, ct), ct);

    /// <summary>A quote needs the view permission; a manual override of the price or discount additionally needs <c>company.pricing.override</c> (the engine requires its reason).</summary>
    public async Task<ConfigResult<PricingSnapshot>> QuoteAsync(Guid user, Guid company, PricingRequest? request, PriceOverride? over, CancellationToken ct = default)
    {
        if (!await Can(user, company, PermissionCatalog.Codes.PricingView, ct)) return Denied<PricingSnapshot>();
        if (over is not null && !await Can(user, company, PermissionCatalog.Codes.PricingOverride, ct)) return Denied<PricingSnapshot>();
        return await pricing.QuoteAsync(company, request, over, ct);
    }
}
