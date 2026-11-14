# PROPOSAL v3 — business-type Chart of Accounts templates (roadmap 3.2A-2)

**Status: architecture and general structure APPROVED by the owner (2026-11-14, with the decisions in section 1). The exact account content below (51 accounts (13 groups, 38 postable) / 62 accounts (14 groups, 48 postable) / 61 accounts (14 groups, 47 postable)) has NOT yet been individually reviewed by the owner and is NOT production-approved.** The templates are therefore not exposed as product options: they exist only as proposal text and as test-side data guarded by structural tests. Nothing is built into `ChartTemplates.All` until the owner approves the table in section 5.

## 1. Owner decisions incorporated (2026-11-14)
1. **Retained earnings (3102)** is the default Retained Earnings account and may serve as the default year-end profit/loss transfer account. It is **never hardcoded** in the posting engine: the company-level year-end equity account setting (3.2C) stays authoritative. No separate Current-Year Result account by default; the current-year result stays reportable before closing from the Revenue and Expense accounts of the open year (3.2C behavior, unchanged).
2. **Inventory-related structures approved as provisional** for Retail and Wholesale: Inventory (group) with Merchandise inventory and Goods in transit, Goods received not invoiced (GRNI), Cost of goods sold, Freight-in and import costs. No automatic inventory accounting behavior is created in this phase, and no assumption is made about purchase receipt, invoicing or inventory-posting events; freight-in and other attributable acquisition costs must stay compatible with later inventory capitalization under the approved FIFO / moving-weighted-average options (5103 then receives only costs that are not capitalized).
3. **Cheque accounts removed from all templates** (the earlier 1104 Cheques under collection and 2102 Cheques payable are gone). Users create suitable accounts manually through the existing authorized Chart of Accounts screens. The Release 1 Cheques module must provide a **safe, optional account-extension/setup workflow for existing charts** (it does not reapply a starter template, which stays empty-chart-only).
4. **Foreign exchange: separate gains and losses.** *Foreign exchange gains* (Revenue type, Profit-and-Loss income, under **Other income**, never under Sales or Service revenue) and *Foreign exchange losses* (Expense type, under Operating expenses). The single combined FX expense account is removed. The later FX accounting phase may add realized/unrealized separation (as additional accounts or sub-accounts); no FX settlement or revaluation posting is implemented here.
5. **Terminology review done** (section 4).
6. **Validation done** (section 3) and kept as automated tests. The shipped generic template is unchanged.

## 2. Compatibility with the existing HASM Chart of Accounts
* Same shape as the generic template (`ChartTemplates.cs`): codes like `1`, `11`, `1101`; explicit parent links (never implied by code prefixes); roots are groups; every parent is a **group of the same type**; postable accounts have no children; normalized unique codes.
* The statement follows the type: Asset, Liability, Equity → Balance Sheet; Revenue, Expense → Profit and Loss. Default nature follows the type (assets, expenses: debit; liabilities, equity, revenue: credit); **contra accounts** are postable accounts with the opposite nature, as in the generic template.
* A starter template applies only to an **empty chart**, in one audited transaction, at most once per company; afterwards every account is ordinary. The Excel import (3.2D) extends an existing chart.
* **Jurisdiction-specific content is not templated**: no named VAT/GST/sales tax, withholding, zakat or income-tax accounts, social insurance, statutory reserves or e-invoicing accounts. Only two neutral placeholders exist (*Taxes receivable 1109*, *Taxes payable 2104*), as in the generic template, for the tax module (SRS §6) or the company to refine.

## 3. Structural validation (automated)
`ChartTemplateValidator` (product code, pure function, not applied to any data) checks any template against the chart rules, and `ChartTemplateTests` (unit tests) run it on the generic template and on the three proposals: unique normalized codes, existing parents, parent is a group, same type as the parent, no postable account with children, roots are groups, no hierarchy cycles, groups never contra, no empty groups, valid natures, Arabic names contain Arabic letters and English names none, **no hardcoded jurisdiction terms (VAT, GST, zakat, withholding)**. Further tests assert the owner's decisions: no cheque accounts, FX gains/losses separate and gains under Other income, 3102 present and postable, the exact contra-account set per template, the inventory accounts only in the trading templates, the retail/wholesale differences, and that the proposals are **not** registered in `ChartTemplates.All`. Result: Service: 51 accounts (13 groups, 38 postable); Retail: 62 accounts (14 groups, 48 postable); Wholesale: 61 accounts (14 groups, 47 postable).

## 4. Arabic and English terminology review
Principle: classical accounting-textbook Arabic understood across the Levant, Gulf and Egypt; where regions differ the chosen term is the most widely used and the alternative is listed for the reviewer. Names of the generic template were kept where already precise.

| English | Arabic chosen | Alternative / rejected | Reason |
|---|---|---|---|
| Retained earnings | الأرباح المحتجزة | الأرباح المبقاة (equivalent, IFRS-Arabic usage) | Matches the shipped generic template; both are correct, owner may prefer one |
| Sales returns | مردودات المبيعات | مرتجعات المبيعات | "مردودات" is the textbook term for returns (sales/purchases); "مرتجعات" is colloquial |
| Purchase returns | مردودات المشتريات | مرتجعات المشتريات | Same |
| Sales discounts (granted to customers) | الخصم المسموح به | خصم المبيعات (ambiguous) | Textbook term "discount allowed"; unambiguous direction |
| Purchase discounts (received from suppliers) | الخصم المكتسب | خصم المشتريات (ambiguous) | Textbook "discount earned"; keeps the two directions distinct |
| Cost of goods sold | تكلفة البضاعة المباعة | تكلفة المبيعات (the group name) | Account = COGS; group *Cost of sales* = تكلفة المبيعات |
| Accrued revenue | إيرادات مستحقة | — | Earned, not yet invoiced/received (asset) |
| Deferred revenue | إيرادات مؤجلة (مقبوضة مقدماً) | إيرادات مقدمة | Received, not yet earned (liability); the parenthesis removes any doubt versus accrued revenue |
| Goods in transit | بضاعة في الطريق | بضاعة بالطريق | Natural wording, same meaning |
| Goods received not invoiced | بضاعة مستلمة لم تُفوتر | بضاعة مستلمة غير مفوترة | Verb form reads naturally; English keeps the GRNI abbreviation |
| Freight-in and import costs | مصاريف شحن المشتريات وتكاليف الاستيراد | شحن وارد | States that it is freight on purchases (inward), not delivery to customers (that is *النقل والتوصيل*, delivery and transport) |
| Accumulated depreciation | مجمع الإهلاك | مجمع الاستهلاك (also used in Gulf/Maghreb) | Matches the generic template; contra asset |
| Depreciation expense | مصروف الإهلاك | — | Expense counterpart |
| Foreign exchange gains | أرباح فروق أسعار الصرف | أرباح فروقات العملة | "فروق أسعار الصرف" is the precise term for exchange-rate differences |
| Foreign exchange losses | خسائر فروق أسعار الصرف | خسائر فروقات العملة | Same |
| Accounts receivable | الذمم المدينة (العملاء) | العملاء | Both common; the parenthesis helps new users |
| Accounts payable | الذمم الدائنة (الموردون) | الموردون | Same |
| Volume rebates and incentives granted | حوافز وخصومات الكميات الممنوحة | — | Given to customers (contra revenue); the purchase-side counterpart is *الخصم المكتسب وحوافز المشتريات* |
| Cash over and short | عجز وزيادة الصندوق | فروقات الصندوق | Explicit about both directions |
| Card settlements receivable | مبالغ بطاقات الدفع قيد التحصيل | — | Card payments captured, not yet received from the acquirer |
| Owners' current account | جاري الشركاء | جاري المالك (sole owner) | Matches generic template |
| Miscellaneous other income / expenses | إيرادات أخرى متنوعة / مصروفات أخرى متنوعة | — | "Miscellaneous" separates them from the FX accounts in the same group |

A native-speaker accounting reviewer should confirm the regional alternatives before approval.

## 5. Complete account tables for final content review
● = included in the template, · = not included. Kind: Group (cannot be posted to) or Postable. Nature: Dr = debit, Cr = credit; "(contra)" = opposite of the type's normal balance. Statement: BS = Balance Sheet, P&L = Profit and Loss. The same code appears twice only where two templates give it different meanings (different columns marked). Notes: *tax* = jurisdiction-neutral placeholder; *inventory* = provisional pending the inventory accounting design; *retail* = channel-specific; *FX* = realized/unrealized split in the later FX phase.

| Code | Arabic | English | Type | Nature | Statement | Kind | Svc | Ret | Whl | Note |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | الأصول | Assets | Asset | Dr | BS | Group | ● | ● | ● |  |
| 11 | الأصول المتداولة | Current assets | Asset | Dr | BS | Group | ● | ● | ● |  |
| 1101 | النقدية في الصندوق | Cash on hand | Asset | Dr | BS | Postable | ● | ● | ● |  |
| 1102 | الحسابات البنكية | Bank accounts | Asset | Dr | BS | Postable | ● | ● | ● |  |
| 1103 | الذمم المدينة (العملاء) | Accounts receivable | Asset | Dr | BS | Postable | ● | ● | ● |  |
| 1104 | مبالغ بطاقات الدفع قيد التحصيل | Card settlements receivable | Asset | Dr | BS | Postable | · | ● | · | retail |
| 1105 | سلف الموظفين | Employee advances | Asset | Dr | BS | Postable | ● | ● | ● |  |
| 1106 | دفعات مقدمة للموردين | Supplier advances | Asset | Dr | BS | Postable | · | ● | ● |  |
| 1107 | مصاريف مدفوعة مقدماً | Prepaid expenses | Asset | Dr | BS | Postable | ● | ● | ● |  |
| 1108 | إيرادات مستحقة | Accrued revenue | Asset | Dr | BS | Postable | ● | · | · |  |
| 1109 | ضرائب مدينة | Taxes receivable | Asset | Dr | BS | Postable | ● | ● | ● | tax |
| 112 | المخزون | Inventory | Asset | Dr | BS | Group | · | ● | ● | inventory |
| 11201 | مخزون البضاعة | Merchandise inventory | Asset | Dr | BS | Postable | · | ● | ● | inventory |
| 11202 | بضاعة في الطريق | Goods in transit | Asset | Dr | BS | Postable | · | ● | ● | inventory |
| 12 | الأصول غير المتداولة | Non-current assets | Asset | Dr | BS | Group | ● | ● | ● |  |
| 1201 | الأثاث والتجهيزات والمعدات | Furniture, fixtures and equipment | Asset | Dr | BS | Postable | ● | ● | ● |  |
| 1202 | الحاسبات والبرمجيات | Computers and software | Asset | Dr | BS | Postable | ● | ● | ● |  |
| 1203 | المركبات | Vehicles | Asset | Dr | BS | Postable | · | ● | ● |  |
| 1204 | مجمع الإهلاك | Accumulated depreciation | Asset | Cr (contra) | BS | Postable | ● | ● | ● | contra |
| 2 | الخصوم | Liabilities | Liability | Cr | BS | Group | ● | ● | ● |  |
| 21 | الخصوم المتداولة | Current liabilities | Liability | Cr | BS | Group | ● | ● | ● |  |
| 2101 | الذمم الدائنة (الموردون) | Accounts payable | Liability | Cr | BS | Postable | ● | ● | ● |  |
| 2102 | مصاريف مستحقة | Accrued expenses | Liability | Cr | BS | Postable | ● | ● | ● |  |
| 2103 | رواتب وأجور مستحقة | Salaries and wages payable | Liability | Cr | BS | Postable | ● | ● | ● |  |
| 2104 | ضرائب مستحقة | Taxes payable | Liability | Cr | BS | Postable | ● | ● | ● | tax |
| 2105 | إيرادات مؤجلة (مقبوضة مقدماً) | Deferred revenue | Liability | Cr | BS | Postable | ● | · | · |  |
| 2106 | دفعات مقدمة من العملاء | Customer advances | Liability | Cr | BS | Postable | · | ● | ● |  |
| 2107 | بضاعة مستلمة لم تُفوتر | Goods received not invoiced (GRNI) | Liability | Cr | BS | Postable | · | ● | ● | inventory |
| 2108 | قروض قصيرة الأجل | Short-term loans | Liability | Cr | BS | Postable | ● | ● | ● |  |
| 22 | الخصوم غير المتداولة | Non-current liabilities | Liability | Cr | BS | Group | ● | ● | ● |  |
| 2201 | قروض طويلة الأجل | Long-term loans | Liability | Cr | BS | Postable | ● | ● | ● |  |
| 3 | حقوق الملكية | Equity | Equity | Cr | BS | Group | ● | ● | ● |  |
| 3101 | رأس المال | Capital | Equity | Cr | BS | Postable | ● | ● | ● |  |
| 3102 | الأرباح المحتجزة | Retained earnings | Equity | Cr | BS | Postable | ● | ● | ● | default year-end account (configurable) |
| 3103 | جاري الشركاء | Owners' current account | Equity | Cr | BS | Postable | ● | ● | ● |  |
| 4 | الإيرادات | Revenue | Revenue | Cr | P&L | Group | ● | ● | ● |  |
| 41 | إيرادات الخدمات | Service revenue | Revenue | Cr | P&L | Group | ● | · | · |  |
| 4101 | إيرادات الخدمات | Service revenue | Revenue | Cr | P&L | Postable | ● | · | · |  |
| 4102 | إيرادات العقود والاشتراكات | Contract and retainer revenue | Revenue | Cr | P&L | Postable | ● | · | · |  |
| 4103 | الخصم المسموح به | Discounts allowed | Revenue | Dr (contra) | P&L | Postable | ● | · | · | contra |
| 41 | المبيعات | Sales | Revenue | Cr | P&L | Group | · | ● | ● |  |
| 4101 | مبيعات البضاعة | Merchandise sales | Revenue | Cr | P&L | Postable | · | ● | ● |  |
| 4102 | مردودات المبيعات | Sales returns | Revenue | Dr (contra) | P&L | Postable | · | ● | ● | contra |
| 4103 | الخصم المسموح به على المبيعات | Sales discounts allowed | Revenue | Dr (contra) | P&L | Postable | · | ● | ● | contra |
| 4104 | حوافز وخصومات الكميات الممنوحة | Volume rebates and incentives granted | Revenue | Dr (contra) | P&L | Postable | · | · | ● | contra |
| 42 | إيرادات أخرى | Other income | Revenue | Cr | P&L | Group | ● | ● | ● |  |
| 4201 | إيرادات أخرى متنوعة | Miscellaneous other income | Revenue | Cr | P&L | Postable | ● | ● | ● |  |
| 4202 | أرباح فروق أسعار الصرف | Foreign exchange gains | Revenue | Cr | P&L | Postable | ● | ● | ● | FX; realized/unrealized split later |
| 5 | المصروفات | Expenses | Expense | Dr | P&L | Group | ● | ● | ● |  |
| 51 | التكاليف المباشرة للخدمات | Direct service costs | Expense | Dr | P&L | Group | ● | · | · |  |
| 5101 | مقاولو الباطن والتكاليف المباشرة | Subcontractors and direct costs | Expense | Dr | P&L | Postable | ● | · | · |  |
| 51 | تكلفة المبيعات | Cost of sales | Expense | Dr | P&L | Group | · | ● | ● |  |
| 5101 | تكلفة البضاعة المباعة | Cost of goods sold | Expense | Dr | P&L | Postable | · | ● | ● |  |
| 5102 | فروقات الجرد وتسويات المخزون | Inventory count and adjustment differences | Expense | Dr | P&L | Postable | · | ● | ● | inventory |
| 5103 | مصاريف شحن المشتريات وتكاليف الاستيراد | Freight-in and import costs | Expense | Dr | P&L | Postable | · | ● | ● | inventory |
| 5104 | مردودات المشتريات | Purchase returns | Expense | Cr (contra) | P&L | Postable | · | ● | ● | contra |
| 5105 | الخصم المكتسب وحوافز المشتريات | Purchase discounts and rebates received | Expense | Cr (contra) | P&L | Postable | · | ● | ● | contra |
| 52 | المصروفات التشغيلية | Operating expenses | Expense | Dr | P&L | Group | ● | ● | ● |  |
| 5201 | الرواتب والأجور | Salaries and wages | Expense | Dr | P&L | Postable | ● | ● | ● |  |
| 5202 | الإيجارات | Rent | Expense | Dr | P&L | Postable | ● | ● | ● |  |
| 5203 | الكهرباء والماء والاتصالات | Utilities and communications | Expense | Dr | P&L | Postable | ● | ● | ● |  |
| 5204 | الدعاية والإعلان | Marketing and advertising | Expense | Dr | P&L | Postable | ● | ● | ● |  |
| 5205 | الأتعاب المهنية | Professional fees | Expense | Dr | P&L | Postable | ● | · | · |  |
| 5206 | السفر والتنقل | Travel and transport | Expense | Dr | P&L | Postable | ● | · | · |  |
| 5207 | المصاريف المكتبية والقرطاسية | Office supplies | Expense | Dr | P&L | Postable | ● | · | · |  |
| 5205 | النقل والتوصيل | Delivery and transport | Expense | Dr | P&L | Postable | · | ● | ● |  |
| 5206 | مصاريف المستودعات والخدمات اللوجستية | Warehousing and logistics | Expense | Dr | P&L | Postable | · | · | ● |  |
| 5207 | عمولات المبيعات والمندوبين | Sales commissions | Expense | Dr | P&L | Postable | · | · | ● |  |
| 5208 | رسوم معالجة بطاقات الدفع | Card processing fees | Expense | Dr | P&L | Postable | · | ● | · |  |
| 5209 | مستلزمات المتجر والتغليف | Store supplies and packaging | Expense | Dr | P&L | Postable | · | ● | · |  |
| 5210 | عجز وزيادة الصندوق | Cash over and short | Expense | Dr | P&L | Postable | · | ● | · |  |
| 5211 | مصروف الإهلاك | Depreciation expense | Expense | Dr | P&L | Postable | ● | ● | ● |  |
| 5212 | الرسوم والعمولات البنكية | Bank charges | Expense | Dr | P&L | Postable | ● | ● | ● |  |
| 5213 | ديون معدومة | Bad debts | Expense | Dr | P&L | Postable | ● | ● | ● |  |
| 5214 | خسائر فروق أسعار الصرف | Foreign exchange losses | Expense | Dr | P&L | Postable | ● | ● | ● | FX; realized/unrealized split later |
| 5215 | مصروفات أخرى متنوعة | Miscellaneous other expenses | Expense | Dr | P&L | Postable | ● | ● | ● |  |

### What differs between the templates
* **Service (51 accounts (13 groups, 38 postable)):** no inventory, goods in transit or GRNI; accrued and deferred revenue; *Direct service costs* (subcontractors) instead of cost of sales; discounts allowed (contra); professional fees, travel, office supplies.
* **Retail (62 accounts (14 groups, 48 postable)):** the trading core (inventory, goods in transit, GRNI, cost of sales group, sales returns and discounts, purchase returns and discounts) plus **card settlements receivable, card processing fees, cash over/short, store supplies and packaging**.
* **Wholesale (61 accounts (14 groups, 47 postable)):** the same trading core plus **volume rebates and incentives granted (contra revenue), sales commissions, warehousing and logistics**.
* Retail and wholesale share every other account.

## 6. Remaining points for the owner's content review
1. Approve, edit or remove each account row (exact content not yet reviewed individually).
2. Confirm the terminology choices where alternatives exist (section 4), in particular *الأرباح المحتجزة* versus *المبقاة* and *مجمع الإهلاك* versus *الاستهلاك*.
3. Confirm that *Goods in transit* belongs to Retail as well as Wholesale (it is included in both as provisional).
4. Confirm the shallow hierarchy (at most three levels, inventory four) and the code scheme.
5. Not included on purpose (jurisdiction- or business-specific): payroll detail, end-of-service provisions, zakat/income tax, gift cards and loyalty liabilities, VAT sub-accounts, consignment, further fixed-asset categories.

## 7. Implementation once the content is approved (nothing exposed before)
1. Move the approved data into `ChartTemplates.All` (keys `service`, `retail`, `wholesale`); the generic template stays.
2. The structural and decision tests above keep guarding it; add service-level tests for applying each template to an empty chart (counts, parents, natures, statements, audit events), refusal on a non-empty chart and single application.
3. The existing template picker in the shared UI lists them (no platform-specific code); no migration is needed.
4. Cheques module (later): optional account-extension workflow for existing charts.
