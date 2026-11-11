using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using Cfg = Hasm.Infrastructure.CompanyDatabase.CompanySchema;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Company profile, settings, currencies and configuration history. Every public method authorizes the acting user against the selected
/// company (permissions are re-read from Hasm_System on every call: no cache), then reaches the company database only through
/// <see cref="CompanyDatabaseResolver"/> (that company's own least-privilege role). Every mutation runs in
/// <see cref="CompanyAuditedTransactions"/>: the change, its append-only <c>ConfigRevision</c> row and its authenticated audit event commit
/// or roll back together. Concurrency is optimistic (revision, 0 = does not exist yet) with a database-level fence on every table.
/// Authorization and the company transaction are separate databases: a permission revoked in the instant between the check and the commit
/// is not seen by that one in-flight request (the next request is refused).
/// </summary>
public sealed class CompanyConfigurationService(
    CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, AccessService access,
    IEntitlementPolicy entitlements, FinancialHistoryGuard financialHistory, CompanyDisplayNameProjection projection, TimeProvider clock)
{
    public const int MaxPageSize = 200;
    private const int CurrencyLockKey1 = 0x48434647;   // "HCFG": advisory-lock namespace of configuration, distinct from the audit stream locks
    private const int CurrencyLockKey2 = 1;

    public IEntitlementPolicy Entitlements => entitlements;

    // ------------------------------------------------------------------------------------------------------------- shared plumbing

    private sealed record Field(string Name, string Column, bool HistoryValues);

    private sealed class ConfigAbort(ConfigStatus status, string errorCode, long? currentRevision = null, IReadOnlyList<FieldError>? fields = null, object? value = null) : Exception(errorCode)
    {
        public ConfigResult<T> ToResult<T>() => Status == ConfigStatus.Ok
            ? ConfigResult<T>.Ok((T)Value!)
            : new ConfigResult<T>(Status, Message, default, fields, currentRevision);
        public ConfigStatus Status => status;
        public object? Value => value;
    }

    private Task<bool> AllowedAsync(Guid userId, Guid companyId, string code, CancellationToken ct) =>
        access.IsAllowedAsync(userId, companyId, code, ScopeTarget.Company, ct);

    private static ConfigResult<T> Denied<T>() => ConfigResult<T>.Fail(ConfigStatus.Forbidden, "forbidden");

    private async Task<ConfigResult<T>> MutateAsync<T>(Guid companyId, Func<CompanyAuditedWork, Task<T>> work, CancellationToken ct)
    {
        try { return ConfigResult<T>.Ok(await transactions.ExecuteAsync(companyId, work, ct)); }
        catch (ConfigAbort abort) { return abort.ToResult<T>(); }
    }

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant();

    private static JsonNode? Node(object? v) => v switch { null => null, bool b => b, long l => l, string s => s, _ => throw new InvalidOperationException() };

    /// <summary>
    /// Field-level before/after, but only for explicitly permitted fields; sensitive fields record that they changed, never their values
    /// (neither here nor in the audit event).
    /// </summary>
    private static (JsonObject Changes, List<string> Names) Diff(IReadOnlyList<Field> fields, object?[]? oldValues, object?[] newValues)
    {
        var changes = new JsonObject();
        var names = new List<string>();
        for (var i = 0; i < fields.Count; i++)
        {
            var before = oldValues?[i];
            if (Equals(before, newValues[i])) continue;
            names.Add(fields[i].Name);
            changes[fields[i].Name] = fields[i].HistoryValues
                ? new JsonObject { ["old"] = Node(before), ["new"] = Node(newValues[i]) }
                : new JsonObject { ["changed"] = true };
        }
        return (changes, names);
    }

    private async Task<long> RecordAsync(CompanyAuditedWork work, short eventType, Guid actor, string entity, string key, long revision, JsonObject changes, IReadOnlyList<string> names, string label, CancellationToken ct)
    {
        var json = changes.ToJsonString();
        var receipt = await work.AppendAuditAsync(new AuditEventData(eventType, ActorUserId: actor,
            Detail: $"{label} r{revision} f={string.Join(',', names)} h={Hash(json)}"), ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {Cfg.ConfigRevision.Table} ({Cfg.ConfigRevision.Entity}, {Cfg.ConfigRevision.EntityKey}, {Cfg.ConfigRevision.EntityRevision}, {Cfg.ConfigRevision.ActorUserId}, " +
            $"{Cfg.ConfigRevision.ChangedAtUtc}, {Cfg.ConfigRevision.Changes}, {Cfg.ConfigRevision.AuditStream}, {Cfg.ConfigRevision.AuditSeq}) " +
            "VALUES (@e, @k, @r, @a, @t, @c, @s, @q)", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("e", entity);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("r", revision);
        cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("t", clock.GetUtcNow().UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("s", receipt.StreamKey);
        cmd.Parameters.AddWithValue("q", receipt.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
        return receipt.Seq;
    }

    // ------------------------------------------------------------------------------------------------------------- singleton tables

    private sealed record Singleton(string Entity, string Table, string Key, string Revision, string Updated, IReadOnlyList<Field> Fields);

    private static readonly IReadOnlyList<Field> ProfileFields =
    [
        new("nameAr", Cfg.Profile.NameAr, true), new("nameEn", Cfg.Profile.NameEn, true), new("legalName", Cfg.Profile.LegalName, true),
        new("commercialRegistrationNo", Cfg.Profile.RegistrationNo, false), new("taxRegistrationNo", Cfg.Profile.TaxNo, false),
        new("countryCode", Cfg.Profile.CountryCode, true),
        new("addressLine1", Cfg.Profile.Address1, false), new("addressLine2", Cfg.Profile.Address2, false),
        new("city", Cfg.Profile.City, true), new("region", Cfg.Profile.Region, true), new("postalCode", Cfg.Profile.PostalCode, false),
        new("phone", Cfg.Profile.Phone, false), new("email", Cfg.Profile.Email, false), new("website", Cfg.Profile.Website, false),
    ];

    private static readonly IReadOnlyList<Field> SettingsFields =
    [
        new("defaultLanguage", Cfg.Settings.DefaultLanguage, true), new("dateFormat", Cfg.Settings.DateFormat, true),
        new("numeralSystem", Cfg.Settings.NumeralSystem, true), new("timeZone", Cfg.Settings.TimeZone, true),
    ];

    private static readonly Singleton ProfileTable = new("profile", Cfg.Profile.Table, Cfg.Profile.Singleton, Cfg.Profile.Revision, Cfg.Profile.UpdatedAtUtc, ProfileFields);
    private static readonly Singleton SettingsTable = new("settings", Cfg.Settings.Table, Cfg.Settings.Singleton, Cfg.Settings.Revision, Cfg.Settings.UpdatedAtUtc, SettingsFields);

    private static object?[] Values(ProfileData d) =>
        [d.NameAr, d.NameEn, d.LegalName, d.CommercialRegistrationNo, d.TaxRegistrationNo, d.CountryCode, d.AddressLine1, d.AddressLine2, d.City, d.Region, d.PostalCode, d.Phone, d.Email, d.Website];

    private static ProfileData ToProfile(object?[] v) =>
        new((string?)v[0], (string?)v[1], (string?)v[2], (string?)v[3], (string?)v[4], (string)v[5]!, (string?)v[6], (string?)v[7], (string?)v[8], (string?)v[9], (string?)v[10], (string?)v[11], (string?)v[12], (string?)v[13]);

    private static object?[] Values(SettingsData d) => [d.DefaultLanguage, d.DateFormat, d.NumeralSystem, d.TimeZone];
    private static SettingsData ToSettings(object?[] v) => new((string)v[0]!, (string)v[1]!, (string)v[2]!, (string?)v[3]);

    private sealed record Row(object?[] Values, long Revision, DateTimeOffset UpdatedAtUtc);

    private static async Task<Row?> ReadSingletonAsync(Singleton t, NpgsqlConnection conn, NpgsqlTransaction? tx, bool forUpdate, CancellationToken ct)
    {
        var columns = string.Join(", ", t.Fields.Select(f => f.Column));
        await using var cmd = new NpgsqlCommand($"SELECT {columns}, {t.Revision}, {t.Updated} FROM {t.Table} WHERE {t.Key}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var values = new object?[t.Fields.Count];
        for (var i = 0; i < values.Length; i++) values[i] = r.IsDBNull(i) ? null : r.GetString(i);
        return new Row(values, r.GetInt64(values.Length), r.GetFieldValue<DateTimeOffset>(values.Length + 1));
    }

    private static NpgsqlCommand ParametersFor(NpgsqlCommand cmd, Singleton t, object?[] values)
    {
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue($"p{i}", values[i] ?? DBNull.Value);
        return cmd;
    }

    private async Task<ConfigResult<(object?[] Values, long Revision, DateTimeOffset At)>> PutSingletonAsync(
        Singleton t, Guid userId, Guid companyId, long expectedRevision, object?[] newValues, short createdEvent, short updatedEvent,
        Func<CompanyAuditedWork, object?[], object?[], Task>? guard, CancellationToken ct)
    {
        return await MutateAsync(companyId, async work =>
        {
            var current = await ReadSingletonAsync(t, work.Connection, work.Transaction, forUpdate: true, ct);
            var now = clock.GetUtcNow();
            long newRevision;
            if (current is null)
            {
                if (expectedRevision != 0) throw new ConfigAbort(ConfigStatus.PreconditionFailed, "revision_conflict", 0);
                var columns = string.Join(", ", t.Fields.Select(f => f.Column));
                var placeholders = string.Join(", ", t.Fields.Select((_, i) => $"@p{i}"));
                await using var insert = ParametersFor(new NpgsqlCommand(
                    $"INSERT INTO {t.Table} ({columns}, {t.Revision}, {t.Updated}) VALUES ({placeholders}, 1, @now) ON CONFLICT ({t.Key}) DO NOTHING", work.Connection, work.Transaction), t, newValues);
                insert.Parameters.AddWithValue("now", now.UtcDateTime);
                if (await insert.ExecuteNonQueryAsync(ct) != 1)   // a concurrent first write won (the unique key made us wait for its commit)
                {
                    var winner = await ReadSingletonAsync(t, work.Connection, work.Transaction, forUpdate: false, ct);
                    throw new ConfigAbort(ConfigStatus.PreconditionFailed, "revision_conflict", winner?.Revision ?? 0);
                }
                newRevision = 1;
            }
            else
            {
                if (expectedRevision != current.Revision) throw new ConfigAbort(ConfigStatus.PreconditionFailed, "revision_conflict", current.Revision);
                if (current.Values.SequenceEqual(newValues))
                    throw new ConfigAbort(ConfigStatus.Ok, "unchanged", value: (current.Values, current.Revision, current.UpdatedAtUtc));
                if (guard is not null) await guard(work, current.Values, newValues);
                newRevision = current.Revision + 1;
                var sets = string.Join(", ", t.Fields.Select((f, i) => $"{f.Column} = @p{i}"));
                await using var update = ParametersFor(new NpgsqlCommand(
                    $"UPDATE {t.Table} SET {sets}, {t.Revision} = @new, {t.Updated} = @now WHERE {t.Key} AND {t.Revision} = @exp", work.Connection, work.Transaction), t, newValues);
                update.Parameters.AddWithValue("new", newRevision);
                update.Parameters.AddWithValue("now", now.UtcDateTime);
                update.Parameters.AddWithValue("exp", expectedRevision);
                if (await update.ExecuteNonQueryAsync(ct) != 1) throw new ConfigAbort(ConfigStatus.PreconditionFailed, "revision_conflict", current.Revision);
            }
            var (changes, names) = Diff(t.Fields, current?.Values, newValues);
            await RecordAsync(work, current is null ? createdEvent : updatedEvent, userId, t.Entity, "", newRevision, changes, names, $"{t.Entity}.{(current is null ? "create" : "update")}", ct);
            return (newValues, newRevision, now);
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- profile

    private async Task<string> ProjectAsync(Guid companyId, ProfileData data, long revision, CancellationToken ct) =>
        await projection.TryProjectAsync(companyId, data.NameAr, data.NameEn, revision, ct) ? "synced" : "pending";

    public async Task<ConfigResult<ProfileView>> GetProfileAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        if (!await AllowedAsync(userId, companyId, PermissionCatalog.Codes.ProfileView, ct)) return Denied<ProfileView>();
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var row = await ReadSingletonAsync(ProfileTable, conn, null, forUpdate: false, ct);   // a read never creates or changes a row
        if (row is null) return ConfigResult<ProfileView>.Ok(new ProfileView(null, 0, null, "none"));
        var data = ToProfile(row.Values);
        // Self-healing: a projection that failed after an earlier commit is retried on the next authorized read.
        return ConfigResult<ProfileView>.Ok(new ProfileView(data, row.Revision, row.UpdatedAtUtc, await ProjectAsync(companyId, data, row.Revision, ct)));
    }

    public async Task<ConfigResult<ProfileView>> PutProfileAsync(Guid userId, Guid companyId, long? expectedRevision, ProfileInput? input, CancellationToken ct = default)
    {
        if (!await AllowedAsync(userId, companyId, PermissionCatalog.Codes.ProfileUpdate, ct)) return Denied<ProfileView>();
        if (expectedRevision is not { } expected) return ConfigResult<ProfileView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!ConfigurationValidation.TryNormalizeProfile(input, out var data, out var errors)) return ConfigResult<ProfileView>.Invalid(errors);
        var result = await PutSingletonAsync(ProfileTable, userId, companyId, expected, Values(data), ConfigAuditEvents.ProfileCreated, ConfigAuditEvents.ProfileUpdated, null, ct);
        if (!result.IsOk) return new ConfigResult<ProfileView>(result.Status, result.Code, default, result.Fields, result.CurrentRevision);
        var (values, revision, at) = result.Value;
        var stored = ToProfile(values);
        // After the commit: a projection failure is reported, never rolled back into the successful company change.
        return ConfigResult<ProfileView>.Ok(new ProfileView(stored, revision, at, await ProjectAsync(companyId, stored, revision, ct)));
    }

    /// <summary>Recovery path: re-projects the authoritative profile to the registry (idempotent, monotonic).</summary>
    public async Task<ConfigResult<ProfileView>> SyncDisplayNameAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        if (!await AllowedAsync(userId, companyId, PermissionCatalog.Codes.ProfileUpdate, ct)) return Denied<ProfileView>();
        return await GetProfileAsyncUnchecked(companyId, ct);
    }

    private async Task<ConfigResult<ProfileView>> GetProfileAsyncUnchecked(Guid companyId, CancellationToken ct)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var row = await ReadSingletonAsync(ProfileTable, conn, null, forUpdate: false, ct);
        if (row is null) return ConfigResult<ProfileView>.Fail(ConfigStatus.Conflict, "profile_not_set");
        var data = ToProfile(row.Values);
        return ConfigResult<ProfileView>.Ok(new ProfileView(data, row.Revision, row.UpdatedAtUtc, await ProjectAsync(companyId, data, row.Revision, ct)));
    }

    // ------------------------------------------------------------------------------------------------------------- settings

    public async Task<ConfigResult<SettingsView>> GetSettingsAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        if (!await AllowedAsync(userId, companyId, PermissionCatalog.Codes.SettingsView, ct)) return Denied<SettingsView>();
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var row = await ReadSingletonAsync(SettingsTable, conn, null, forUpdate: false, ct);
        if (row is null) return ConfigResult<SettingsView>.Ok(new SettingsView(SettingsData.Defaults, 0, false, false, null));
        var data = ToSettings(row.Values);
        return ConfigResult<SettingsView>.Ok(new SettingsView(data, row.Revision, true, data.TimeZone is not null, row.UpdatedAtUtc));
    }

    public async Task<ConfigResult<SettingsView>> PutSettingsAsync(Guid userId, Guid companyId, long? expectedRevision, SettingsInput? input, CancellationToken ct = default)
    {
        if (!await AllowedAsync(userId, companyId, PermissionCatalog.Codes.SettingsUpdate, ct)) return Denied<SettingsView>();
        if (expectedRevision is not { } expected) return ConfigResult<SettingsView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!ConfigurationValidation.TryNormalizeSettings(input, out var data, out var errors)) return ConfigResult<SettingsView>.Invalid(errors);
        var result = await PutSingletonAsync(SettingsTable, userId, companyId, expected, Values(data), ConfigAuditEvents.SettingsCreated, ConfigAuditEvents.SettingsUpdated,
            async (work, oldValues, newValues) =>
            {
                // Changing an already selected business time zone is refused once financial history exists (contract; see FinancialHistoryGuard).
                if (oldValues[3] is not null && !Equals(oldValues[3], newValues[3]) && (await financialHistory.EvaluateAsync(work.CompanyId, work.Connection, work.Transaction, ct)).Locked)
                    throw new ConfigAbort(ConfigStatus.Conflict, "setting_locked");
            }, ct);
        if (!result.IsOk) return new ConfigResult<SettingsView>(result.Status, result.Code, default, result.Fields, result.CurrentRevision);
        var (values, revision, at) = result.Value;
        var stored = ToSettings(values);
        return ConfigResult<SettingsView>.Ok(new SettingsView(stored, revision, true, stored.TimeZone is not null, at));
    }

    /// <summary>
    /// The company business time zone for operations that depend on company-local business dates (future modules). Refuses while the
    /// setup is incomplete: no zone is ever assumed. Internal use: callers authorize their own operation.
    /// </summary>
    public async Task<TimeZoneInfo> RequireBusinessTimeZoneAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var row = await ReadSingletonAsync(SettingsTable, conn, null, forUpdate: false, ct);
        if (row?.Values[3] is not string id) throw new CompanyTimeZoneNotConfiguredException();
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }

    // ------------------------------------------------------------------------------------------------------------- currencies

    private sealed record CurrencyRow(string Code, string NameEn, short? MinorUnits, bool IsBase, bool Enabled, long Revision);

    private static async Task<List<CurrencyRow>> ReadCurrenciesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT cc.{Cfg.CompanyCurrency.Code}, c.{Cfg.Currency.NameEn}, c.{Cfg.Currency.MinorUnits}, cc.{Cfg.CompanyCurrency.IsBase}, cc.{Cfg.CompanyCurrency.Enabled}, cc.{Cfg.CompanyCurrency.Revision} " +
            $"FROM {Cfg.CompanyCurrency.Table} cc JOIN {Cfg.Currency.Table} c ON c.{Cfg.Currency.Code} = cc.{Cfg.CompanyCurrency.Code} ORDER BY cc.{Cfg.CompanyCurrency.Code}", conn, tx);
        var list = new List<CurrencyRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CurrencyRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt16(2), r.GetBoolean(3), r.GetBoolean(4), r.GetInt64(5)));
        return list;
    }

    private async Task<CurrenciesView> BuildCurrenciesViewAsync(Guid companyId, NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var rows = await ReadCurrenciesAsync(conn, tx, ct);
        // Outside a transaction the history probes (which need one) are not consulted: report only what is known without claiming safety.
        var lockInfo = tx is null
            ? new LockInformation(false, financialHistory.UnimplementedDomains.Count == 0, financialHistory.UnimplementedDomains)
            : await financialHistory.EvaluateAsync(companyId, conn, tx, ct);
        var baseRow = rows.FirstOrDefault(r => r.IsBase);
        return new CurrenciesView([.. rows.Select(r => new CompanyCurrencyView(r.Code, r.NameEn, r.MinorUnits, r.IsBase, r.Enabled, r.Revision))], baseRow?.Code, baseRow is not null, lockInfo);
    }

    public async Task<ConfigResult<CurrenciesView>> GetCurrenciesAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        if (!await AllowedAsync(userId, companyId, PermissionCatalog.Codes.CurrenciesView, ct)) return Denied<CurrenciesView>();
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);   // read-only unit so the lock probes see one consistent snapshot
        return ConfigResult<CurrenciesView>.Ok(await BuildCurrenciesViewAsync(companyId, conn, tx, ct));
    }

    public async Task<ConfigResult<IReadOnlyList<CurrencyReference>>> GetCurrencyReferenceAsync(Guid userId, Guid companyId, CancellationToken ct = default)
    {
        if (!await AllowedAsync(userId, companyId, PermissionCatalog.Codes.CurrenciesView, ct)) return Denied<IReadOnlyList<CurrencyReference>>();
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Cfg.Currency.Code}, {Cfg.Currency.NumericCode}, {Cfg.Currency.NameEn}, {Cfg.Currency.MinorUnits}, {Cfg.Currency.IsActive}, {Cfg.Currency.DatasetVersion} FROM {Cfg.Currency.Table} ORDER BY {Cfg.Currency.Code}", conn);
        var list = new List<CurrencyReference>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CurrencyReference(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt16(3), r.GetBoolean(4), r.GetInt32(5)));
        return ConfigResult<IReadOnlyList<CurrencyReference>>.Ok(list);
    }

    /// <summary>
    /// Sets the state of one company currency. Rules: the first enabled currency must be the base; the base currency is always enabled and
    /// is replaced (never unset or disabled); replacing an established base is refused once financial history exists; the entitlement
    /// policy bounds how many currencies may be enabled (Trial: only the base currency, so enabling another is denied).
    /// </summary>
    public async Task<ConfigResult<CurrenciesView>> SetCurrencyAsync(Guid userId, Guid companyId, string? code, long? expectedRevision, bool? enabled, bool? isBase, CancellationToken ct = default)
    {
        if (!await AllowedAsync(userId, companyId, PermissionCatalog.Codes.CurrenciesUpdate, ct)) return Denied<CurrenciesView>();
        if (expectedRevision is not { } expected) return ConfigResult<CurrenciesView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        var errors = new List<FieldError>();
        if (!ConfigurationValidation.IsCurrencyCodeShape(code)) errors.Add(new("code", "invalid_currency_code"));
        if (enabled is null) errors.Add(new("enabled", "required"));
        if (errors.Count > 0) return ConfigResult<CurrenciesView>.Invalid(errors);
        var wantEnabled = enabled!.Value;
        var wantBase = isBase ?? false;
        if (wantBase && !wantEnabled) return ConfigResult<CurrenciesView>.Invalid([new("enabled", "base_currency_must_be_enabled")]);

        return await MutateAsync(companyId, async work =>
        {
            // Serialize every currency change of the company (a business lock: taken before the audit stream lock).
            await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction))
            {
                lk.Parameters.AddWithValue("a", CurrencyLockKey1);
                lk.Parameters.AddWithValue("b", CurrencyLockKey2);
                await lk.ExecuteNonQueryAsync(ct);
            }
            await using (var refCmd = new NpgsqlCommand($"SELECT {Cfg.Currency.IsActive} FROM {Cfg.Currency.Table} WHERE {Cfg.Currency.Code} = @c", work.Connection, work.Transaction))
            {
                refCmd.Parameters.AddWithValue("c", code!);
                var active = await refCmd.ExecuteScalarAsync(ct);
                if (active is null) throw new ConfigAbort(ConfigStatus.Invalid, "validation_failed", fields: [new FieldError("code", "unknown_currency")]);
                if (active is false && (await ReadCurrenciesAsync(work.Connection, work.Transaction, ct)).All(r => r.Code != code))
                    throw new ConfigAbort(ConfigStatus.Invalid, "validation_failed", fields: [new FieldError("code", "currency_not_active")]);
            }

            var rows = await ReadCurrenciesAsync(work.Connection, work.Transaction, ct);
            var target = rows.FirstOrDefault(r => r.Code == code);
            var oldBase = rows.FirstOrDefault(r => r.IsBase);
            if (expected != (target?.Revision ?? 0)) throw new ConfigAbort(ConfigStatus.PreconditionFailed, "revision_conflict", target?.Revision ?? 0);
            if (target is not null && target.Enabled == wantEnabled && target.IsBase == wantBase)
                throw new ConfigAbort(ConfigStatus.Ok, "unchanged", value: await BuildCurrenciesViewAsync(companyId, work.Connection, work.Transaction, ct));

            if (target is { IsBase: true } && !wantBase) throw new ConfigAbort(ConfigStatus.Invalid, "validation_failed", fields: [new FieldError("isBase", "base_currency_cannot_be_unset")]);
            if (wantBase && oldBase is not null && oldBase.Code != code)
            {
                var lockInfo = await financialHistory.EvaluateAsync(companyId, work.Connection, work.Transaction, ct);
                if (lockInfo.Locked) throw new ConfigAbort(ConfigStatus.Conflict, "base_currency_locked");
            }
            if (!wantBase && wantEnabled && oldBase is null)
                throw new ConfigAbort(ConfigStatus.Invalid, "validation_failed", fields: [new FieldError("isBase", "base_currency_required_first")]);

            // The resulting set, then the entitlement bound.
            var demote = wantBase && oldBase is not null && oldBase.Code != code ? oldBase : null;
            var disableDemoted = false;
            int EnabledCount() => rows.Count(r => r.Code != code && !(demote is not null && r.Code == demote.Code && disableDemoted) && r.Enabled) + (wantEnabled ? 1 : 0);
            if (EnabledCount() > entitlements.MaxEnabledCurrencies && demote is { Enabled: true }) disableDemoted = true;   // replacing the base frees its slot
            if (EnabledCount() > entitlements.MaxEnabledCurrencies) throw new ConfigAbort(ConfigStatus.EntitlementDenied, "entitlement_denied");

            var pending = new List<(CurrencyRow Row, bool Enabled, bool Base, long NewRevision, bool Insert, long Expected)>();
            if (demote is not null) pending.Add((demote, demote.Enabled && !disableDemoted, false, demote.Revision + 1, false, demote.Revision));
            pending.Add((target ?? new CurrencyRow(code!, "", null, false, false, 0), wantEnabled, wantBase, (target?.Revision ?? 0) + 1, target is null, target?.Revision ?? 0));

            foreach (var p in pending)   // all data changes first; the audit stream lock is taken last
            {
                var now = clock.GetUtcNow().UtcDateTime;
                await using var cmd = p.Insert
                    ? new NpgsqlCommand($"INSERT INTO {Cfg.CompanyCurrency.Table} ({Cfg.CompanyCurrency.Code}, {Cfg.CompanyCurrency.IsBase}, {Cfg.CompanyCurrency.Enabled}, {Cfg.CompanyCurrency.Revision}, {Cfg.CompanyCurrency.UpdatedAtUtc}) VALUES (@c, @b, @e, 1, @t)", work.Connection, work.Transaction)
                    : new NpgsqlCommand($"UPDATE {Cfg.CompanyCurrency.Table} SET {Cfg.CompanyCurrency.IsBase} = @b, {Cfg.CompanyCurrency.Enabled} = @e, {Cfg.CompanyCurrency.Revision} = @n, {Cfg.CompanyCurrency.UpdatedAtUtc} = @t WHERE {Cfg.CompanyCurrency.Code} = @c AND {Cfg.CompanyCurrency.Revision} = @x", work.Connection, work.Transaction);
                cmd.Parameters.AddWithValue("c", p.Row.Code);
                cmd.Parameters.AddWithValue("b", p.Base);
                cmd.Parameters.AddWithValue("e", p.Enabled);
                cmd.Parameters.AddWithValue("t", now);
                if (!p.Insert) { cmd.Parameters.AddWithValue("n", p.NewRevision); cmd.Parameters.AddWithValue("x", p.Expected); }
                if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConfigAbort(ConfigStatus.PreconditionFailed, "revision_conflict", p.Expected);
            }
            foreach (var p in pending)
            {
                var changes = new JsonObject();
                var names = new List<string>();
                if (p.Insert || p.Row.IsBase != p.Base) { names.Add("isBase"); changes["isBase"] = new JsonObject { ["old"] = p.Insert ? null : p.Row.IsBase, ["new"] = p.Base }; }
                if (p.Insert || p.Row.Enabled != p.Enabled) { names.Add("enabled"); changes["enabled"] = new JsonObject { ["old"] = p.Insert ? null : p.Row.Enabled, ["new"] = p.Enabled }; }
                var evt = p.Base && (p.Insert || !p.Row.IsBase) ? ConfigAuditEvents.BaseCurrencySet : p.Enabled ? ConfigAuditEvents.CurrencyEnabled : ConfigAuditEvents.CurrencyDisabled;
                await RecordAsync(work, evt, userId, "currency", p.Row.Code, p.NewRevision, changes, names, $"currency {p.Row.Code}", ct);
            }
            return await BuildCurrenciesViewAsync(companyId, work.Connection, work.Transaction, ct);
        }, ct);
    }

    // ------------------------------------------------------------------------------------------------------------- history

    public async Task<ConfigResult<IReadOnlyList<ConfigHistoryEntry>>> GetHistoryAsync(Guid userId, Guid companyId, string? entity, int limit, long? beforeId, CancellationToken ct = default)
    {
        if (!await AllowedAsync(userId, companyId, PermissionCatalog.Codes.ConfigView, ct)) return Denied<IReadOnlyList<ConfigHistoryEntry>>();
        if (entity is not null && entity is not ("profile" or "settings" or "currency"))
            return ConfigResult<IReadOnlyList<ConfigHistoryEntry>>.Invalid([new("entity", "unsupported_value")]);
        limit = Math.Clamp(limit, 1, MaxPageSize);
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var h = Cfg.ConfigRevision.Table;
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Cfg.ConfigRevision.Id}, {Cfg.ConfigRevision.Entity}, {Cfg.ConfigRevision.EntityKey}, {Cfg.ConfigRevision.EntityRevision}, {Cfg.ConfigRevision.ActorUserId}, {Cfg.ConfigRevision.ChangedAtUtc}, {Cfg.ConfigRevision.Changes}::text, {Cfg.ConfigRevision.AuditSeq} " +
            $"FROM {h} WHERE (@e::text IS NULL OR {Cfg.ConfigRevision.Entity} = @e) AND (@b::bigint IS NULL OR {Cfg.ConfigRevision.Id} < @b) ORDER BY {Cfg.ConfigRevision.Id} DESC LIMIT @n", conn);
        cmd.Parameters.AddWithValue("e", (object?)entity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("b", (object?)beforeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("n", limit);
        var list = new List<ConfigHistoryEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ConfigHistoryEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetGuid(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), r.GetInt64(7)));
        return ConfigResult<IReadOnlyList<ConfigHistoryEntry>>.Ok(list);
    }
}
