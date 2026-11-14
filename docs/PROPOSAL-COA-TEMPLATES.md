# Business-type Chart of Accounts templates (roadmap 3.2A-2) — APPROVED and IMPLEMENTED

**Status: the owner approved this package (2026-11-14) with two refinements (section K) and the templates are ACTIVE.** They are registered in `ChartTemplates.All` (keys `service`, `retail`, `wholesale`) and offered by the shared Web/Desktop template picker; application remains empty-chart-only. Sizes: **Service 54; Retail 66; Wholesale 64** accounts.

Reading guide: the shared accounts appear once (section A and B); each template's own accounts follow (C, D, E). Kind: Group (cannot be posted to) or Postable. Nature: Dr = debit, Cr = credit; "(contra)" = the opposite of the type's normal balance. Statement: BS = Balance Sheet, P&L = Profit and Loss. A template = A + its own section (+ B for Retail and Wholesale). Codes 41, 4101-4103, 5101, 5205-5207 mean different things in Service and in the trading templates; each is listed where it belongs.

## 1. Owner decisions applied (2026-11-14)
Retained earnings 3102 is the default year-end account and stays configurable per company (not hardcoded); no separate current-year-result account; inventory structures, Goods in transit (**optional, provisional** in Retail and Wholesale), GRNI and Freight-in are provisional and create no automatic behavior; **no cheque accounts** (the Cheques module later provides an optional extension workflow for existing charts); **separate FX gains (Other income) and FX losses (Operating expenses)**, realized/unrealized split left to the FX phase; the approved Arabic terms: الأرباح المحتجزة, مجمع الإهلاك, مردودات المبيعات, مردودات المشتريات; the shipped generic template is unchanged.

## 2. Consolidated accounting review — result
Every item below was verified against the full set of three templates (automated where marked ✔ test):
| Check | Result |
|---|---|
| Type and nature of every account; statement follows the type (Asset/Liability/Equity → BS; Revenue/Expense → P&L) | Correct ✔ test |
| Parent classification: parent is a group of the same type, roots are groups, no cycles, no postable with children, no empty groups, unique normalized codes | Correct ✔ test (pure validator) |
| Contra accounts are postable accounts with the opposite nature: Accumulated depreciation (Cr), Accumulated amortization (Cr), Discounts allowed / Sales returns / Sales discounts allowed / Volume rebates (Dr, revenue side), Purchase returns / Purchase discounts and rebates (Cr, cost-of-sales side). Groups are never contra | Correct ✔ test (exact contra set per template) |
| Sales returns and sales discounts reduce revenue (Revenue type, debit nature); purchase returns and purchase discounts reduce cost of sales (Expense type, credit nature); returns and discounts are distinct accounts on both sides | Correct ✔ test |
| Foreign exchange: gains (Revenue type, Other income group, credit) and losses (Expense type, Operating expenses, debit) are separate; gains are never under Sales/Operating revenue; no combined account | Correct ✔ test |
| Inventory: Inventory group, Merchandise inventory and Goods in transit (assets, debit), GRNI (liability, credit), Cost of goods sold, count differences and Freight-in (expenses in the Cost of sales group); absent from Service, which uses Direct service costs | Correct ✔ test |
| Retained earnings 3102: postable equity account, credit nature | Correct ✔ test |
| Tangible versus intangible: depreciation of tangible assets and amortization of intangible assets are separate (accounts, contra accounts and expenses) | **Corrected in this review** (section G) ✔ test |
| Arabic stored in logical Unicode order | Verified ✔ test (code points) and rendered in the Arabic RTL chart screen ✔ frontend test (section 5) |
| No mandatory jurisdiction-specific tax assumptions: only neutral *Taxes receivable* and *Taxes payable*; no VAT/GST/zakat/withholding terms | Correct ✔ test |
| No specialized cheque accounts | Correct ✔ test |

## A. Shared by all three templates (43 accounts)
| Code | Arabic | English | Type | Nature | Statement | Kind | Note |
|---|---|---|---|---|---|---|---|
| 1 | الأصول | Assets | Asset | Dr | BS | Group |  |
| 11 | الأصول المتداولة | Current assets | Asset | Dr | BS | Group |  |
| 1101 | النقدية في الصندوق | Cash on hand | Asset | Dr | BS | Postable |  |
| 1102 | الحسابات البنكية | Bank accounts | Asset | Dr | BS | Postable |  |
| 1103 | الذمم المدينة (العملاء) | Accounts receivable | Asset | Dr | BS | Postable |  |
| 1105 | سلف الموظفين | Employee advances | Asset | Dr | BS | Postable |  |
| 1107 | مصاريف مدفوعة مقدماً | Prepaid expenses | Asset | Dr | BS | Postable |  |
| 1109 | ضرائب مدينة | Taxes receivable | Asset | Dr | BS | Postable | tax |
| 12 | الأصول غير المتداولة | Non-current assets | Asset | Dr | BS | Group |  |
| 1201 | الأثاث والتجهيزات والمعدات | Furniture, fixtures and equipment | Asset | Dr | BS | Postable |  |
| 1202 | الحاسبات والأجهزة الإلكترونية | Computers and electronic equipment | Asset | Dr | BS | Postable |  |
| 1204 | مجمع الإهلاك | Accumulated depreciation | Asset | Cr (contra) | BS | Postable | contra; tangible assets |
| 1205 | البرمجيات والأصول غير الملموسة | Software and other intangible assets | Asset | Dr | BS | Postable |  |
| 1206 | مجمع الإطفاء | Accumulated amortization | Asset | Cr (contra) | BS | Postable | contra; intangible assets |
| 2 | الخصوم | Liabilities | Liability | Cr | BS | Group |  |
| 21 | الخصوم المتداولة | Current liabilities | Liability | Cr | BS | Group |  |
| 2101 | الذمم الدائنة (الموردون) | Accounts payable | Liability | Cr | BS | Postable |  |
| 2102 | مصاريف مستحقة | Accrued expenses | Liability | Cr | BS | Postable |  |
| 2103 | رواتب وأجور مستحقة | Salaries and wages payable | Liability | Cr | BS | Postable |  |
| 2104 | ضرائب مستحقة | Taxes payable | Liability | Cr | BS | Postable | tax |
| 2108 | قروض قصيرة الأجل | Short-term loans | Liability | Cr | BS | Postable |  |
| 22 | الخصوم غير المتداولة | Non-current liabilities | Liability | Cr | BS | Group |  |
| 2201 | قروض طويلة الأجل | Long-term loans | Liability | Cr | BS | Postable |  |
| 3 | حقوق الملكية | Equity | Equity | Cr | BS | Group |  |
| 3101 | رأس المال | Capital | Equity | Cr | BS | Postable |  |
| 3102 | الأرباح المحتجزة | Retained earnings | Equity | Cr | BS | Postable | default year-end account (configurable) |
| 3103 | جاري الشركاء | Owners' current account | Equity | Cr | BS | Postable |  |
| 4 | الإيرادات | Revenue | Revenue | Cr | P&L | Group |  |
| 42 | إيرادات أخرى | Other income | Revenue | Cr | P&L | Group |  |
| 4201 | إيرادات أخرى متنوعة | Miscellaneous other income | Revenue | Cr | P&L | Postable |  |
| 4202 | أرباح فروق أسعار الصرف | Foreign exchange gains | Revenue | Cr | P&L | Postable | FX; realized/unrealized split later |
| 5 | المصروفات | Expenses | Expense | Dr | P&L | Group |  |
| 52 | المصروفات التشغيلية | Operating expenses | Expense | Dr | P&L | Group |  |
| 5201 | الرواتب والأجور | Salaries and wages | Expense | Dr | P&L | Postable |  |
| 5202 | الإيجارات | Rent | Expense | Dr | P&L | Postable |  |
| 5203 | الكهرباء والماء والاتصالات | Utilities and communications | Expense | Dr | P&L | Postable |  |
| 5204 | الدعاية والإعلان | Marketing and advertising | Expense | Dr | P&L | Postable |  |
| 5211 | مصروف الإهلاك | Depreciation expense | Expense | Dr | P&L | Postable |  |
| 5212 | مصروف الإطفاء | Amortization expense | Expense | Dr | P&L | Postable |  |
| 5213 | الرسوم والعمولات البنكية | Bank charges | Expense | Dr | P&L | Postable |  |
| 5214 | ديون معدومة | Bad debts | Expense | Dr | P&L | Postable |  |
| 5215 | خسائر فروق أسعار الصرف | Foreign exchange losses | Expense | Dr | P&L | Postable | FX; realized/unrealized split later |
| 5216 | مصروفات أخرى متنوعة | Miscellaneous other expenses | Expense | Dr | P&L | Postable |  |

## B. Shared by Retail and Wholesale only (18 accounts)
| Code | Arabic | English | Type | Nature | Statement | Kind | Note |
|---|---|---|---|---|---|---|---|
| 1106 | دفعات مقدمة للموردين | Supplier advances | Asset | Dr | BS | Postable |  |
| 112 | المخزون | Inventory | Asset | Dr | BS | Group | inventory |
| 11201 | مخزون البضاعة | Merchandise inventory | Asset | Dr | BS | Postable | inventory |
| 11202 | بضاعة في الطريق | Goods in transit | Asset | Dr | BS | Postable | inventory; optional, provisional |
| 1203 | المركبات | Vehicles | Asset | Dr | BS | Postable |  |
| 2106 | دفعات مقدمة من العملاء | Customer advances | Liability | Cr | BS | Postable |  |
| 2107 | بضاعة مستلمة لم تُفوتر | Goods received not invoiced (GRNI) | Liability | Cr | BS | Postable | inventory |
| 41 | المبيعات | Sales | Revenue | Cr | P&L | Group |  |
| 4101 | مبيعات البضاعة | Merchandise sales | Revenue | Cr | P&L | Postable |  |
| 4102 | مردودات المبيعات | Sales returns | Revenue | Dr (contra) | P&L | Postable | contra |
| 4103 | الخصم المسموح به على المبيعات | Sales discounts allowed | Revenue | Dr (contra) | P&L | Postable | contra |
| 51 | تكلفة المبيعات | Cost of sales | Expense | Dr | P&L | Group |  |
| 5101 | تكلفة البضاعة المباعة | Cost of goods sold | Expense | Dr | P&L | Postable |  |
| 5102 | فروقات الجرد وتسويات المخزون | Inventory count and adjustment differences | Expense | Dr | P&L | Postable | inventory |
| 5103 | مصاريف شحن المشتريات وتكاليف الاستيراد | Freight-in and import costs | Expense | Dr | P&L | Postable | inventory; capitalized per costing method |
| 5104 | مردودات المشتريات | Purchase returns | Expense | Cr (contra) | P&L | Postable | contra; optional, provisional |
| 5105 | الخصم المكتسب وحوافز المشتريات | Purchase discounts and rebates received | Expense | Cr (contra) | P&L | Postable | contra; optional, provisional |
| 5205 | النقل والتوصيل | Delivery and transport | Expense | Dr | P&L | Postable |  |

## C. Service only (11 accounts)
| Code | Arabic | English | Type | Nature | Statement | Kind | Note |
|---|---|---|---|---|---|---|---|
| 1108 | إيرادات مستحقة | Accrued revenue | Asset | Dr | BS | Postable |  |
| 2105 | إيرادات مؤجلة (مقبوضة مقدماً) | Deferred revenue | Liability | Cr | BS | Postable |  |
| 41 | الإيرادات التشغيلية | Operating revenue | Revenue | Cr | P&L | Group |  |
| 4101 | إيرادات الخدمات | Service revenue | Revenue | Cr | P&L | Postable |  |
| 4102 | إيرادات العقود والاشتراكات | Contract and retainer revenue | Revenue | Cr | P&L | Postable |  |
| 4103 | الخصم المسموح به | Discounts allowed | Revenue | Dr (contra) | P&L | Postable | contra |
| 51 | التكاليف المباشرة للخدمات | Direct service costs | Expense | Dr | P&L | Group |  |
| 5101 | مقاولو الباطن والتكاليف المباشرة | Subcontractors and direct costs | Expense | Dr | P&L | Postable |  |
| 5205 | الأتعاب المهنية | Professional fees | Expense | Dr | P&L | Postable |  |
| 5206 | السفر والتنقل | Travel and transport | Expense | Dr | P&L | Postable |  |
| 5207 | المصاريف المكتبية والقرطاسية | Office supplies | Expense | Dr | P&L | Postable |  |

## D. Retail only (5 accounts)
| Code | Arabic | English | Type | Nature | Statement | Kind | Note |
|---|---|---|---|---|---|---|---|
| 1104 | مبالغ بطاقات الدفع قيد التحصيل | Card settlements receivable | Asset | Dr | BS | Postable | retail |
| 4203 | فائض الصندوق | Cash surpluses | Revenue | Cr | P&L | Postable | retail; differences must be investigated |
| 5208 | رسوم معالجة بطاقات الدفع | Card processing fees | Expense | Dr | P&L | Postable |  |
| 5209 | مستلزمات المتجر والتغليف | Store supplies and packaging | Expense | Dr | P&L | Postable |  |
| 5210 | عجز الصندوق | Cash shortages | Expense | Dr | P&L | Postable | retail; differences must be investigated |

## E. Wholesale only (3 accounts)
| Code | Arabic | English | Type | Nature | Statement | Kind | Note |
|---|---|---|---|---|---|---|---|
| 4104 | حوافز وخصومات الكميات الممنوحة | Volume rebates and incentives granted | Revenue | Dr (contra) | P&L | Postable | contra |
| 5206 | مصاريف المستودعات والخدمات اللوجستية | Warehousing and logistics | Expense | Dr | P&L | Postable |  |
| 5207 | عمولات المبيعات والمندوبين | Sales commissions | Expense | Dr | P&L | Postable |  |

## K. Final owner refinements (applied)
1. **Purchase returns, purchase discounts/rebates and Freight-in are optional, provisional accounts.** The templates do NOT assume these always reduce Cost of goods sold. Under perpetual inventory accounting, qualifying purchase returns, supplier rebates and purchase discounts may instead require adjusting the **inventory acquisition cost**, and qualifying freight-in and import costs must be **capitalized into inventory** according to the applicable accounting rules. The future Inventory module decides the correct treatment under the selected FIFO or moving-weighted-average method; no automatic purchase, return or inventory posting exists.
2. **Cash over and short split.** The combined account is replaced in Retail by two accounts: **5210 Cash shortages** (عجز الصندوق, Expense, debit) and **4203 Cash surpluses** (فائض الصندوق, Revenue/Other income, credit, under group 42). Cash differences must still be investigated and classified; the account names do not replace that review.
3. **Future dependencies recorded:** customer and supplier control accounts must reconcile with their respective subledgers; future banking functionality must maintain General Ledger reconciliation; inventory account mappings stay provisional until the Inventory module defines its posting events. None of these modules is implemented here.

## F. Remaining consequential accounting assumptions
1. **Retained earnings (3102)** is only the proposed default; the closing process always uses the company's configured equity account. Before closing, the current-year result is reported from the open year's Revenue and Expense accounts.
2. **Inventory, Goods in transit, GRNI, Freight-in, Purchase returns and Purchase discounts are provisional.** They commit to no purchase-receipt, invoicing, ownership-transfer or inventory-posting event. Freight-in (5103) is expected to receive only costs that the approved FIFO / moving-weighted-average design does not capitalize into inventory; Goods in transit is optional and its recognition depends on when ownership and control pass.
3. **Cost of sales as a group (51)** is what the future Trading Account / Cost of Sales statement reads; Service uses Direct service costs and reports no inventory.
4. **FX:** gains and losses are single accounts each for now; realized/unrealized separation (additional accounts or sub-accounts) is decided in the FX phase. No FX posting exists yet.
5. **Cheques:** no accounts here; the Cheques module provides an optional extension workflow for existing charts (never a re-applied starter template).
6. **Cash on hand is one account**; per-cashbox/per-branch cash and bank accounts are created by the company. Currency policy defaults to the base currency.
7. **Tax:** only two neutral placeholders; tax accounts proper come from the tax module (SRS §6) or the company.
8. **Not included on purpose** (jurisdiction- or business-specific): payroll detail, end-of-service provisions, zakat/income tax, gift cards and loyalty liabilities, VAT sub-accounts, consignment, further fixed-asset categories.

## G. Exact changes from proposal v3 (and the final refinements)
0. **Final refinements after approval:** Retail *Cash over and short* is replaced by *Cash shortages* (5210) and *Cash surpluses* (4203); purchase returns/discounts/rebates and freight-in are documented as provisional with the capitalization principle (section K). Retail is now 66 accounts.
Substantive corrections (the approved structure is otherwise unchanged):
1. **Tangible/intangible split.** 1202 *Computers and software* → **1202 Computers and electronic equipment** (الحاسبات والأجهزة الإلكترونية, tangible). Added **1205 Software and other intangible assets** (البرمجيات والأصول غير الملموسة), **1206 Accumulated amortization** (مجمع الإطفاء, contra, credit) and **5212 Amortization expense** (مصروف الإطفاء). 1204 Accumulated depreciation now applies to tangible assets only. Following expense codes shifted by one: Bank charges 5212 → 5213, Bad debts 5213 → 5214, FX losses 5214 → 5215, Miscellaneous other expenses 5215 → 5216.
2. **Service revenue group renamed** from *Service revenue* to **Operating revenue** (الإيرادات التشغيلية) so it no longer duplicates the name of postable 4101 *Service revenue*.
3. **Goods in transit** (11202) is labelled optional and provisional in both Retail and Wholesale (content unchanged).
4. **Sizes:** Service 51 → 54, Retail 62 → 66, Wholesale 61 → 64 (three accounts added to each, plus one more for Retail from the cash split).
5. **Arabic terms:** the owner-approved terms (الأرباح المحتجزة, مجمع الإهلاك, مردودات المبيعات, مردودات المشتريات) were already in use and are unchanged. The Arabic text in the previous report looked reversed only because of how the terminal displayed right-to-left text; the stored strings were correct, and this is now proven by code-point tests and an RTL rendering test. Regional alternatives (المبقاة, الاستهلاك, مرتجعات) are documented, not used.
6. No other account was changed, added or removed; no classification error was found in the structure, natures, contra treatment, returns/discounts, FX, inventory or retained-earnings accounts.

## H. Terminology reference (alternatives documented, not used)
| English | Arabic used | Documented alternative | Note |
|---|---|---|---|
| Retained earnings | الأرباح المحتجزة | الأرباح المبقاة | owner-approved |
| Accumulated depreciation (tangible) | مجمع الإهلاك | مجمع الاستهلاك | owner-approved |
| Accumulated amortization (intangible) | مجمع الإطفاء | مجمع الاستنفاد (natural resources, not used) | amortization = الإطفاء; depreciation = الإهلاك |
| Sales returns / Purchase returns | مردودات المبيعات / مردودات المشتريات | مرتجعات | owner-approved |
| Sales discounts allowed / Purchase discounts received | الخصم المسموح به / الخصم المكتسب | خصم المبيعات / خصم المشتريات (ambiguous direction) | |
| Cost of goods sold (account) / Cost of sales (group) | تكلفة البضاعة المباعة / تكلفة المبيعات | | |
| Accrued revenue / Deferred revenue | إيرادات مستحقة / إيرادات مؤجلة (مقبوضة مقدماً) | | earned-not-billed (asset) vs received-not-earned (liability) |
| Goods in transit / GRNI | بضاعة في الطريق / بضاعة مستلمة لم تُفوتر | بضاعة بالطريق | |
| Freight-in and import costs | مصاريف شحن المشتريات وتكاليف الاستيراد | | inward freight; delivery to customers is *النقل والتوصيل* |
| Foreign exchange gains / losses | أرباح / خسائر فروق أسعار الصرف | فروقات العملة | |
| Owners' current account | جاري الشركاء | جاري المالك (sole owner) | |

## I. Validation and tests (all executed)
* `ChartTemplateValidator` (pure, product code) and `ChartTemplateTests` (backend unit): structural rules on the generic template and the three business templates, the decisions above as assertions, Arabic stored as correct code points, ta-marbuta-never-starts-a-word check, tangible/intangible split, and that the proposals are not registered in `ChartTemplates.All`.
* `chartTemplatesRender.test.tsx` (frontend): every name of all three templates rendered through the existing Arabic RTL chart screen (document direction rtl, names present exactly as stored, no reversed words).

## J. Implementation (done)
Data in `ChartTemplates.All`; PostgreSQL tests (`ChartBusinessTemplateTests`, `ChartBusinessTemplateApiTests`: exact counts and content, parents, natures, statements, contra accounts, audit integrity, refusal on a non-empty chart, single and concurrent application, company isolation, HTTP picker data, permission); unit tests (`ChartTemplateTests`); the shared template picker tests and the RTL rendering test; CI expected counts updated. No migration was needed and nothing posts to inventory.
