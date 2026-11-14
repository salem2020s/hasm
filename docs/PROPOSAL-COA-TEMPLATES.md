# PROPOSAL — business-type Chart of Accounts templates (3.2A-2)

**Status: PROPOSAL, not approved, not implemented.** Nothing here is production-ready accounting content. The owner (or the accounting reviewer they appoint) must review and approve every template before it is built into the product (roadmap item 3.2A-2). Account codes follow the configurable DEFAULT convention of 3.2A (root codes 1 to 5 by type, children appended); codes, names, nature and grouping are all open to change by the reviewer.

Conventions used below: **G** = group (parent) account, **P** = postable account. Types: A asset, L liability, E equity, R revenue, X expense. Nature is the normal balance (D debit, C credit); contra accounts are marked. Arabic names are proposals for review by a native accounting reviewer.

## Template 1 — Service business (consulting, agencies, clinics, workshops without trading stock)

| Code | Arabic | English | Type | Kind |
|---|---|---|---|---|
| 1 | الأصول | Assets | A | G |
| 11 | الأصول المتداولة | Current assets | A | G |
| 1101 | الصندوق | Cash on hand | A | P |
| 1102 | البنوك | Bank accounts | A | P |
| 1103 | العملاء | Accounts receivable | A | P |
| 1104 | سلف الموظفين | Employee advances | A | P |
| 1105 | مصاريف مدفوعة مقدماً | Prepaid expenses | A | P |
| 1106 | إيرادات مستحقة | Accrued revenue | A | P |
| 12 | الأصول الثابتة | Fixed assets | A | G |
| 1201 | المعدات والأثاث | Equipment and furniture | A | P |
| 1202 | الحاسبات والبرمجيات | Computers and software | A | P |
| 1203 | مجمع الاستهلاك (حساب مقابل) | Accumulated depreciation (contra) | A | P |
| 2 | الالتزامات | Liabilities | L | G |
| 21 | الالتزامات المتداولة | Current liabilities | L | G |
| 2101 | الموردون | Accounts payable | L | P |
| 2102 | مصاريف مستحقة | Accrued expenses | L | P |
| 2103 | رواتب مستحقة | Salaries payable | L | P |
| 2104 | إيرادات مقبوضة مقدماً | Deferred revenue | L | P |
| 2105 | ضرائب مستحقة | Taxes payable | L | P |
| 22 | الالتزامات طويلة الأجل | Long-term liabilities | L | G |
| 2201 | قروض طويلة الأجل | Long-term loans | L | P |
| 3 | حقوق الملكية | Equity | E | G |
| 3101 | رأس المال | Capital | E | P |
| 3102 | جاري الشركاء | Partners' current accounts | E | P |
| 3103 | الأرباح المرحّلة | Retained earnings | E | P |
| 4 | الإيرادات | Revenue | R | G |
| 4101 | إيرادات الخدمات | Service revenue | R | P |
| 4102 | إيرادات اشتراكات وعقود | Retainer and contract revenue | R | P |
| 4103 | إيرادات أخرى | Other income | R | P |
| 4104 | خصومات ممنوحة (حساب مقابل) | Discounts allowed (contra) | R | P |
| 5 | المصروفات | Expenses | X | G |
| 5101 | الرواتب والأجور | Salaries and wages | X | P |
| 5102 | الإيجارات | Rent | X | P |
| 5103 | الكهرباء والماء والاتصالات | Utilities and communications | X | P |
| 5104 | الدعاية والإعلان | Marketing and advertising | X | P |
| 5105 | أتعاب مهنية | Professional fees | X | P |
| 5106 | مصاريف سفر وتنقل | Travel and transport | X | P |
| 5107 | مصاريف مكتبية وقرطاسية | Office supplies | X | P |
| 5108 | الاستهلاك | Depreciation | X | P |
| 5109 | عمولات ومصاريف بنكية | Bank charges | X | P |
| 5110 | فروقات تقريب العملة | Currency rounding differences | X | P |

## Template 2 — Trading business (retail and wholesale)

| Code | Arabic | English | Type | Kind |
|---|---|---|---|---|
| 1 | الأصول | Assets | A | G |
| 11 | الأصول المتداولة | Current assets | A | G |
| 1101 | الصندوق | Cash on hand | A | P |
| 1102 | البنوك | Bank accounts | A | P |
| 1103 | العملاء | Accounts receivable | A | P |
| 1104 | شيكات برسم التحصيل | Cheques under collection | A | P |
| 1105 | المخزون | Inventory | A | P |
| 1106 | بضاعة بالطريق / مستلمة غير مفوترة | Goods in transit / received not invoiced | A | P |
| 1107 | سلف الموردين | Supplier advances | A | P |
| 1108 | مصاريف مدفوعة مقدماً | Prepaid expenses | A | P |
| 1109 | ضريبة المدخلات القابلة للاسترداد | Recoverable input tax | A | P |
| 12 | الأصول الثابتة | Fixed assets | A | G |
| 1201 | الأثاث والتجهيزات | Furniture and fixtures | A | P |
| 1202 | المركبات | Vehicles | A | P |
| 1203 | مجمع الاستهلاك (حساب مقابل) | Accumulated depreciation (contra) | A | P |
| 2 | الالتزامات | Liabilities | L | G |
| 21 | الالتزامات المتداولة | Current liabilities | L | G |
| 2101 | الموردون | Accounts payable | L | P |
| 2102 | شيكات تحت الدفع | Cheques payable | L | P |
| 2103 | بضاعة مستلمة غير مفوترة (GRNI) | Goods received not invoiced (GRNI) | L | P |
| 2104 | مصاريف مستحقة | Accrued expenses | L | P |
| 2105 | رواتب مستحقة | Salaries payable | L | P |
| 2106 | ضريبة المخرجات المستحقة | Output tax payable | L | P |
| 2107 | دفعات مقدمة من العملاء | Customer advances | L | P |
| 22 | الالتزامات طويلة الأجل | Long-term liabilities | L | G |
| 2201 | قروض طويلة الأجل | Long-term loans | L | P |
| 3 | حقوق الملكية | Equity | E | G |
| 3101 | رأس المال | Capital | E | P |
| 3102 | جاري الشركاء | Partners' current accounts | E | P |
| 3103 | الأرباح المرحّلة | Retained earnings | E | P |
| 4 | الإيرادات | Revenue | R | G |
| 4101 | مبيعات البضاعة | Merchandise sales | R | P |
| 4102 | مردودات المبيعات (حساب مقابل) | Sales returns (contra) | R | P |
| 4103 | خصومات المبيعات (حساب مقابل) | Sales discounts (contra) | R | P |
| 4104 | إيرادات أخرى | Other income | R | P |
| 5 | المصروفات | Expenses | X | G |
| 51 | تكلفة المبيعات | Cost of sales | X | G |
| 5101 | تكلفة البضاعة المباعة | Cost of goods sold | X | P |
| 5102 | فروقات الجرد والتسويات المخزنية | Inventory count and adjustment differences | X | P |
| 5103 | مصاريف شحن واستيراد | Freight and import costs | X | P |
| 5104 | مردودات المشتريات وخصوماتها (حساب مقابل) | Purchase returns and discounts (contra) | X | P |
| 52 | المصروفات التشغيلية | Operating expenses | X | G |
| 5201 | الرواتب والأجور | Salaries and wages | X | P |
| 5202 | الإيجارات | Rent | X | P |
| 5203 | الكهرباء والماء والاتصالات | Utilities and communications | X | P |
| 5204 | الدعاية والإعلان | Marketing and advertising | X | P |
| 5205 | مصاريف نقل وتوصيل | Delivery and transport | X | P |
| 5206 | الاستهلاك | Depreciation | X | P |
| 5207 | عمولات ومصاريف بنكية | Bank charges | X | P |
| 5208 | ديون معدومة | Bad debts | X | P |
| 5209 | فروقات تقريب العملة | Currency rounding differences | X | P |

## Open points for the reviewer (not decided here)
1. Whether Retained earnings (3103) is the default profit-and-loss account or a separate "current-year result" account is preferred (3.2C S3 lets every company choose; the template only proposes a postable equity account).
2. The tax accounts (2105 / 1109 / 2106) are placeholders: the tax engine (SRS §6) and the localization requirements are not defined yet and must drive the final structure.
3. Inventory, GRNI and cost-of-sales accounts anticipate the inventory and purchasing phases; their final names depend on the accounting-event design of those phases.
4. Currency policy per account (base / any / specific), cash-flow and statement classifications and whether branch-specific bank accounts are created automatically are not part of this proposal.
5. Arabic terminology (Levant vs Gulf vs Maghreb conventions) needs a native accounting review before approval.
