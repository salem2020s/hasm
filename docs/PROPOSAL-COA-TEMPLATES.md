# PROPOSAL v2 — business-type Chart of Accounts templates (roadmap 3.2A-2)

**Status: PROPOSAL awaiting owner review. NOT approved, NOT implemented.** This version supersedes the earlier two-template draft: it proposes **three** templates — Service, Retail trading, Wholesale trading — as one comparison table. No template content is built into the product until the owner (or the accounting reviewer they appoint) approves the actual account structures below. Arabic names need a native accounting review.

## 1. Compatibility with the existing HASM Chart of Accounts (verified against the 3.2A implementation)
* Same shape as the shipped **generic** template (`ChartTemplates.cs`): codes like `1`, `11`, `1101`; real parent links (never implied by code prefixes); roots are groups; every parent is a **group of the same type**; postable accounts have no children; codes unique and valid (`[A-Z0-9][A-Z0-9._-]{0,29}`).
* Account type A/L/E/R/X sets the statement automatically (A, L, E = Balance Sheet; R, X = Profit and Loss); default nature follows the type (assets and expenses debit; liabilities, equity and revenue credit). **Contra accounts** (accumulated depreciation, sales returns and discounts, purchase returns and rebates) carry an explicit opposite nature, exactly as the generic template does.
* Names follow the generic template's Arabic conventions (الخصوم, الذمم المدينة, الأصول غير المتداولة) so a company can move between templates without vocabulary changes.
* Application rules are unchanged: a starter template applies only to an **empty chart**, in one audited transaction; afterwards every account is ordinary (rename, re-code, move, deactivate). Accounts are never deleted, so a template applies at most once per company. The Excel import (3.2D) remains the way to extend an existing chart.
* The generated structure was checked by a script against these rules for all three templates (unique codes, parent group and type, no postable with children, no redundant nature overrides, no empty groups): Service: 52 accounts (13 groups, 39 postable); Retail: 61 accounts (14 groups, 47 postable); Wholesale: 61 accounts (14 groups, 47 postable).

## 2. Common structure versus jurisdiction-specific content
* **Common accounting structure (in the templates):** assets (cash, bank, receivables, prepaid, fixed assets with accumulated depreciation), liabilities (payables, accruals, salaries, loans), equity (capital, retained earnings, owners' current account), revenue, cost of sales or direct costs, operating expenses, other income.
* **Jurisdiction-specific (deliberately NOT templated):** named VAT/GST/sales-tax accounts, withholding taxes, zakat or income-tax accounts, social-insurance accounts, statutory reserves, e-invoicing clearing accounts and any government chart-of-accounts mapping. The templates carry only two neutral, configurable placeholders — **Taxes receivable (1110)** and **Taxes payable (2105)**, as in the generic template — which the tax module (SRS §6) or the company refines (for example by adding Output VAT and Input VAT child accounts after the tax rules are defined).
* **No fixed accounting standard** is imposed; depth stays shallow (at most three levels, inventory four) so customers can restructure.

## 3. Comparison table (● included, · not included)
Kind: Group = parent/summary account that cannot be posted to; Postable = accepts postings. The same code appears twice only where two templates give it different meanings (the rows then have different columns marked). Notes: *contra account* = explicit opposite nature; *tax* = jurisdiction-specific placeholder; *inventory design* = name/structure must be confirmed when the inventory and purchasing accounting events are designed; *cheques module* = used once SRS §11 exists, harmless before.

| Code | Arabic | English | Type | Nature | Kind | Svc | Ret | Whl | Note |
|---|---|---|---|---|---|---|---|---|---|
| 1 | الأصول | Assets | Asset | Dr | Group | ● | ● | ● |  |
| 11 | الأصول المتداولة | Current assets | Asset | Dr | Group | ● | ● | ● |  |
| 1101 | النقدية في الصندوق | Cash on hand | Asset | Dr | Postable | ● | ● | ● |  |
| 1102 | الحسابات البنكية | Bank accounts | Asset | Dr | Postable | ● | ● | ● |  |
| 1103 | الذمم المدينة | Accounts receivable | Asset | Dr | Postable | ● | ● | ● |  |
| 1104 | شيكات برسم التحصيل | Cheques under collection | Asset | Dr | Postable | ● | ● | ● | cheques module (SRS 11) |
| 1105 | مبالغ بطاقات قيد التحصيل | Card settlements receivable | Asset | Dr | Postable | · | ● | · | retail settlement |
| 1106 | سلف الموظفين | Employee advances | Asset | Dr | Postable | ● | ● | ● |  |
| 1107 | دفعات مقدمة للموردين | Supplier advances | Asset | Dr | Postable | · | ● | ● |  |
| 1108 | مصاريف مدفوعة مقدماً | Prepaid expenses | Asset | Dr | Postable | ● | ● | ● |  |
| 1109 | إيرادات مستحقة | Accrued revenue | Asset | Dr | Postable | ● | · | · |  |
| 1110 | ضرائب مدينة | Taxes receivable | Asset | Dr | Postable | ● | ● | ● | tax, jurisdiction-specific |
| 112 | المخزون | Inventory | Asset | Dr | Group | · | ● | ● | depends on inventory accounting design |
| 11201 | مخزون البضاعة | Merchandise inventory | Asset | Dr | Postable | · | ● | ● | depends on inventory accounting design |
| 11202 | بضاعة بالطريق | Goods in transit | Asset | Dr | Postable | · | · | ● | depends on inventory accounting design |
| 12 | الأصول غير المتداولة | Non-current assets | Asset | Dr | Group | ● | ● | ● |  |
| 1201 | الأثاث والتجهيزات والمعدات | Furniture, fixtures and equipment | Asset | Dr | Postable | ● | ● | ● |  |
| 1202 | الحاسبات والبرمجيات | Computers and software | Asset | Dr | Postable | ● | ● | ● |  |
| 1203 | المركبات | Vehicles | Asset | Dr | Postable | · | ● | ● |  |
| 1204 | مجمع الإهلاك | Accumulated depreciation | Asset | Cr (override) | Postable | ● | ● | ● | contra account |
| 2 | الخصوم | Liabilities | Liability | Cr | Group | ● | ● | ● |  |
| 21 | الخصوم المتداولة | Current liabilities | Liability | Cr | Group | ● | ● | ● |  |
| 2101 | الذمم الدائنة | Accounts payable | Liability | Cr | Postable | ● | ● | ● |  |
| 2102 | شيكات تحت الدفع | Cheques payable | Liability | Cr | Postable | ● | ● | ● | cheques module (SRS 11) |
| 2103 | مصاريف مستحقة | Accrued expenses | Liability | Cr | Postable | ● | ● | ● |  |
| 2104 | رواتب وأجور مستحقة | Salaries and wages payable | Liability | Cr | Postable | ● | ● | ● |  |
| 2105 | ضرائب مستحقة | Taxes payable | Liability | Cr | Postable | ● | ● | ● | tax, jurisdiction-specific |
| 2106 | إيرادات مقبوضة مقدماً | Deferred revenue | Liability | Cr | Postable | ● | · | · |  |
| 2107 | دفعات مقدمة من العملاء | Customer advances | Liability | Cr | Postable | · | ● | ● |  |
| 2108 | بضاعة مستلمة غير مفوترة | Goods received not invoiced | Liability | Cr | Postable | · | ● | ● | depends on inventory accounting design |
| 2109 | قروض قصيرة الأجل | Short-term loans | Liability | Cr | Postable | ● | ● | ● |  |
| 22 | الخصوم غير المتداولة | Non-current liabilities | Liability | Cr | Group | ● | ● | ● |  |
| 2201 | قروض طويلة الأجل | Long-term loans | Liability | Cr | Postable | ● | ● | ● |  |
| 3 | حقوق الملكية | Equity | Equity | Cr | Group | ● | ● | ● |  |
| 3101 | رأس المال | Capital | Equity | Cr | Postable | ● | ● | ● |  |
| 3102 | الأرباح المحتجزة | Retained earnings | Equity | Cr | Postable | ● | ● | ● | year-end result account |
| 3103 | جاري الشركاء | Owners' current account | Equity | Cr | Postable | ● | ● | ● |  |
| 4 | الإيرادات | Revenue | Revenue | Cr | Group | ● | ● | ● |  |
| 41 | إيرادات الخدمات | Service revenue | Revenue | Cr | Group | ● | · | · |  |
| 4101 | إيرادات الخدمات | Service revenue | Revenue | Cr | Postable | ● | · | · |  |
| 4102 | إيرادات العقود والاشتراكات | Contract and retainer revenue | Revenue | Cr | Postable | ● | · | · |  |
| 4103 | خصومات ممنوحة | Discounts allowed | Revenue | Dr (override) | Postable | ● | · | · | contra account |
| 41 | المبيعات | Sales | Revenue | Cr | Group | · | ● | ● |  |
| 4101 | مبيعات البضاعة | Merchandise sales | Revenue | Cr | Postable | · | ● | ● |  |
| 4102 | مرتجعات المبيعات | Sales returns | Revenue | Dr (override) | Postable | · | ● | ● | contra account |
| 4103 | خصومات المبيعات | Sales discounts | Revenue | Dr (override) | Postable | · | ● | ● | contra account |
| 4104 | حوافز وخصومات الكميات | Volume rebates and incentives | Revenue | Dr (override) | Postable | · | · | ● | contra account |
| 42 | إيرادات أخرى | Other income | Revenue | Cr | Group | ● | ● | ● |  |
| 4201 | إيرادات أخرى | Other income | Revenue | Cr | Postable | ● | ● | ● |  |
| 5 | المصروفات | Expenses | Expense | Dr | Group | ● | ● | ● |  |
| 51 | التكاليف المباشرة للخدمات | Direct service costs | Expense | Dr | Group | ● | · | · |  |
| 5101 | مقاولو الباطن والتكاليف المباشرة | Subcontractors and direct costs | Expense | Dr | Postable | ● | · | · |  |
| 51 | تكلفة المبيعات | Cost of sales | Expense | Dr | Group | · | ● | ● |  |
| 5101 | تكلفة البضاعة المباعة | Cost of goods sold | Expense | Dr | Postable | · | ● | ● |  |
| 5102 | فروقات الجرد وتسويات المخزون | Inventory count and adjustment differences | Expense | Dr | Postable | · | ● | ● | depends on inventory accounting design |
| 5103 | مصاريف الشحن والاستيراد | Freight-in and import costs | Expense | Dr | Postable | · | ● | ● | depends on inventory accounting design |
| 5104 | مرتجعات وخصومات وحوافز المشتريات | Purchase returns, discounts and rebates | Expense | Cr (override) | Postable | · | ● | ● | contra account |
| 52 | المصروفات التشغيلية | Operating expenses | Expense | Dr | Group | ● | ● | ● |  |
| 5201 | الرواتب والأجور | Salaries and wages | Expense | Dr | Postable | ● | ● | ● |  |
| 5202 | الإيجارات | Rent | Expense | Dr | Postable | ● | ● | ● |  |
| 5203 | الكهرباء والماء والاتصالات | Utilities and communications | Expense | Dr | Postable | ● | ● | ● |  |
| 5204 | الدعاية والإعلان | Marketing and advertising | Expense | Dr | Postable | ● | ● | ● |  |
| 5205 | الأتعاب المهنية | Professional fees | Expense | Dr | Postable | ● | · | · |  |
| 5206 | السفر والتنقل | Travel and transport | Expense | Dr | Postable | ● | · | · |  |
| 5207 | المصاريف المكتبية والقرطاسية | Office supplies | Expense | Dr | Postable | ● | · | · |  |
| 5205 | النقل والتوصيل | Delivery and transport | Expense | Dr | Postable | · | ● | ● |  |
| 5206 | مصاريف المستودعات والخدمات اللوجستية | Warehousing and logistics | Expense | Dr | Postable | · | · | ● |  |
| 5207 | عمولات المبيعات والمندوبين | Sales commissions | Expense | Dr | Postable | · | · | ● |  |
| 5208 | رسوم معالجة البطاقات | Card processing fees | Expense | Dr | Postable | · | ● | · |  |
| 5209 | مستلزمات المتجر والتغليف | Store supplies and packaging | Expense | Dr | Postable | · | ● | · |  |
| 5210 | فروقات الصندوق (عجز وزيادة) | Cash over and short | Expense | Dr | Postable | · | ● | · |  |
| 5211 | مصروف الإهلاك | Depreciation expense | Expense | Dr | Postable | ● | ● | ● |  |
| 5212 | رسوم بنكية | Bank charges | Expense | Dr | Postable | ● | ● | ● |  |
| 5213 | ديون معدومة | Bad debts | Expense | Dr | Postable | ● | ● | ● |  |
| 5214 | فروقات العملة | Foreign exchange differences | Expense | Dr | Postable | ● | ● | ● |  |
| 5215 | مصروفات أخرى | Other expenses | Expense | Dr | Postable | ● | ● | ● |  |

## 4. What differs between the templates
* **Service (52 accounts (13 groups, 39 postable)):** no inventory, no goods-in-transit or GRNI; adds accrued revenue and deferred revenue, **Direct service costs** (subcontractors) instead of cost of sales, service/contract revenue with discounts allowed (contra), professional fees, travel and office supplies.
* **Retail (61 accounts (14 groups, 47 postable)):** inventory (merchandise), cost of sales group (COGS, count differences, freight-in, purchase returns/rebates contra), sales returns and discounts (contra), **card settlements receivable, card processing fees, cash over/short, store supplies and packaging**, delivery.
* **Wholesale (61 accounts (14 groups, 47 postable)):** same trading core as retail plus **goods in transit, volume rebates and incentives (contra revenue), sales commissions, warehousing and logistics**; no card or till accounts.
* Retail and wholesale share all other accounts, so a business that is both can start from either and add the missing accounts (or import them).

## 5. Consequential accounting assumptions for the owner to confirm
1. **Retained earnings (3102)** is the proposed postable equity account that receives the year-end result (the company still chooses its closing account in 3.2C settings). A separate "current-year result" account is not proposed.
2. **Inventory, GRNI (2108), goods in transit and freight-in (5103)** anticipate the inventory/purchasing phases: freight and import costs may later be capitalised into inventory by the approved costing method, in which case 5103 is used only for non-capitalised costs. Names may change when those accounting events are designed.
3. **Cost of sales as a group (51)** is the group the future Trading Account / Cost of Sales statement (Phase 3.3) reads; services use **Direct service costs** and report no inventory.
4. **Contra accounts are separate postable accounts** (sales returns, discounts, rebates, accumulated depreciation) rather than negative postings, so reports show gross and returns separately.
5. **Foreign-exchange differences are one expense account (5214)** (as in the generic template); realized/unrealized FX handling is a later accounting design and may add accounts.
6. **Cheques** accounts (1104, 2102) are included in all templates because cheques are a mandatory Release 1 module; if the owner prefers they can move to the cheques phase.
7. **Cash on hand (1101) is one account**; per-cashbox or per-branch cash and bank accounts are created by the company as child or sibling accounts (no automatic per-branch accounts).
8. **Currency policy** of every account defaults to the company base currency (the existing default); bank accounts in other currencies are set by the company.
9. Payroll detail (social insurance, end-of-service provisions), zakat/income tax, gift cards and customer loyalty liabilities, VAT sub-accounts, consignment and fixed-asset categories beyond those shown are **not included** (jurisdiction- or business-specific; can be added by the company or a later approved template revision).

## 6. Implementation plan once approved (nothing started)
1. Add `service`, `retail` and `wholesale` entries to `ChartTemplates.All` with the approved data (the generic template stays).
2. Tests: structural invariants for every template (the validation above as unit tests), application on an empty chart through the real service (counts, parents, natures, statements, audit events), refusal on a non-empty chart, single application, bilingual names present, template key stored on accounts.
3. Shared UI: the existing template picker lists the new templates with their Arabic/English names (no platform-specific code); message and component tests.
4. No migration is needed (templates are code-defined data).
