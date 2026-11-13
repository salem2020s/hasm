namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Code-defined chart templates. Phase 3.2A ships ONE well-defined generic starter template; the SRS also requires business-type templates (an explicit
/// Release 1 requirement, still outstanding: each needs its own reviewed content). Applying a template is an audited user action on an EMPTY chart:
/// the accounts it creates are ordinary accounts that can be renamed, re-coded, moved and deactivated afterwards.
/// </summary>
public static class ChartTemplates
{
    public const string Generic = "generic";

    private static TemplateAccount G(string code, string? parent, string ar, string en, AccountType type, bool group = false, AccountNature? nature = null) =>
        new(code, parent, ar, en, type, nature ?? ConfigurationValidation.DefaultNature(type), group);

    private static readonly ChartTemplate GenericTemplate = new(Generic, "دليل حسابات عام", "Generic chart of accounts",
    [
        G("1", null, "الأصول", "Assets", AccountType.Asset, true),
        G("11", "1", "الأصول المتداولة", "Current assets", AccountType.Asset, true),
        G("1101", "11", "النقدية في الصندوق", "Cash on hand", AccountType.Asset),
        G("1102", "11", "الحسابات البنكية", "Bank accounts", AccountType.Asset),
        G("1103", "11", "الذمم المدينة", "Accounts receivable", AccountType.Asset),
        G("1104", "11", "المخزون", "Inventory", AccountType.Asset),
        G("1105", "11", "مصاريف مدفوعة مقدماً", "Prepaid expenses", AccountType.Asset),
        G("1106", "11", "ضرائب مدينة", "Taxes receivable", AccountType.Asset),
        G("12", "1", "الأصول غير المتداولة", "Non-current assets", AccountType.Asset, true),
        G("1201", "12", "الممتلكات والمعدات", "Property and equipment", AccountType.Asset),
        G("1202", "12", "مجمع الإهلاك", "Accumulated depreciation", AccountType.Asset, nature: AccountNature.Credit),
        G("2", null, "الخصوم", "Liabilities", AccountType.Liability, true),
        G("21", "2", "الخصوم المتداولة", "Current liabilities", AccountType.Liability, true),
        G("2101", "21", "الذمم الدائنة", "Accounts payable", AccountType.Liability),
        G("2102", "21", "مصاريف مستحقة", "Accrued expenses", AccountType.Liability),
        G("2103", "21", "ضرائب مستحقة", "Taxes payable", AccountType.Liability),
        G("2104", "21", "قروض قصيرة الأجل", "Short-term loans", AccountType.Liability),
        G("22", "2", "الخصوم غير المتداولة", "Non-current liabilities", AccountType.Liability, true),
        G("2201", "22", "قروض طويلة الأجل", "Long-term loans", AccountType.Liability),
        G("3", null, "حقوق الملكية", "Equity", AccountType.Equity, true),
        G("3101", "3", "رأس المال", "Capital", AccountType.Equity),
        G("3102", "3", "الأرباح المحتجزة", "Retained earnings", AccountType.Equity),
        G("3103", "3", "جاري الشركاء", "Owners' current account", AccountType.Equity),
        G("4", null, "الإيرادات", "Revenue", AccountType.Revenue, true),
        G("4101", "4", "إيرادات المبيعات", "Sales revenue", AccountType.Revenue),
        G("4102", "4", "مرتجعات وخصومات المبيعات", "Sales returns and discounts", AccountType.Revenue, nature: AccountNature.Debit),
        G("4103", "4", "إيرادات أخرى", "Other income", AccountType.Revenue),
        G("5", null, "المصروفات", "Expenses", AccountType.Expense, true),
        G("51", "5", "تكلفة المبيعات", "Cost of sales", AccountType.Expense, true),
        G("5101", "51", "تكلفة البضاعة المباعة", "Cost of goods sold", AccountType.Expense),
        G("52", "5", "المصروفات التشغيلية", "Operating expenses", AccountType.Expense, true),
        G("5201", "52", "الرواتب والأجور", "Salaries and wages", AccountType.Expense),
        G("5202", "52", "الإيجار", "Rent", AccountType.Expense),
        G("5203", "52", "المرافق", "Utilities", AccountType.Expense),
        G("5204", "52", "مصروف الإهلاك", "Depreciation expense", AccountType.Expense),
        G("5205", "52", "رسوم بنكية", "Bank charges", AccountType.Expense),
        G("5206", "52", "فروقات العملة", "Foreign exchange differences", AccountType.Expense),
        G("5207", "52", "مصروفات أخرى", "Other expenses", AccountType.Expense),
    ]);

    public static IReadOnlyList<ChartTemplate> All { get; } = [GenericTemplate];

    public static ChartTemplate? Find(string? key) => All.FirstOrDefault(t => string.Equals(t.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));
}
