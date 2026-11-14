using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Reporting;

/// <summary>
/// The presentation sections of the financial statements. Persisted as numbers in the statement mapping (never renumber). The Chart of Accounts stays the accounting structure; this classification is a SEPARATE,
/// company-level, versioned mapping of accounts to these sections. Balance-sheet sections are below 10, profit-and-loss sections 10 and above.
/// </summary>
public enum StatementSection : short
{
    CurrentAssets = 1, NonCurrentAssets = 2, CurrentLiabilities = 3, NonCurrentLiabilities = 4, EquityCapital = 5, EquityRetained = 6, EquityOther = 7,
    OperatingRevenue = 10, RevenueReductions = 11, CostOfSales = 12, OperatingExpenses = 13, OtherIncome = 14, FinanceAndOtherExpenses = 15, DirectServiceCosts = 16,
}

public static class StatementSections
{
    public static readonly IReadOnlyList<StatementSection> All = [.. Enum.GetValues<StatementSection>()];

    public static string Key(StatementSection s) => s switch
    {
        StatementSection.CurrentAssets => "current_assets", StatementSection.NonCurrentAssets => "non_current_assets", StatementSection.CurrentLiabilities => "current_liabilities",
        StatementSection.NonCurrentLiabilities => "non_current_liabilities", StatementSection.EquityCapital => "equity_capital", StatementSection.EquityRetained => "equity_retained",
        StatementSection.EquityOther => "equity_other", StatementSection.OperatingRevenue => "operating_revenue", StatementSection.RevenueReductions => "revenue_reductions",
        StatementSection.CostOfSales => "cost_of_sales", StatementSection.OperatingExpenses => "operating_expenses", StatementSection.OtherIncome => "other_income",
        StatementSection.FinanceAndOtherExpenses => "finance_and_other_expenses", StatementSection.DirectServiceCosts => "direct_service_costs", _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static bool TryParse(string? key, out StatementSection section)
    {
        foreach (var s in All) if (string.Equals(Key(s), key?.Trim(), StringComparison.OrdinalIgnoreCase)) { section = s; return true; }
        section = default; return false;
    }

    public static bool IsBalanceSheet(StatementSection s) => (short)s < 10;

    /// <summary>The sections an account of a type may be presented in (PostgreSQL enforces the same table).</summary>
    public static IReadOnlyList<StatementSection> For(AccountType type) => type switch
    {
        AccountType.Asset => [StatementSection.CurrentAssets, StatementSection.NonCurrentAssets],
        AccountType.Liability => [StatementSection.CurrentLiabilities, StatementSection.NonCurrentLiabilities],
        AccountType.Equity => [StatementSection.EquityCapital, StatementSection.EquityRetained, StatementSection.EquityOther],
        AccountType.Revenue => [StatementSection.OperatingRevenue, StatementSection.RevenueReductions, StatementSection.OtherIncome],
        AccountType.Expense => [StatementSection.CostOfSales, StatementSection.DirectServiceCosts, StatementSection.OperatingExpenses, StatementSection.FinanceAndOtherExpenses],
        _ => [],
    };

    public static bool Allows(AccountType type, StatementSection section) => For(type).Contains(section);
}

/// <summary>An account as the statements see it: identity (never the code), place in the tree, type and nature.</summary>
public sealed record StatementAccount(Guid Id, string Code, string? NameAr, string? NameEn, Guid? ParentId, AccountType Type, AccountNature Nature, bool IsGroup, string? TemplateKey);

/// <summary>The default mapping derived from the approved starter templates for the accounts that came from one. Only template accounts get a default (a user-created account is never assumed to be classified).</summary>
public static class StatementDefaults
{
    private static readonly Dictionary<string, StatementSection> Generic = new()
    {
        ["11"] = StatementSection.CurrentAssets, ["12"] = StatementSection.NonCurrentAssets, ["21"] = StatementSection.CurrentLiabilities, ["22"] = StatementSection.NonCurrentLiabilities,
        ["3101"] = StatementSection.EquityCapital, ["3102"] = StatementSection.EquityRetained, ["3103"] = StatementSection.EquityOther,
        ["4101"] = StatementSection.OperatingRevenue, ["4102"] = StatementSection.RevenueReductions, ["4103"] = StatementSection.OtherIncome,
        ["51"] = StatementSection.CostOfSales, ["52"] = StatementSection.OperatingExpenses, ["5205"] = StatementSection.FinanceAndOtherExpenses, ["5206"] = StatementSection.FinanceAndOtherExpenses,
    };

    private static readonly Dictionary<string, StatementSection> Business = new()
    {
        ["11"] = StatementSection.CurrentAssets, ["12"] = StatementSection.NonCurrentAssets, ["21"] = StatementSection.CurrentLiabilities, ["22"] = StatementSection.NonCurrentLiabilities,
        ["3101"] = StatementSection.EquityCapital, ["3102"] = StatementSection.EquityRetained, ["3103"] = StatementSection.EquityOther,
        ["41"] = StatementSection.OperatingRevenue, ["42"] = StatementSection.OtherIncome,
        ["52"] = StatementSection.OperatingExpenses, ["5213"] = StatementSection.FinanceAndOtherExpenses, ["5215"] = StatementSection.FinanceAndOtherExpenses,
    };

    /// <summary>One default entry per matched account (groups map their whole subtree through inheritance; a more specific mapping on a descendant wins).</summary>
    public static IReadOnlyDictionary<Guid, StatementSection> Propose(IReadOnlyList<StatementAccount> accounts)
    {
        var result = new Dictionary<Guid, StatementSection>();
        foreach (var a in accounts)
        {
            if (a.TemplateKey is null) continue;
            var service = a.TemplateKey == "service";
            var table = a.TemplateKey == "generic" ? Generic : Business;
            if (table.TryGetValue(a.Code, out var s)) { result[a.Id] = s; continue; }
            if (a.Type == AccountType.Expense && a.Code == "51" && a.TemplateKey != "generic") result[a.Id] = service ? StatementSection.DirectServiceCosts : StatementSection.CostOfSales;
            // a contra revenue account (a revenue account with a debit nature: returns, discounts, rebates) reduces revenue; this rule needs no code list
            else if (a.Type == AccountType.Revenue && !a.IsGroup && a.Nature == AccountNature.Debit) result[a.Id] = StatementSection.RevenueReductions;
        }
        return result;
    }
}

/// <summary>Resolves the effective section of every account: its own mapping or the nearest mapped ancestor; an account with neither is unclassified (never dropped, never guessed).</summary>
public static class StatementClassifier
{
    public static IReadOnlyDictionary<Guid, StatementSection?> Resolve(IReadOnlyList<StatementAccount> accounts, IReadOnlyDictionary<Guid, StatementSection> mapping)
    {
        var byId = accounts.ToDictionary(a => a.Id);
        var result = new Dictionary<Guid, StatementSection?>();
        foreach (var a in accounts)
        {
            StatementSection? section = null; var cur = a; var guard = 0;
            while (cur is not null && guard++ < 10_000)
            {
                if (mapping.TryGetValue(cur.Id, out var s) && StatementSections.Allows(a.Type, s)) { section = s; break; }
                cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null;
            }
            result[a.Id] = section;
        }
        return result;
    }
}

public enum StatementKind { ProfitAndLoss = 1, BalanceSheet = 2, TradingAccount = 3 }

/// <summary>One line of a statement. <c>Kind</c>: header, account, subtotal, total, note. Amounts are in the natural presentation sign of the section (revenue and equity credit-positive, assets and expenses debit-positive; contra accounts negative).</summary>
public sealed record StatementLine(string Kind, string Key, Guid? AccountId, string? Code, string? NameAr, string? NameEn, int Level, decimal Current, decimal? Comparative);

public sealed record StatementIssue(string Code, int Count, decimal Amount);
public sealed record StatementCheck(string Code, string Status, decimal Difference);

public sealed record FinancialStatement(
    StatementKind Kind, bool Applicable, string? NotApplicableReason, bool Complete, IReadOnlyList<StatementIssue> Issues, IReadOnlyList<StatementLine> Lines, IReadOnlyList<StatementCheck> Checks);

/// <summary>
/// Builds the statements from SIGNED posted balances per postable account (debit minus credit) and the effective classification. Pure: it reads nothing and never invents a figure. Every postable account with a balance
/// appears exactly once: in its section, or in an explicit "unclassified" block that also marks the statement incomplete. The statement reconciles with the totals of the balances it was given.
/// </summary>
public static class StatementBuilder
{
    private static decimal Natural(StatementAccount a, decimal signed) => a.Type is AccountType.Asset or AccountType.Expense ? signed : -signed;

    private sealed record Row(StatementAccount Account, StatementSection? Section, decimal Current, decimal? Comparative);

    private static List<Row> Rows(IReadOnlyList<StatementAccount> accounts, IReadOnlyDictionary<Guid, StatementSection?> sections, IReadOnlyDictionary<Guid, decimal> current, IReadOnlyDictionary<Guid, decimal>? comparative, Func<StatementAccount, bool> filter)
    {
        var rows = new List<Row>();
        foreach (var a in accounts.Where(a => !a.IsGroup && filter(a)).OrderBy(a => a.Code, StringComparer.OrdinalIgnoreCase))
        {
            var cur = Natural(a, current.GetValueOrDefault(a.Id));
            decimal? cmp = comparative is null ? null : Natural(a, comparative.GetValueOrDefault(a.Id));
            if (cur == 0 && (cmp ?? 0) == 0) continue;
            rows.Add(new Row(a, sections.GetValueOrDefault(a.Id), cur, cmp));
        }
        return rows;
    }

    private static StatementLine Account(Row r, string key) => new("account", key, r.Account.Id, r.Account.Code, r.Account.NameAr, r.Account.NameEn, 1, r.Current, r.Comparative);

    private static void Section(List<StatementLine> lines, List<Row> rows, StatementSection section, string titleKey, bool comparative, out decimal total, out decimal? cmpTotal)
    {
        var mine = rows.Where(r => r.Section == section).ToList();
        total = mine.Sum(r => r.Current); cmpTotal = comparative ? mine.Sum(r => r.Comparative ?? 0) : null;
        lines.Add(new StatementLine("header", titleKey, null, null, null, null, 0, 0, null));
        foreach (var r in mine) lines.Add(Account(r, titleKey));
        lines.Add(new StatementLine("subtotal", "total_" + titleKey, null, null, null, null, 0, total, cmpTotal));
    }

    private static decimal? Add(decimal? a, decimal? b) => a is null || b is null ? null : a + b;
    private static decimal? Sub(decimal? a, decimal? b) => a is null || b is null ? null : a - b;

    private static List<StatementCheck> Checks(List<StatementIssue> issues, decimal statementTotal, decimal sourceTotal, string code)
    {
        var checks = new List<StatementCheck>
        {
            new("classification_complete", issues.Count == 0 ? "passed" : "failed", issues.Sum(i => i.Amount)),
            new(code, statementTotal == sourceTotal ? "passed" : "failed", statementTotal - sourceTotal),
        };
        return checks;
    }

    /// <summary>
    /// Profit and Loss over the period movements (signed, closing entries excluded by the caller). Gross profit appears only when cost of sales or direct service costs are classified. Result =
    /// net revenue - cost of sales - direct service costs - operating expenses + other income - finance and other expenses.
    /// </summary>
    public static FinancialStatement ProfitAndLoss(IReadOnlyList<StatementAccount> accounts, IReadOnlyDictionary<Guid, StatementSection?> sections, IReadOnlyDictionary<Guid, decimal> current, IReadOnlyDictionary<Guid, decimal>? comparative, bool noMapping)
    {
        var cmp = comparative is not null;
        var rows = Rows(accounts, sections, current, comparative, a => a.Type is AccountType.Revenue or AccountType.Expense);
        var lines = new List<StatementLine>(); var issues = new List<StatementIssue>();
        if (noMapping) issues.Add(new StatementIssue("no_mapping", 0, 0));
        Section(lines, rows, StatementSection.OperatingRevenue, "operating_revenue", cmp, out var rev, out var revC);
        Section(lines, rows, StatementSection.RevenueReductions, "revenue_reductions", cmp, out var red, out var redC);
        var net = rev + red; var netC = Add(revC, redC);
        lines.Add(new StatementLine("total", "net_revenue", null, null, null, null, 0, net, netC));
        Section(lines, rows, StatementSection.CostOfSales, "cost_of_sales", cmp, out var cos, out var cosC);
        Section(lines, rows, StatementSection.DirectServiceCosts, "direct_service_costs", cmp, out var dir, out var dirC);
        var hasCost = rows.Any(r => r.Section is StatementSection.CostOfSales or StatementSection.DirectServiceCosts);
        var gross = net - cos - dir; var grossC = Sub(netC, Add(cosC, dirC));
        if (hasCost) lines.Add(new StatementLine("total", "gross_profit", null, null, null, null, 0, gross, grossC));
        Section(lines, rows, StatementSection.OperatingExpenses, "operating_expenses", cmp, out var opx, out var opxC);
        var op = gross - opx; var opC = Sub(grossC, opxC);
        lines.Add(new StatementLine("total", "operating_profit", null, null, null, null, 0, op, opC));
        Section(lines, rows, StatementSection.OtherIncome, "other_income", cmp, out var oth, out var othC);
        Section(lines, rows, StatementSection.FinanceAndOtherExpenses, "finance_and_other_expenses", cmp, out var fin, out var finC);
        var result = op + oth - fin; var resultC = Sub(Add(opC, othC), finC);
        // unclassified revenue is shown with the revenue-side sign, unclassified expenses with the expense-side sign, so each is added to / taken from the result by its own type
        var unRev = rows.Where(r => r.Section is null && r.Account.Type == AccountType.Revenue).ToList();
        var unExp = rows.Where(r => r.Section is null && r.Account.Type == AccountType.Expense).ToList();
        if (unRev.Count + unExp.Count > 0)
        {
            lines.Add(new StatementLine("header", "unclassified", null, null, null, null, 0, 0, null));
            foreach (var r in unRev.Concat(unExp)) lines.Add(Account(r, "unclassified"));
            if (unRev.Count > 0) lines.Add(new StatementLine("subtotal", "total_unclassified_revenue", null, null, null, null, 0, unRev.Sum(r => r.Current), cmp ? unRev.Sum(r => r.Comparative ?? 0) : null));
            if (unExp.Count > 0) lines.Add(new StatementLine("subtotal", "total_unclassified_expenses", null, null, null, null, 0, unExp.Sum(r => r.Current), cmp ? unExp.Sum(r => r.Comparative ?? 0) : null));
            issues.Add(new StatementIssue("unclassified_accounts", unRev.Count + unExp.Count, unRev.Sum(r => r.Current) - unExp.Sum(r => r.Current)));
        }
        var unAdj = unRev.Sum(r => r.Current) - unExp.Sum(r => r.Current);
        var unAdjC = cmp ? unRev.Sum(r => r.Comparative ?? 0) - unExp.Sum(r => r.Comparative ?? 0) : (decimal?)null;
        result += unAdj; resultC = Add(resultC, unAdjC);
        lines.Add(new StatementLine("total", "net_result", null, null, null, null, 0, result, resultC));
        var source = -accounts.Where(a => !a.IsGroup && a.Type is AccountType.Revenue or AccountType.Expense).Sum(a => current.GetValueOrDefault(a.Id));
        return new FinancialStatement(StatementKind.ProfitAndLoss, true, null, issues.Count == 0, issues, lines, Checks(issues, result, source, "result_matches_trial_balance"));
    }

    /// <summary>
    /// The Trading Account / Cost of Sales statement: net sales, cost of sales and gross profit. It applies only when accounts are classified as COST OF SALES (trading); direct service costs belong to the Profit
    /// and Loss and never make this statement applicable. Nothing is inferred from inventory (no inventory operations exist).
    /// </summary>
    public static FinancialStatement TradingAccount(IReadOnlyList<StatementAccount> accounts, IReadOnlyDictionary<Guid, StatementSection?> sections, IReadOnlyDictionary<Guid, decimal> current, IReadOnlyDictionary<Guid, decimal>? comparative, bool noMapping)
    {
        var applicable = sections.Any(kv => kv.Value == StatementSection.CostOfSales);
        if (!applicable) return new FinancialStatement(StatementKind.TradingAccount, false, noMapping ? "no_mapping" : "no_cost_of_sales_accounts", true, [], [], []);
        var cmp = comparative is not null;
        var rows = Rows(accounts, sections, current, comparative, a => a.Type is AccountType.Revenue or AccountType.Expense);
        var lines = new List<StatementLine>(); var issues = new List<StatementIssue>();
        Section(lines, rows, StatementSection.OperatingRevenue, "operating_revenue", cmp, out var rev, out var revC);
        Section(lines, rows, StatementSection.RevenueReductions, "revenue_reductions", cmp, out var red, out var redC);
        var net = rev + red; var netC = Add(revC, redC);
        lines.Add(new StatementLine("total", "net_sales", null, null, null, null, 0, net, netC));
        Section(lines, rows, StatementSection.CostOfSales, "cost_of_sales", cmp, out var cos, out var cosC);
        var gross = net - cos; var grossC = Sub(netC, cosC);
        lines.Add(new StatementLine("total", "gross_profit", null, null, null, null, 0, gross, grossC));
        var unclassifiedRevOrCost = rows.Where(r => r.Section is null).ToList();
        if (unclassifiedRevOrCost.Count > 0) issues.Add(new StatementIssue("unclassified_accounts", unclassifiedRevOrCost.Count, unclassifiedRevOrCost.Sum(r => r.Account.Type == AccountType.Revenue ? r.Current : -r.Current)));
        var sourceNet = accounts.Where(a => !a.IsGroup && sections.GetValueOrDefault(a.Id) is StatementSection.OperatingRevenue or StatementSection.RevenueReductions).Sum(a => -current.GetValueOrDefault(a.Id));
        return new FinancialStatement(StatementKind.TradingAccount, true, null, issues.Count == 0, issues, lines, Checks(issues, net, sourceNet, "net_sales_matches_trial_balance"));
    }

    /// <summary>
    /// The Balance Sheet from CUMULATIVE signed balances at the date (closing entries and carried-forward history included). The result of the Revenue and Expense accounts that is not closed to equity yet is a DERIVED
    /// reporting line (never a posted entry): it is zero after the year-end closing entries and equals the open years' result before them, so nothing is counted twice.
    /// </summary>
    public static FinancialStatement BalanceSheet(IReadOnlyList<StatementAccount> accounts, IReadOnlyDictionary<Guid, StatementSection?> sections, IReadOnlyDictionary<Guid, decimal> current, IReadOnlyDictionary<Guid, decimal>? comparative, bool noMapping, bool restrictedScope)
    {
        var cmp = comparative is not null;
        var rows = Rows(accounts, sections, current, comparative, a => a.Type is AccountType.Asset or AccountType.Liability or AccountType.Equity);
        var lines = new List<StatementLine>(); var issues = new List<StatementIssue>();
        if (noMapping) issues.Add(new StatementIssue("no_mapping", 0, 0));
        Section(lines, rows, StatementSection.CurrentAssets, "current_assets", cmp, out var ca, out var caC);
        Section(lines, rows, StatementSection.NonCurrentAssets, "non_current_assets", cmp, out var nca, out var ncaC);
        var unA = rows.Where(r => r.Section is null && r.Account.Type == AccountType.Asset).ToList();
        if (unA.Count > 0) { lines.Add(new StatementLine("header", "unclassified_assets", null, null, null, null, 0, 0, null)); foreach (var r in unA) lines.Add(Account(r, "unclassified_assets")); lines.Add(new StatementLine("subtotal", "total_unclassified_assets", null, null, null, null, 0, unA.Sum(r => r.Current), cmp ? unA.Sum(r => r.Comparative ?? 0) : null)); issues.Add(new StatementIssue("unclassified_accounts", unA.Count, unA.Sum(r => r.Current))); }
        var assets = ca + nca + unA.Sum(r => r.Current); var assetsC = cmp ? caC + ncaC + unA.Sum(r => r.Comparative ?? 0) : null;
        lines.Add(new StatementLine("total", "total_assets", null, null, null, null, 0, assets, assetsC));
        Section(lines, rows, StatementSection.CurrentLiabilities, "current_liabilities", cmp, out var cl, out var clC);
        Section(lines, rows, StatementSection.NonCurrentLiabilities, "non_current_liabilities", cmp, out var ncl, out var nclC);
        var unL = rows.Where(r => r.Section is null && r.Account.Type == AccountType.Liability).ToList();
        if (unL.Count > 0) { lines.Add(new StatementLine("header", "unclassified_liabilities", null, null, null, null, 0, 0, null)); foreach (var r in unL) lines.Add(Account(r, "unclassified_liabilities")); lines.Add(new StatementLine("subtotal", "total_unclassified_liabilities", null, null, null, null, 0, unL.Sum(r => r.Current), cmp ? unL.Sum(r => r.Comparative ?? 0) : null)); issues.Add(new StatementIssue("unclassified_accounts", unL.Count, unL.Sum(r => r.Current))); }
        var liabilities = cl + ncl + unL.Sum(r => r.Current); var liabilitiesC = cmp ? clC + nclC + unL.Sum(r => r.Comparative ?? 0) : null;
        lines.Add(new StatementLine("total", "total_liabilities", null, null, null, null, 0, liabilities, liabilitiesC));
        Section(lines, rows, StatementSection.EquityCapital, "equity_capital", cmp, out var ec, out var ecC);
        Section(lines, rows, StatementSection.EquityRetained, "equity_retained", cmp, out var er, out var erC);
        Section(lines, rows, StatementSection.EquityOther, "equity_other", cmp, out var eo, out var eoC);
        var unE = rows.Where(r => r.Section is null && r.Account.Type == AccountType.Equity).ToList();
        if (unE.Count > 0) { lines.Add(new StatementLine("header", "unclassified_equity", null, null, null, null, 0, 0, null)); foreach (var r in unE) lines.Add(Account(r, "unclassified_equity")); lines.Add(new StatementLine("subtotal", "total_unclassified_equity", null, null, null, null, 0, unE.Sum(r => r.Current), cmp ? unE.Sum(r => r.Comparative ?? 0) : null)); issues.Add(new StatementIssue("unclassified_accounts", unE.Count, unE.Sum(r => r.Current))); }
        // the derived result of the Revenue and Expense accounts not closed to equity: -(sum of their signed balances)
        decimal Result(IReadOnlyDictionary<Guid, decimal> b) => -accounts.Where(a => !a.IsGroup && a.Type is AccountType.Revenue or AccountType.Expense).Sum(a => b.GetValueOrDefault(a.Id));
        var unclosed = Result(current); decimal? unclosedC = comparative is null ? null : Result(comparative);
        lines.Add(new StatementLine("account", "unclosed_result", null, null, "ربح أو خسارة لم تُقفل بعد", "Profit or loss not yet closed to equity", 1, unclosed, unclosedC));
        var equity = ec + er + eo + unE.Sum(r => r.Current) + unclosed; var equityC = cmp ? ecC + erC + eoC + unE.Sum(r => r.Comparative ?? 0) + unclosedC : null;
        lines.Add(new StatementLine("total", "total_equity", null, null, null, null, 0, equity, equityC));
        lines.Add(new StatementLine("total", "total_liabilities_and_equity", null, null, null, null, 0, liabilities + equity, Add(liabilitiesC, equityC)));
        var difference = assets - liabilities - equity;
        var sourceBs = accounts.Where(a => !a.IsGroup && a.Type is AccountType.Asset or AccountType.Liability or AccountType.Equity).Sum(a => current.GetValueOrDefault(a.Id));
        var statementBs = assets - liabilities - (equity - unclosed);                                   // signed assets minus credit-natured sides, without the derived line, equals the signed sum
        var checks = new List<StatementCheck>
        {
            new("classification_complete", issues.Count == 0 ? "passed" : "failed", issues.Sum(i => i.Amount)),
            new("balance_sheet_matches_trial_balance", statementBs == sourceBs ? "passed" : "failed", statementBs - sourceBs),
            restrictedScope ? new("accounting_equation", "not_applicable", 0) : new("accounting_equation", difference == 0 ? "passed" : "failed", difference),
        };
        return new FinancialStatement(StatementKind.BalanceSheet, true, null, issues.Count == 0, issues, lines, checks);
    }
}
