using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using A = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Account;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;

namespace Hasm.Infrastructure.Imports;

/// <summary>
/// Chart of Accounts import (Phase 3.2D S5, roadmap 3.2A-3). ALL knowledge about accounts lives here; the generic framework only stages, normalizes by type and controls the lifecycle.
/// <para><b>What an import can do:</b> CREATE new accounts, or explicitly SKIP a row. It never overwrites, merges, re-codes, moves, deactivates or deletes an existing account (owner decision).</para>
/// <para><b>Matching (deterministic, documented):</b> an existing account matches a row by its <i>code only</i> (codes are unique and normalized to upper case; leading zeros are significant).
/// A matching account with identical names, parent, type, nature, group flag and currency rule is an <b>exact</b> match (nothing to do). A matching account that differs in anything is a <b>conflict</b> (resolution: skip).
/// A row with a NEW code whose names equal (case-insensitive, trimmed) those of an existing sibling under the same parent is an ambiguous match (possible duplicate with another code): also a conflict (skip), never silently created.
/// Duplicate codes inside the file are handled by the framework (identical repeat = skippable conflict, differing repeat = blocking error).</para>
/// <para><b>Hierarchy:</b> a parent is named by its code and may be an existing account or another row of the file, in any order (children before parents are fine). Cycles, a missing parent, a parent that is not a group, an inactive existing parent, a type that differs
/// from the parent's, and a parent whose own row is invalid or skipped are blocking errors for the child. A root row must state its type; a child inherits it.</para>
/// <para><b>Application</b> is atomic: operations run parents first in one audited transaction through <see cref="ChartOfAccountsService.CreateInWorkAsync"/>, so every domain invariant, the history row and the audit event of an ordinary account creation still apply.</para>
/// </summary>
public sealed class ChartOfAccountsImportAdapter(ChartOfAccountsService chart) : IImportAdapter
{
    public const string AdapterCode = "chart-of-accounts";
    public string Code => AdapterCode;
    public int Version => 1;
    public string NameAr => "دليل الحسابات";
    public string NameEn => "Chart of accounts";
    public string UploadPermission => PermissionCatalog.Codes.AccountsCreate;
    public string ViewPermission => PermissionCatalog.Codes.AccountsView;
    public bool Atomic => true;
    public IReadOnlyList<string> Resolutions => [ImportResolutions.Skip];

    public IReadOnlyList<ImportField> Fields { get; } =
    [
        new("code", "رمز الحساب", "Account code", ImportFieldType.Code, Required: true, MaxLength: 30, IsKey: true, UpperCase: true, Aliases: ["account no", "account number", "code", "رقم الحساب", "كود الحساب", "الرمز"]),
        new("name_ar", "الاسم بالعربية", "Arabic name", ImportFieldType.Text, MaxLength: 200, Aliases: ["arabic", "الاسم العربي", "اسم الحساب", "اسم الحساب بالعربية"]),
        new("name_en", "الاسم بالإنجليزية", "English name", ImportFieldType.Text, MaxLength: 200, Aliases: ["english", "الاسم الانجليزي", "الاسم الإنجليزي", "اسم الحساب بالانجليزية"]),
        new("parent_code", "رمز الحساب الأب", "Parent code", ImportFieldType.Code, MaxLength: 30, UpperCase: true, Aliases: ["parent", "parent account", "الحساب الأب", "الحساب الاب", "الأب"]),
        new("type", "النوع", "Type", ImportFieldType.Enum, EnumValues: new Dictionary<string, string[]>
        {
            ["asset"] = ["assets", "أصل", "أصول", "اصول"], ["liability"] = ["liabilities", "التزام", "التزامات", "خصوم"], ["equity"] = ["حقوق ملكية", "حقوق الملكية"],
            ["revenue"] = ["income", "revenues", "إيراد", "إيرادات", "ايرادات"], ["expense"] = ["expenses", "مصروف", "مصروفات", "مصاريف"],
        }, Aliases: ["account type", "نوع الحساب"]),
        new("nature", "الطبيعة", "Nature", ImportFieldType.Enum, EnumValues: new Dictionary<string, string[]> { ["debit"] = ["dr", "مدين"], ["credit"] = ["cr", "دائن"] }, Aliases: ["normal balance", "طبيعة الحساب"]),
        new("is_group", "حساب رئيسي", "Group account", ImportFieldType.Boolean, Aliases: ["group", "is group", "رئيسي", "حساب تجميعي"]),
        new("currency_policy", "سياسة العملة", "Currency policy", ImportFieldType.Enum, EnumValues: new Dictionary<string, string[]> { ["base"] = ["الأساسية", "الاساسية"], ["any"] = ["أي عملة", "اي عملة", "أي"], ["specific"] = ["محددة", "عملة محددة"] }, Aliases: ["currency rule"]),
        new("currency_code", "رمز العملة", "Currency code", ImportFieldType.Code, MaxLength: 3, UpperCase: true, Aliases: ["currency", "العملة"]),
    ];

    private sealed record Existing(Guid Id, string Code, string? ParentCode, string? NameAr, string? NameEn, AccountType Type, AccountNature Nature, bool IsGroup, bool IsActive, AccountCurrencyPolicy Policy, string? Currency);

    /// <summary>A row after its own (data-independent) validation.</summary>
    private sealed class Draft(NormalizedRow row)
    {
        public NormalizedRow Row { get; } = row;
        public string Code = "";
        public string? ParentCode, NameAr, NameEn, Currency;
        public AccountType? GivenType, Type;
        public AccountNature? GivenNature;
        public bool IsGroup;
        public AccountCurrencyPolicy Policy = AccountCurrencyPolicy.Base;
        public List<RowIssue> Issues = [];
        public RowOutcome Outcome = RowOutcome.Valid;          // Valid = will be created (so far)
        public bool Creates;                                   // survives all checks and is not skipped
        public int Depth;
        public string? Explanation;
        public bool Exists;
    }

    private static AccountNature DefaultNature(AccountType t) => t is AccountType.Asset or AccountType.Expense ? AccountNature.Debit : AccountNature.Credit;
    private static string TypeKey(AccountType t) => t.ToString().ToLowerInvariant();
    private static string NatureKey(AccountNature n) => n.ToString().ToLowerInvariant();
    private static string PolicyKey(AccountCurrencyPolicy p) => p.ToString().ToLowerInvariant();

    // ------------------------------------------------------------------------------------------------------------------------------------------ analysis

    public async Task<AnalysisResult> AnalyzeAsync(ImportAnalysisContext context, IReadOnlyList<NormalizedRow> rows, CancellationToken ct)
    {
        var existing = await LoadExistingAsync(context, ct);
        var enabled = await LoadEnabledCurrenciesAsync(context, ct);
        var drafts = new Dictionary<int, Draft>();
        foreach (var row in rows) drafts[row.RowNo] = Validate(row, enabled);
        var byCode = new Dictionary<string, Draft>(StringComparer.Ordinal);
        foreach (var d in drafts.Values.OrderBy(d => d.Row.RowNo)) if (d.Code.Length > 0) byCode.TryAdd(d.Code, d);

        // 1) rows whose code already exists: exact or conflict (never a change of the existing account)
        foreach (var d in drafts.Values.Where(d => d.Outcome == RowOutcome.Valid && existing.ContainsKey(d.Code)))
        {
            d.Exists = true;
            var e = existing[d.Code];
            var type = d.GivenType ?? e.Type;       // an absent type means "whatever it is": only a stated, different type counts as a difference
            var diffs = new List<string>();
            if (!string.Equals(d.NameAr, e.NameAr, StringComparison.Ordinal)) diffs.Add("name_ar");
            if (!string.Equals(d.NameEn, e.NameEn, StringComparison.Ordinal)) diffs.Add("name_en");
            if (!string.Equals(d.ParentCode, e.ParentCode, StringComparison.Ordinal)) diffs.Add("parent_code");
            if (d.GivenType is { } gt && gt != e.Type) diffs.Add("type");
            if ((d.GivenNature ?? DefaultNature(type)) != e.Nature) diffs.Add("nature");
            if (d.IsGroup != e.IsGroup) diffs.Add("is_group");
            if (d.Policy != e.Policy || !string.Equals(d.Currency, e.Currency, StringComparison.Ordinal)) diffs.Add("currency");
            if (diffs.Count == 0) { d.Outcome = RowOutcome.Exact; d.Explanation = "exists_identical"; }
            else { d.Outcome = RowOutcome.Conflict; d.Issues.Add(new RowIssue("code", "exists_with_differences", string.Join(',', diffs))); d.Explanation = "exists_with_differences:" + string.Join(',', diffs); }
        }

        // 2) hierarchy of the rows that would be created: cycles first, then parents, types and depth, parents before children (iterative: a hostile file may nest 50,000 levels)
        var candidates = drafts.Values.Where(d => d.Outcome == RowOutcome.Valid).OrderBy(d => d.Row.RowNo).ToList();
        MarkCycles(candidates, byCode, existing);
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in candidates)
        {
            var stack = new Stack<Draft>();
            for (var cur = d; cur is not null && !done.Contains(cur.Code);)
            {
                stack.Push(cur);
                cur = cur.ParentCode is { } pc && !existing.ContainsKey(pc) && byCode.TryGetValue(pc, out var next) && next.Outcome == RowOutcome.Valid ? next : null;
            }
            while (stack.Count > 0) { var x = stack.Pop(); ResolveParent(x, byCode, existing); done.Add(x.Code); }
        }

        // 3) ambiguous matches: a NEW code whose names equal an existing sibling's
        var existingByParent = existing.Values.GroupBy(e => e.ParentCode ?? "").ToDictionary(g => g.Key, g => g.OrderBy(e => e.Code, StringComparer.Ordinal).ToList());
        foreach (var d in candidates.Where(d => d.Outcome == RowOutcome.Valid).OrderBy(d => d.Row.RowNo))
        {
            if (!existingByParent.TryGetValue(d.ParentCode ?? "", out var siblings)) continue;
            var same = siblings.FirstOrDefault(s => SameName(d.NameAr, s.NameAr) || SameName(d.NameEn, s.NameEn));
            if (same is null) continue;
            d.Outcome = RowOutcome.Conflict;
            d.Issues.Add(new RowIssue("name", "possible_duplicate_of_existing", same.Code));
            d.Explanation = "possible_duplicate_of_existing:" + same.Code;
        }

        // 4) explicit skips (the framework turns a skipped conflict into "resolved"): such a row creates nothing, so rows below it lose their parent
        bool Skipped(Draft d) => d.Outcome == RowOutcome.Conflict && d.Row.Resolution == ImportResolutions.Skip;
        foreach (var d in candidates.OrderBy(d => d.Depth).ThenBy(d => d.Row.RowNo))
        {
            if (d.Outcome != RowOutcome.Valid || d.ParentCode is null) continue;
            if (existing.ContainsKey(d.ParentCode)) continue;
            if (!byCode.TryGetValue(d.ParentCode, out var p)) continue;
            if (p.Outcome == RowOutcome.Invalid) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue("parent_code", "parent_row_invalid", d.ParentCode)); }
            else if (Skipped(p) || p.Outcome == RowOutcome.Conflict)
            {
                // the parent will not be created (skipped) or is still undecided: the child cannot be created either, which is a decision of its own (it can be skipped too), never a silent loss
                var code = Skipped(p) ? "parent_row_skipped" : "parent_row_unresolved";
                d.Outcome = RowOutcome.Conflict; d.Issues.Add(new RowIssue("parent_code", code, d.ParentCode)); d.Explanation = code;
            }
        }

        // 5) the answer for every row, the operations in parents-first order and the reconciliation
        var answers = new List<RowAnalysis>();
        int created = 0, groups = 0, exact = 0;
        foreach (var d in drafts.Values.OrderBy(d => d.Row.RowNo))
        {
            switch (d.Outcome)
            {
                case RowOutcome.Valid:
                    created++; if (d.IsGroup) groups++;
                    answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Valid, Operation: Operation(d), Explanation: "will_be_created", Sequence: d.Depth));
                    break;
                case RowOutcome.Exact:
                    exact++; answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Exact, Explanation: d.Explanation));
                    break;
                case RowOutcome.Conflict:
                    answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Conflict, d.Issues, [ImportResolutions.Skip], Explanation: d.Explanation));
                    break;
                default:
                    answers.Add(new RowAnalysis(d.Row.RowNo, RowOutcome.Invalid, d.Issues));
                    break;
            }
        }
        var recon = new List<ReconLine>
        {
            new("accounts_to_create", created.ToString(CultureInfo.InvariantCulture)), new("groups_to_create", groups.ToString(CultureInfo.InvariantCulture)),
            new("postable_to_create", (created - groups).ToString(CultureInfo.InvariantCulture)), new("existing_matched", exact.ToString(CultureInfo.InvariantCulture)),
            new("existing_total", existing.Count.ToString(CultureInfo.InvariantCulture)),
        };
        foreach (var t in Enum.GetValues<AccountType>())
            recon.Add(new ReconLine("to_create_" + TypeKey(t), drafts.Values.Count(d => d.Outcome == RowOutcome.Valid && d.Type == t).ToString(CultureInfo.InvariantCulture)));
        return new AnalysisResult(answers, Baseline(existing, enabled), recon);
    }

    private static bool SameName(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private static Draft Validate(NormalizedRow row, HashSet<string> enabled)
    {
        var d = new Draft(row);
        var input = new AccountInput(row.Get("code"), null, row.Get("name_ar"), row.Get("name_en"), row.Get("type"), row.Get("nature"), ParseBool(row.Get("is_group")), row.Get("currency_policy"), row.Get("currency_code"));
        if (!ConfigurationValidation.TryNormalizeAccount(input, out var code, out var nameAr, out var nameEn, out var type, out var nature, out var isGroup, out var policy, out var currency, out var errors))
        {
            d.Code = (row.Get("code") ?? "").ToUpperInvariant();
            d.Outcome = RowOutcome.Invalid;
            d.Issues.AddRange(errors.Select(e => new RowIssue(Field(e.Field), e.Code)));
            return d;
        }
        d.Code = code ?? ""; d.NameAr = nameAr; d.NameEn = nameEn; d.GivenType = type; d.GivenNature = nature; d.IsGroup = isGroup; d.Policy = policy; d.Currency = currency;
        if (code is null) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue("code", "required")); return d; }
        if (row.Get("parent_code") is { Length: > 0 } rawParent)
        {
            var parent = ConfigurationValidation.NormalizeAccountCode(rawParent);
            if (parent is null) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue("parent_code", "invalid_format")); }
            else if (parent == code) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue("parent_code", "parent_is_self")); }
            else d.ParentCode = parent;
        }
        if (policy == AccountCurrencyPolicy.Specific && currency is not null && !enabled.Contains(currency)) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue("currency_code", "currency_not_enabled")); }
        if (d.ParentCode is null && type is null && d.Outcome == RowOutcome.Valid) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue("type", "required")); }
        d.Type = type;
        return d;
    }

    private static string Field(string domainField) => domainField switch { "nameAr" => "name_ar", "nameEn" => "name_en", "currencyPolicy" => "currency_policy", "currencyCode" => "currency_code", "isGroup" => "is_group", _ => domainField };

    private static bool? ParseBool(string? v) => v switch { "true" => true, "false" => false, _ => null };

    /// <summary>Resolves the parent of a draft whose parent row (if any) is already resolved: sets the effective type and the depth (parents are always shallower than children).</summary>
    private static void ResolveParent(Draft d, Dictionary<string, Draft> byCode, Dictionary<string, Existing> existing)
    {
        if (d.Outcome != RowOutcome.Valid) return;
        if (d.ParentCode is null) { d.Type = d.GivenType; d.Depth = 0; return; }
        if (existing.TryGetValue(d.ParentCode, out var e))
        {
            if (!e.IsGroup) { Fail(d, "parent_code", "parent_not_group"); return; }
            if (!e.IsActive) { Fail(d, "parent_code", "parent_inactive"); return; }
            if (d.GivenType is { } gt && gt != e.Type) { Fail(d, "type", "parent_type_mismatch"); return; }
            d.Type = e.Type; d.Depth = 0;
            return;
        }
        if (!byCode.TryGetValue(d.ParentCode, out var p)) { Fail(d, "parent_code", "parent_not_found"); return; }
        d.Depth = p.Depth + 1;
        if (p.Outcome == RowOutcome.Invalid) return;                    // reported as parent_row_invalid in the propagation step
        if (!p.IsGroup) { Fail(d, "parent_code", "parent_not_group"); return; }
        if (d.GivenType is { } t && p.Type is { } pt && t != pt) { Fail(d, "type", "parent_type_mismatch"); return; }
        d.Type = p.Type ?? d.GivenType;
    }

    /// <summary>Marks every row that lies ON a parent cycle among the file's own rows invalid (each row has at most one parent, so one linear walk with path tracking finds them all).</summary>
    private static void MarkCycles(List<Draft> candidates, Dictionary<string, Draft> byCode, Dictionary<string, Existing> existing)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in candidates)
        {
            if (done.Contains(start.Code)) continue;
            var path = new List<Draft>(); var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var cur = start;
            while (cur is not null && !done.Contains(cur.Code))
            {
                if (index.TryGetValue(cur.Code, out var at)) { for (var i = at; i < path.Count; i++) Fail(path[i], "parent_code", "parent_cycle"); break; }
                index[cur.Code] = path.Count; path.Add(cur);
                cur = cur.ParentCode is { } pc && !existing.ContainsKey(pc) && byCode.TryGetValue(pc, out var next) ? next : null;
            }
            foreach (var p in path) done.Add(p.Code);
        }
    }

    private static void Fail(Draft d, string field, string code) { d.Outcome = RowOutcome.Invalid; d.Issues.Add(new RowIssue(field, code, d.ParentCode)); }

    private static JsonObject Operation(Draft d) => new()
    {
        ["type"] = "create-account", ["code"] = d.Code, ["parentCode"] = d.ParentCode, ["nameAr"] = d.NameAr, ["nameEn"] = d.NameEn,
        ["accountType"] = TypeKey(d.Type!.Value), ["nature"] = NatureKey(d.GivenNature ?? DefaultNature(d.Type.Value)), ["isGroup"] = d.IsGroup, ["currencyPolicy"] = PolicyKey(d.Policy), ["currencyCode"] = d.Currency,
    };

    private static async Task<Dictionary<string, Existing>> LoadExistingAsync(ImportAnalysisContext c, CancellationToken ct)
    {
        var result = new Dictionary<string, Existing>(StringComparer.Ordinal);
        await using var q = new NpgsqlCommand(
            $"SELECT a.{A.Id}, a.{A.Code}, p.{A.Code}, a.{A.NameAr}, a.{A.NameEn}, a.{A.Type}, a.{A.Nature}, a.{A.IsGroup}, a.{A.IsActive}, a.{A.CurrencyPolicy}, a.{A.CurrencyCode} " +
            $"FROM {A.Table} a LEFT JOIN {A.Table} p ON p.{A.Id} = a.{A.ParentId} ORDER BY a.{A.Code}", c.Connection, c.Transaction);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var e = new Existing(r.GetGuid(0), r.GetString(1), S(2), S(3), S(4), (AccountType)r.GetInt16(5), (AccountNature)r.GetInt16(6), r.GetBoolean(7), r.GetBoolean(8), (AccountCurrencyPolicy)r.GetInt16(9), S(10)?.Trim());
            result[e.Code] = e;
        }
        return result;
    }

    private static async Task<HashSet<string>> LoadEnabledCurrenciesAsync(ImportAnalysisContext c, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var q = new NpgsqlCommand($"SELECT {CC.Code} FROM {CC.Table} WHERE {CC.Enabled} ORDER BY {CC.Code}", c.Connection, c.Transaction);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0).Trim());
        return set;
    }

    /// <summary>Fingerprint of everything the analysis read: any change to the existing chart or to the enabled currencies makes the plan stale.</summary>
    private static string Baseline(Dictionary<string, Existing> existing, HashSet<string> enabled)
    {
        var sb = new StringBuilder();
        foreach (var e in existing.Values.OrderBy(e => e.Code, StringComparer.Ordinal))
            sb.Append(e.Code).Append('|').Append(e.ParentCode).Append('|').Append(e.NameAr).Append('|').Append(e.NameEn).Append('|').Append((short)e.Type).Append('|').Append((short)e.Nature).Append('|')
              .Append(e.IsGroup ? 1 : 0).Append('|').Append(e.IsActive ? 1 : 0).Append('|').Append((short)e.Policy).Append('|').Append(e.Currency).Append('\n');
        sb.Append("currencies:").Append(string.Join(',', enabled.OrderBy(x => x, StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ application

    public Task BeginApplyAsync(CompanyAuditedWork work, CancellationToken ct) => chart.LockForImportAsync(work, ct);

    public async Task<ImportApplyResult> ApplyAsync(ImportApplyContext context, CancellationToken ct)
    {
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        int created = 0, groups = 0;
        foreach (var op in context.Operations.OrderBy(o => o.Sequence).ThenBy(o => o.RowNo))
        {
            var o = op.Operation;
            Guid? parentId = null;
            if (o["parentCode"]?.GetValue<string>() is { } parentCode)
            {
                if (ids.TryGetValue(parentCode, out var pid)) parentId = pid;
                else parentId = await FindAsync(context.Work, parentCode, ct) ?? throw new ImportApplyException("parent_missing", $"row {op.RowNo}: the parent account does not exist");
            }
            var input = new AccountInput(o["code"]!.GetValue<string>(), parentId, o["nameAr"]?.GetValue<string>(), o["nameEn"]?.GetValue<string>(), o["accountType"]!.GetValue<string>(),
                o["nature"]!.GetValue<string>(), o["isGroup"]!.GetValue<bool>(), o["currencyPolicy"]!.GetValue<string>(), o["currencyCode"]?.GetValue<string>());
            try
            {
                var account = await chart.CreateInWorkAsync(context.Work, context.Actor, input, ct);
                ids[account.Code] = account.Id;
                created++; if (account.IsGroup) groups++;
            }
            catch (ChartOperationException ex)
            {
                throw new ImportApplyException(ex.Code, $"row {op.RowNo}: {ex.Code}" + (ex.Fields is { Count: > 0 } f ? " (" + string.Join(',', f.Select(x => $"{x.Field}:{x.Code}")) + ")" : ""));
            }
        }
        return new ImportApplyResult(new JsonObject { ["created"] = created, ["groups"] = groups, ["postable"] = created - groups });
    }

    private static async Task<Guid?> FindAsync(CompanyAuditedWork work, string code, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand($"SELECT {A.Id} FROM {A.Table} WHERE {A.Code} = @c", work.Connection, work.Transaction);
        q.Parameters.AddWithValue("c", code);
        return await q.ExecuteScalarAsync(ct) is Guid g ? g : null;
    }
}
