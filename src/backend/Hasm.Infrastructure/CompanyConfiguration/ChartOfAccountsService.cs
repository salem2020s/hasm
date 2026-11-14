using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using A = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using AS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.AccountSettings;
using AH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.AccountRevision;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>A refusal by the Chart of Accounts domain rules raised inside a caller-owned unit of work (<see cref="ChartOfAccountsService.CreateInWorkAsync"/>): the stable <see cref="Code"/> and field errors.</summary>
public sealed class ChartOperationException(string code, IReadOnlyList<FieldError>? fields) : Exception(code)
{
    public string Code { get; } = code;
    public IReadOnlyList<FieldError>? Fields { get; } = fields;
}

/// <summary>
/// Chart of Accounts domain operations (Phase 3.2A). <b>No authorization here by design:</b> <see cref="ChartOfAccountsManagementService"/> wraps it.
/// The chart is the company's own tree of accounts (its own database, so no cross-company reference is possible). Every mutation runs in
/// <see cref="CompanyAuditedTransactions"/>: the change, its append-only history row and its authenticated audit event commit or roll back together, after
/// ONE transaction-level advisory lock that serialises all chart changes of the company (taken before the audit-stream lock, as the lock order requires).
/// PostgreSQL independently enforces the tree integrity (see migration 0007): cycles, parents that are not groups, type mismatches between parent and child,
/// active accounts under inactive parents, duplicate codes.
/// <list type="bullet">
/// <item>Posting is only ever allowed to an ACTIVE account that is not a group (<see cref="AccountView.IsPostable"/>); the future posting engine must check it.</item>
/// <item>Codes are not immutable: re-coding is its own audited operation (stable ids are what everything references).</item>
/// <item>Attributes that would invalidate posted history (nature, group flag, currency policy) change only while the account has no postings; once a financial
///   module reports history while the usage probe is a placeholder, those changes are refused (no silent bypass, as with the period close checks).</item>
/// <item>Creating, renaming or organizing accounts is master-data configuration: it never activates the base-currency or time-zone financial-history lock.</item>
/// </list>
/// </summary>
public sealed class ChartOfAccountsService(
    CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, AccountUsageGuard usage, FinancialHistoryGuard financialHistory, TimeProvider clock)
{
    internal const int LockKey1 = 0x48434F41;   // "HCOA": advisory-lock namespace of the chart of accounts
    internal const int LockKey2 = 1;

    private static readonly string Columns =
        $"a.{A.Id}, a.{A.Code}, a.{A.NameAr}, a.{A.NameEn}, a.{A.ParentId}, a.{A.Type}, a.{A.Nature}, a.{A.Statement}, a.{A.IsGroup}, a.{A.IsActive}, a.{A.CurrencyPolicy}, " +
        $"a.{A.CurrencyCode}, a.{A.TemplateKey}, a.{A.Revision}, a.{A.CreatedAtUtc}, a.{A.UpdatedAtUtc}, EXISTS (SELECT 1 FROM {A.Table} c WHERE c.{A.ParentId} = a.{A.Id})";

    public IReadOnlyList<string> UnimplementedUsageChecks => usage.UnimplementedDomains;

    private sealed class Abort(ConfigStatus status, string code, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, fields, currentRevision);
        public IReadOnlyList<FieldError>? Fields => fields;
    }

    // ------------------------------------------------------------------------------------------------------------- reads

    public async Task<IReadOnlyList<AccountView>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await LoadAsync(conn, null, null, false, ct);
    }

    public async Task<AccountView?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return (await LoadAsync(conn, null, id, false, ct)).SingleOrDefault();
    }

    public async Task<ChartSettingsView> GetSettingsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await ReadSettingsAsync(conn, null, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- mutations

    public async Task<ConfigResult<AccountView>> CreateAsync(Guid actor, Guid companyId, AccountInput? input, CancellationToken ct = default)
    {
        if (!ConfigurationValidation.TryNormalizeAccount(input, out var manualCode, out var nameAr, out var nameEn, out var givenType, out var givenNature, out var isGroup, out var policy, out var currency, out var errors))
            return ConfigResult<AccountView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            return await CreateCoreAsync(work, actor, input!, manualCode, nameAr, nameEn, givenType, givenNature, isGroup, policy, currency, ct);
        }, ct);
    }

    /// <summary>
    /// Creates an account INSIDE a unit of work the caller already runs (the import framework applies a whole approved batch in one audited transaction). The same validation, the same domain rules, the same
    /// history row and audit event as <see cref="CreateAsync"/>; the caller must have called <see cref="LockForImportAsync"/> first. A refusal is a <see cref="ChartOperationException"/> (the caller rolls back).
    /// </summary>
    public async Task<AccountView> CreateInWorkAsync(CompanyAuditedWork work, Guid actor, AccountInput input, CancellationToken ct = default)
    {
        if (!ConfigurationValidation.TryNormalizeAccount(input, out var manualCode, out var nameAr, out var nameEn, out var givenType, out var givenNature, out var isGroup, out var policy, out var currency, out var errors))
            throw new ChartOperationException("validation_failed", errors);
        try { return await CreateCoreAsync(work, actor, input, manualCode, nameAr, nameEn, givenType, givenNature, isGroup, policy, currency, ct); }
        catch (Abort abort) { throw new ChartOperationException(abort.Message, abort.Fields); }
    }

    /// <summary>The advisory lock every chart writer takes first (before the audit-stream lock).</summary>
    public Task LockForImportAsync(CompanyAuditedWork work, CancellationToken ct = default) => LockAsync(work, ct);

    private async Task<AccountView> CreateCoreAsync(CompanyAuditedWork work, Guid actor, AccountInput input, string? manualCode, string? nameAr, string? nameEn, AccountType? givenType, AccountNature? givenNature,
        bool isGroup, AccountCurrencyPolicy policy, string? currency, CancellationToken ct)
    {
        {
            AccountView? parent = null;
            if (input.ParentId is { } pid)
            {
                parent = (await LoadAsync(work.Connection, work.Transaction, pid, true, ct)).SingleOrDefault()
                    ?? throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("parentId", "parent_not_found")]);
                if (!parent.IsGroup) throw new Abort(ConfigStatus.Conflict, "parent_not_group");
                if (!parent.IsActive) throw new Abort(ConfigStatus.Conflict, "parent_inactive");
            }
            AccountType type;
            if (parent is not null)
            {
                if (givenType is { } gt && gt != parent.Type) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("type", "parent_type_mismatch")]);
                type = parent.Type;
            }
            else type = givenType ?? throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("type", "required")]);
            await EnsureCurrencyEnabledAsync(work, policy, currency, ct);
            var settings = await ReadSettingsAsync(work.Connection, work.Transaction, ct);
            var code = manualCode;
            if (code is null)
            {
                if (!settings.AutoCodes) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("code", "required")]);
                code = await AutoCodeAsync(work, parent, type, settings.SegmentWidth, ct) ?? throw new Abort(ConfigStatus.Conflict, "code_auto_unavailable");
            }
            var nature = givenNature ?? ConfigurationValidation.DefaultNature(type);
            var id = Guid.NewGuid();
            var now = clock.GetUtcNow().UtcDateTime;
            try
            {
                await using var ins = new NpgsqlCommand(
                    $"INSERT INTO {A.Table} ({A.Id}, {A.Code}, {A.NameAr}, {A.NameEn}, {A.ParentId}, {A.Type}, {A.Nature}, {A.Statement}, {A.IsGroup}, {A.IsActive}, {A.CurrencyPolicy}, {A.CurrencyCode}, {A.Revision}, {A.CreatedAtUtc}, {A.UpdatedAtUtc}) " +
                    "VALUES (@id, @code, @ar, @en, @parent, @type, @nature, @stmt, @group, true, @pol, @cur, 1, @now, @now)", work.Connection, work.Transaction);
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("code", code);
                ins.Parameters.AddWithValue("ar", (object?)nameAr ?? DBNull.Value);
                ins.Parameters.AddWithValue("en", (object?)nameEn ?? DBNull.Value);
                ins.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parent?.Id ?? DBNull.Value });
                ins.Parameters.AddWithValue("type", (short)type);
                ins.Parameters.AddWithValue("nature", (short)nature);
                ins.Parameters.AddWithValue("stmt", (short)(type <= AccountType.Equity ? AccountStatement.BalanceSheet : AccountStatement.ProfitAndLoss));
                ins.Parameters.AddWithValue("group", isGroup);
                ins.Parameters.AddWithValue("pol", (short)policy);
                ins.Parameters.Add(new NpgsqlParameter("cur", NpgsqlDbType.Char) { Value = (object?)currency ?? DBNull.Value });
                ins.Parameters.AddWithValue("now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new Abort(ConfigStatus.Conflict, "account_code_taken");
            }
            var changes = new JsonObject
            {
                ["code"] = Change(null, code), ["nameAr"] = Change(null, nameAr), ["nameEn"] = Change(null, nameEn),
                ["parentId"] = Change(null, parent?.Id.ToString()), ["type"] = Change(null, type.ToString()), ["nature"] = Change(null, nature.ToString()),
                ["isGroup"] = new JsonObject { ["old"] = null, ["new"] = isGroup }, ["currencyPolicy"] = Change(null, policy.ToString()), ["currencyCode"] = Change(null, currency),
            };
            await RecordAsync(work, ConfigAuditEvents.AccountCreated, actor, 1, id, 1, changes, "account created", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }
    }

    /// <summary>Names, nature, group flag and currency policy. The type never changes; code and parent have their own operations.</summary>
    public async Task<ConfigResult<AccountView>> UpdateDetailsAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, AccountDetailsInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<AccountView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!ConfigurationValidation.TryNormalizeAccountDetails(input, out var nameAr, out var nameEn, out var nature, out var group, out var policy, out var currency, out var errors))
            return ConfigResult<AccountView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            var newNature = nature ?? current.Nature;
            var newGroup = group ?? current.IsGroup;
            await EnsureCurrencyEnabledAsync(work, policy, currency, ct, current);
            var protectedChange = newNature != current.Nature || newGroup != current.IsGroup || policy != current.CurrencyPolicy || currency != current.CurrencyCode;
            if (protectedChange) await EnsureNoPostingHistoryAsync(work, companyId, id, ct);
            if (current.IsGroup && !newGroup && current.HasChildren) throw new Abort(ConfigStatus.Conflict, "account_has_children");
            var changes = new JsonObject();
            Diff(changes, "nameAr", current.NameAr, nameAr); Diff(changes, "nameEn", current.NameEn, nameEn);
            if (newNature != current.Nature) changes["nature"] = Change(current.Nature.ToString(), newNature.ToString());
            if (newGroup != current.IsGroup) changes["isGroup"] = new JsonObject { ["old"] = current.IsGroup, ["new"] = newGroup };
            if (policy != current.CurrencyPolicy) changes["currencyPolicy"] = Change(current.CurrencyPolicy.ToString(), policy.ToString());
            Diff(changes, "currencyCode", current.CurrencyCode, currency);
            if (changes.Count == 0) throw new Abort(ConfigStatus.Ok, "unchanged");
            await ExecAsync(work,
                $"UPDATE {A.Table} SET {A.NameAr} = @ar, {A.NameEn} = @en, {A.Nature} = @nature, {A.IsGroup} = @group, {A.CurrencyPolicy} = @pol, {A.CurrencyCode} = @cur, {A.Revision} = {A.Revision} + 1, {A.UpdatedAtUtc} = @now WHERE {A.Id} = @id",
                p =>
                {
                    p.AddWithValue("id", id); p.AddWithValue("ar", (object?)nameAr ?? DBNull.Value); p.AddWithValue("en", (object?)nameEn ?? DBNull.Value);
                    p.AddWithValue("nature", (short)newNature); p.AddWithValue("group", newGroup); p.AddWithValue("pol", (short)policy);
                    p.Add(new NpgsqlParameter("cur", NpgsqlDbType.Char) { Value = (object?)currency ?? DBNull.Value }); p.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
                });
            await RecordAsync(work, ConfigAuditEvents.AccountUpdated, actor, 1, id, current.Revision + 1, changes, "account updated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct, onUnchanged: async () => await GetAsync(companyId, id, ct));
    }

    /// <summary>Controlled, audited re-coding. The id (what everything references) does not change; the old code stays in the history.</summary>
    public async Task<ConfigResult<AccountView>> RecodeAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, AccountCodeInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<AccountView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var code = ConfigurationValidation.NormalizeAccountCode(input?.Code);
        if (code is null) return ConfigResult<AccountView>.Invalid([new("code", string.IsNullOrWhiteSpace(input?.Code) ? "required" : "invalid_format")]);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (current.Code == code) throw new Abort(ConfigStatus.Ok, "unchanged");
            try
            {
                await ExecAsync(work, $"UPDATE {A.Table} SET {A.Code} = @code, {A.Revision} = {A.Revision} + 1, {A.UpdatedAtUtc} = @now WHERE {A.Id} = @id",
                    p => { p.AddWithValue("id", id); p.AddWithValue("code", code); p.AddWithValue("now", clock.GetUtcNow().UtcDateTime); });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new Abort(ConfigStatus.Conflict, "account_code_taken");
            }
            await RecordAsync(work, ConfigAuditEvents.AccountCodeChanged, actor, 1, id, current.Revision + 1, new JsonObject { ["code"] = Change(current.Code, code) }, "account code changed", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct, onUnchanged: async () => await GetAsync(companyId, id, ct));
    }

    /// <summary>Moves the account (with its whole subtree) under another group account of the same type, or to the top level.</summary>
    public async Task<ConfigResult<AccountView>> MoveAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, AccountMoveInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<AccountView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (input is null) return ConfigResult<AccountView>.Invalid([new("body", "required")]);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (current.ParentId == input.ParentId) throw new Abort(ConfigStatus.Ok, "unchanged");
            if (input.ParentId is { } newParentId)
            {
                if (newParentId == id) throw new Abort(ConfigStatus.Conflict, "account_cycle");
                var parent = (await LoadAsync(work.Connection, work.Transaction, newParentId, true, ct)).SingleOrDefault()
                    ?? throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("parentId", "parent_not_found")]);
                if (!parent.IsGroup) throw new Abort(ConfigStatus.Conflict, "parent_not_group");
                if (current.IsActive && !parent.IsActive) throw new Abort(ConfigStatus.Conflict, "parent_inactive");
                if (parent.Type != current.Type) throw new Abort(ConfigStatus.Conflict, "parent_type_mismatch");
                if (await IsDescendantAsync(work, newParentId, id, ct)) throw new Abort(ConfigStatus.Conflict, "account_cycle");
            }
            await ExecAsync(work, $"UPDATE {A.Table} SET {A.ParentId} = @parent, {A.Revision} = {A.Revision} + 1, {A.UpdatedAtUtc} = @now WHERE {A.Id} = @id",
                p => { p.AddWithValue("id", id); p.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)input.ParentId ?? DBNull.Value }); p.AddWithValue("now", clock.GetUtcNow().UtcDateTime); });
            await RecordAsync(work, ConfigAuditEvents.AccountMoved, actor, 1, id, current.Revision + 1,
                new JsonObject { ["parentId"] = Change(current.ParentId?.ToString(), input.ParentId?.ToString()) }, "account moved", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct, onUnchanged: async () => await GetAsync(companyId, id, ct));
    }

    /// <summary>Deactivation never deletes anything (history stays); a group can only be deactivated when none of its children is active.</summary>
    public async Task<ConfigResult<AccountView>> DeactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        await ToggleAsync(actor, companyId, id, expectedRevision, false, ct);

    /// <summary>Reactivation needs an active parent.</summary>
    public async Task<ConfigResult<AccountView>> ReactivateAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, CancellationToken ct = default) =>
        await ToggleAsync(actor, companyId, id, expectedRevision, true, ct);

    private async Task<ConfigResult<AccountView>> ToggleAsync(Guid actor, Guid companyId, Guid id, long? expectedRevision, bool active, CancellationToken ct)
    {
        if (expectedRevision is null) return ConfigResult<AccountView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await RequireAsync(work, id, expectedRevision.Value, ct);
            if (current.IsActive == active) throw new Abort(ConfigStatus.Conflict, active ? "account_already_active" : "account_already_inactive");
            if (!active)
            {
                await using var q = new NpgsqlCommand($"SELECT count(*) FROM {A.Table} WHERE {A.ParentId} = @id AND {A.IsActive}", work.Connection, work.Transaction);
                q.Parameters.AddWithValue("id", id);
                if (Convert.ToInt64(await q.ExecuteScalarAsync(ct)) > 0) throw new Abort(ConfigStatus.Conflict, "account_has_active_children");
            }
            else if (current.ParentId is { } p)
            {
                var parent = (await LoadAsync(work.Connection, work.Transaction, p, true, ct)).Single();
                if (!parent.IsActive) throw new Abort(ConfigStatus.Conflict, "parent_inactive");
            }
            await ExecAsync(work, $"UPDATE {A.Table} SET {A.IsActive} = @a, {A.Revision} = {A.Revision} + 1, {A.UpdatedAtUtc} = @now WHERE {A.Id} = @id",
                p => { p.AddWithValue("id", id); p.AddWithValue("a", active); p.AddWithValue("now", clock.GetUtcNow().UtcDateTime); });
            await RecordAsync(work, active ? ConfigAuditEvents.AccountReactivated : ConfigAuditEvents.AccountDeactivated, actor, 1, id, current.Revision + 1,
                new JsonObject { ["isActive"] = new JsonObject { ["old"] = !active, ["new"] = active } }, active ? "account reactivated" : "account deactivated", ct);
            return (await LoadAsync(work.Connection, work.Transaction, id, false, ct)).Single();
        }, ct);
    }

    public async Task<ConfigResult<ChartSettingsView>> PutSettingsAsync(Guid actor, Guid companyId, long? expectedRevision, ChartSettingsInput? input, CancellationToken ct = default)
    {
        if (expectedRevision is null) return ConfigResult<ChartSettingsView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!ConfigurationValidation.TryNormalizeChartSettings(input, out var width, out var auto, out var errors)) return ConfigResult<ChartSettingsView>.Invalid(errors);
        return await MutateAsync(companyId, async work =>
        {
            await LockAsync(work, ct);
            var current = await ReadSettingsAsync(work.Connection, work.Transaction, ct, forUpdate: true);
            if (current.Revision != expectedRevision.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", current.Revision);
            if (current.SegmentWidth == width && current.AutoCodes == auto && current.Exists) throw new Abort(ConfigStatus.Ok, "unchanged");
            var now = clock.GetUtcNow().UtcDateTime;
            if (!current.Exists)
                await ExecAsync(work, $"INSERT INTO {AS.Table} ({AS.Singleton}, {AS.SegmentWidth}, {AS.AutoCodes}, {AS.Revision}, {AS.UpdatedAtUtc}) VALUES (true, @w, @a, 1, @now)",
                    p => { p.AddWithValue("w", (short)width); p.AddWithValue("a", auto); p.AddWithValue("now", now); });
            else
                await ExecAsync(work, $"UPDATE {AS.Table} SET {AS.SegmentWidth} = @w, {AS.AutoCodes} = @a, {AS.Revision} = {AS.Revision} + 1, {AS.UpdatedAtUtc} = @now",
                    p => { p.AddWithValue("w", (short)width); p.AddWithValue("a", auto); p.AddWithValue("now", now); });
            var revision = current.Revision + 1;
            var changes = new JsonObject
            {
                ["segmentWidth"] = new JsonObject { ["old"] = current.SegmentWidth, ["new"] = width },
                ["autoCodes"] = new JsonObject { ["old"] = current.AutoCodes, ["new"] = auto },
            };
            await RecordAsync(work, ConfigAuditEvents.ChartSettingsChanged, actor, 2, Guid.Empty, revision, changes, "chart settings changed", ct);
            return new ChartSettingsView(width, auto, revision, true);
        }, ct, onUnchanged: async () => await GetSettingsAsync(companyId, ct));
    }

    /// <summary>
    /// Creates the accounts of a code-defined template in one transaction. Only on an EMPTY chart (accounts are never deleted, so this can happen once).
    /// One audited event and one history row cover the whole application; the accounts carry the template key as provenance.
    /// </summary>
    public async Task<ConfigResult<IReadOnlyList<AccountView>>> ApplyTemplateAsync(Guid actor, Guid companyId, string? templateKey, CancellationToken ct = default)
    {
        var template = ChartTemplates.Find(templateKey);
        if (template is null) return ConfigResult<IReadOnlyList<AccountView>>.Fail(ConfigStatus.NotFound, "template_not_found");
        return await MutateAsync<IReadOnlyList<AccountView>>(companyId, async work =>
        {
            await LockAsync(work, ct);
            await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {A.Table}", work.Connection, work.Transaction))
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) > 0) throw new Abort(ConfigStatus.Conflict, "chart_not_empty");
            var ids = template.Accounts.ToDictionary(a => a.Code, _ => Guid.NewGuid());
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var a in template.Accounts)   // parents precede children in every template
                await ExecAsync(work,
                    $"INSERT INTO {A.Table} ({A.Id}, {A.Code}, {A.NameAr}, {A.NameEn}, {A.ParentId}, {A.Type}, {A.Nature}, {A.Statement}, {A.IsGroup}, {A.IsActive}, {A.CurrencyPolicy}, {A.TemplateKey}, {A.Revision}, {A.CreatedAtUtc}, {A.UpdatedAtUtc}) " +
                    "VALUES (@id, @code, @ar, @en, @parent, @type, @nature, @stmt, @group, true, 1, @tpl, 1, @now, @now)",
                    p =>
                    {
                        p.AddWithValue("id", ids[a.Code]); p.AddWithValue("code", a.Code); p.AddWithValue("ar", a.NameAr); p.AddWithValue("en", a.NameEn);
                        p.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = a.ParentCode is null ? DBNull.Value : ids[a.ParentCode] });
                        p.AddWithValue("type", (short)a.Type); p.AddWithValue("nature", (short)a.Nature);
                        p.AddWithValue("stmt", (short)(a.Type <= AccountType.Equity ? AccountStatement.BalanceSheet : AccountStatement.ProfitAndLoss));
                        p.AddWithValue("group", a.IsGroup); p.AddWithValue("tpl", template.Key); p.AddWithValue("now", now);
                    });
            var changes = new JsonObject { ["template"] = template.Key, ["accounts"] = template.Accounts.Count };
            await RecordAsync(work, ConfigAuditEvents.ChartTemplateApplied, actor, 3, Guid.Empty, 1, changes, "chart template applied", ct);
            return await LoadAsync(work.Connection, work.Transaction, null, false, ct);
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- plumbing

    private async Task<ConfigResult<T>> MutateAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct, Func<Task<T?>>? onUnchanged = null) where T : class
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (Abort abort) when (abort.Message == "unchanged" && onUnchanged is not null) { return ConfigResult<T>.Ok((await onUnchanged())!); }
        catch (Abort abort) { return abort.To<T>(); }
    }

    private static async Task LockAsync(CompanyAuditedWork work, CancellationToken ct)
    {
        await using var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction);
        lk.Parameters.AddWithValue("a", LockKey1);
        lk.Parameters.AddWithValue("b", LockKey2);
        await lk.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(CompanyAuditedWork work, string sql, Action<NpgsqlParameterCollection> bind)
    {
        await using var cmd = new NpgsqlCommand(sql, work.Connection, work.Transaction);
        bind(cmd.Parameters);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("account row not written");
    }

    private async Task<AccountView> RequireAsync(CompanyAuditedWork work, Guid id, long expectedRevision, CancellationToken ct)
    {
        var current = (await LoadAsync(work.Connection, work.Transaction, id, true, ct)).SingleOrDefault() ?? throw new Abort(ConfigStatus.NotFound, "account_not_found");
        if (current.Revision != expectedRevision) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", current.Revision);
        return current;
    }

    private static async Task<List<AccountView>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var sql = $"SELECT {Columns} FROM {A.Table} a WHERE (@id::uuid IS NULL OR a.{A.Id} = @id) ORDER BY a.{A.Code}{(forUpdate ? " FOR UPDATE OF a" : "")}";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = (object?)id ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AccountView>();
        while (await r.ReadAsync(ct))
            list.Add(new AccountView(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4),
                (AccountType)r.GetInt16(5), (AccountNature)r.GetInt16(6), (AccountStatement)r.GetInt16(7), r.GetBoolean(8), r.GetBoolean(9), (AccountCurrencyPolicy)r.GetInt16(10),
                r.IsDBNull(11) ? null : r.GetString(11).Trim(), r.IsDBNull(12) ? null : r.GetString(12), r.GetInt64(13), r.GetFieldValue<DateTimeOffset>(14), r.GetFieldValue<DateTimeOffset>(15), r.GetBoolean(16)));
        return list;
    }

    private static async Task<ChartSettingsView> ReadSettingsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct, bool forUpdate = false)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {AS.SegmentWidth}, {AS.AutoCodes}, {AS.Revision} FROM {AS.Table}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ChartSettingsView(r.GetInt16(0), r.GetBoolean(1), r.GetInt64(2), true) : ChartSettingsView.Defaults;
    }

    private static async Task<bool> IsDescendantAsync(CompanyAuditedWork work, Guid candidate, Guid ancestor, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"WITH RECURSIVE up(id, parent) AS (SELECT {A.Id}, {A.ParentId} FROM {A.Table} WHERE {A.Id} = @c UNION SELECT a.{A.Id}, a.{A.ParentId} FROM {A.Table} a JOIN up ON a.{A.Id} = up.parent) SELECT EXISTS (SELECT 1 FROM up WHERE id = @a)",
            work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("c", candidate); cmd.Parameters.AddWithValue("a", ancestor);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task EnsureCurrencyEnabledAsync(CompanyAuditedWork work, AccountCurrencyPolicy policy, string? currency, CancellationToken ct, AccountView? current = null)
    {
        if (policy != AccountCurrencyPolicy.Specific || currency is null) return;
        if (current is not null && current.CurrencyPolicy == AccountCurrencyPolicy.Specific && current.CurrencyCode == currency) return;   // unchanged: already accepted
        await using var cmd = new NpgsqlCommand($"SELECT 1 FROM {CC.Table} WHERE {CC.Code} = @c AND {CC.Enabled}", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("c", currency);
        if (await cmd.ExecuteScalarAsync(ct) is null) throw new Abort(ConfigStatus.Invalid, "validation_failed", fields: [new("currencyCode", "currency_not_enabled")]);
    }

    /// <summary>
    /// Changes to nature, group flag or currency policy are allowed only while the account has no posting history. When a financial module already holds
    /// history but the account-usage probe is still a placeholder, nobody can tell, so the change is refused (no silent bypass).
    /// </summary>
    private async Task EnsureNoPostingHistoryAsync(CompanyAuditedWork work, Guid companyId, Guid accountId, CancellationToken ct)
    {
        if (usage.UnimplementedDomains.Count > 0 && (await financialHistory.EvaluateAsync(companyId, work.Connection, work.Transaction, ct)).Locked)
            throw new Abort(ConfigStatus.Conflict, "account_usage_checks_not_implemented");
        if (await usage.HasPostingsAsync(companyId, accountId, work.Connection, work.Transaction, ct)) throw new Abort(ConfigStatus.Conflict, "account_has_postings");
    }

    private static async Task<string?> AutoCodeAsync(CompanyAuditedWork work, AccountView? parent, AccountType type, int width, CancellationToken ct)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var siblings = new List<string>();
        await using (var cmd = new NpgsqlCommand($"SELECT upper({A.Code}), {A.ParentId} FROM {A.Table}", work.Connection, work.Transaction))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                taken.Add(r.GetString(0));
                if ((parent is null && r.IsDBNull(1)) || (parent is not null && !r.IsDBNull(1) && r.GetGuid(1) == parent.Id)) siblings.Add(r.GetString(0));
            }
        if (parent is not null)
        {
            long max = 0;
            foreach (var s in siblings)
                if (s.StartsWith(parent.Code, StringComparison.Ordinal) && s.Length > parent.Code.Length && s.Length - parent.Code.Length <= 9
                    && long.TryParse(s.AsSpan(parent.Code.Length), out var n) && s[parent.Code.Length..].All(char.IsDigit)) max = Math.Max(max, n);
            for (var n = max + 1; n < max + 10_000; n++)
            {
                var candidate = parent.Code + n.ToString().PadLeft(width, '0');
                if (candidate.Length > 30) return null;
                if (!taken.Contains(candidate) && ConfigurationValidation.NormalizeAccountCode(candidate) == candidate) return candidate;
            }
            return null;
        }
        var root = ((int)type).ToString();   // 1 Assets, 2 Liabilities, 3 Equity, 4 Revenue, 5 Expenses
        if (!taken.Contains(root)) return root;
        // Numbers 1-5 stay reserved for the default roots of the five types, so a second top-level account of a type starts after them.
        long start = Math.Max(5, siblings.Where(s => s.Length <= 9 && s.All(char.IsDigit)).Select(long.Parse).DefaultIfEmpty(5).Max()) + 1;
        for (var n = start; n < start + 10_000; n++)
            if (!taken.Contains(n.ToString())) return n.ToString();
        return null;
    }

    private static JsonObject Change(string? old, string? @new) => new() { ["old"] = old, ["new"] = @new };

    private static void Diff(JsonObject changes, string name, string? old, string? @new)
    {
        if (!string.Equals(old, @new, StringComparison.Ordinal)) changes[name] = Change(old, @new);
    }

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant();

    private async Task RecordAsync(CompanyAuditedWork work, short eventType, Guid actor, short entity, Guid entityId, long revision, JsonObject changes, string label, CancellationToken ct)
    {
        var json = changes.ToJsonString();
        var receipt = await work.AppendAuditAsync(new AuditEventData(eventType, ActorUserId: actor,
            Detail: $"{label} r{revision} f={string.Join(',', changes.Select(c => c.Key))} h={Hash(json)}"), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {AH.Table} ({AH.Entity}, {AH.EntityId}, {AH.Revision}, {AH.ActorUserId}, {AH.ChangedAtUtc}, {AH.Changes}, {AH.AuditStream}, {AH.AuditSeq}) VALUES (@e, @k, @r, @a, @t, @c, @s, @q)",
            work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("e", entity);
        cmd.Parameters.AddWithValue("k", entityId);
        cmd.Parameters.AddWithValue("r", revision);
        cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("s", receipt.StreamKey);
        cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
