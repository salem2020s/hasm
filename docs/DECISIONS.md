# HASM — سجل القرارات المعتمدة (Decision Register)

> **الحالة:** Approved Baseline v1.0 — 2026-11-08. اعتماد صاحب المشروع للنسخة المنقحة من قرارات HASM v1.0.
> **ملاحظة:** أي تعديل جوهري بعد الاعتماد يتطلب طلب تغيير موثقاً، وتحديث الوثائق المتأثرة عبر Pull Request.

| ID | الموضوع | القرار المعتمد |
|---|---|---|
| D-001 | النطاق | HASM Local محاسبة ومخزون ومبيعات ومشتريات؛ SaaS والرواتب والتصنيع خارج v1 |
| D-002 | Desktop/Web | Windows عبر Tauri 2؛ واجهة React + TypeScript مشتركة مع Web |
| D-003 | Backend | ASP.NET Core / C#؛ العملاء عبر HTTPS API فقط |
| D-004 | البيانات | PostgreSQL، قاعدة نظام منطقية واحدة، **قاعدة مستقلة لكل شركة** |
| D-005 | التثبيت | جهاز واحد أو سيرفر مركزي أو تطبيق/DB منفصلان؛ Windows/Linux للسيرفر حسب الحاجة |
| D-006 | العزل | فصل مالية الشركات، RBAC شركة/فرع/مستودع، بيانات Parties المشتركة بموافقة مركزية |
| D-007 | فروع | تحويلات وتسويات داخل الشركة، مخازن وصناديق وتكاليف فروع ومراكز تكلفة |
| D-008 | هوية وأمان | مستخدمون محليون وتكامل AD اختياري، TOTP، RBAC وSoD وموافقات مرنة |
| D-009 | قيود | ترحيل ذري ومتوازن، القيد المرحّل لا يعدل/يحذف، تصحيح بعكس/تسوية |
| D-010 | الفترات | سنوات مالية متعددة، Open/Soft Closed/Final Closed، إقفال محكوم وموافقات |
| D-011 | العملات | عملات متعددة وأسعار صرف مؤرخة، استثناء مخول وتوثيق الفروق |
| D-012 | الضرائب | محرك ضرائب مرن بمعدلات مؤرخة وتعدد/إعفاء وشامل/غير شامل ولقطة تاريخية |
| D-013 | دورة مستندات | بيع/شراء/عروض/أوامر/مرتجعات/سندات؛ نمطا الفاتورة المباشرة والتسليم المنفصل |
| D-014 | حركة المخزون | في موافقة الاستلام/التسليم الفعلي؛ حالات قيد محاسبي معلقة صريحة إن لزم؛ منع التكرار |
| D-015 | ترقيم المستندات | مرن حسب شركة/فرع/نوع/سنة، بادئات وتسلسل محمي وعدم إعادة رقم ملغى تلقائياً |
| D-016 | التسعير | أسعار وخصومات بأولوية حسب العميل/مجموعة/كمية/وحدة/عملة/تاريخ ومراقبة هامش |
| D-017 | طرق التكلفة | FIFO والمتوسط المرجح المتحرك؛ سياسة وفق شركة/مجموعة مواد |
| D-018 | نطاق التكلفة | **مادة × مستودع** في FIFO والمتوسط؛ تكلفتها الفعلية تنتقل مع التحويل |
| D-019 | مخزون سالب | ممنوع افتراضياً؛ استثناء محدود للمتوسط وبموافقة وتكلفة مؤقتة؛ ممنوع FIFO/Serial/Lot |
| D-020 | بتاريخ سابق | إعادة حساب أثر التكلفة ومعاينة وتسويات مستقلة مع مراعاة الإقفال والتدقيق |
| D-021 | تسوية الذمم | تخصيص قبض/صرف يدوياً أو اقتراح تلقائي، جزئي/مقدم/زائد، فروقات عملة، لا قيود مكررة |
| D-022 | الشيكات | دورة حياة وارد/صادر/مؤجل/حافظة/إيداع/تحصيل/مرتجع/تظهير مع قيود وتقارير |
| D-023 | المطابقة البنكية | يدوي وملفات Excel/CSV، اقتراح ذكي، متعدد/جزئي، مراجعة واعتماد |
| D-024 | المستندات المتكررة | مسودة افتراضياً، ترحيل آلي مشروط بموافقة وصلاحية وفترة، منع تكرار |
| D-025 | التقارير | تقارير جاهزة ومصمم تقارير مرئي متقدم وتصدير PDF/XLSX |
| D-026 | الطباعة | مصمم قوالب مستندات مستقل A4/A5/حراري ولغات وباركود ونسخ تاريخية |
| D-027 | UX | عربي RTL/إنجليزي LTR، قوائم Hover متعددة المستويات، التنفيذ فقط بالنقر/الكيبورد |
| D-028 | الهوية البصرية | استخدام شعار HASM الرسمي المعتمد دون اختراع بديل |
| D-029 | ترميز DB | أسماء فيزيائية غير دلالية لقاعدة النظام/الشركات والجداول والحقول التجارية وأسماء القيود الكاشفة؛ ربط منطقي خاص وثابت عبر Migrations، مع الإقرار بأن DBA قد يحلل المخطط |
| D-030 | الاستيراد | CSV/Excel، Staging وDry Run، دعم تاريخ كامل أو افتتاحيات، موافقة قبل التنفيذ |
| D-031 | المرفقات | ملفات على السيرفر محلي/SMB/NFS مع Metadata في قاعدة الشركة وعزل وصلاحيات |
| D-032 | النسخ | تشفير وجداول مرنة واحتفاظ ووجهات متعددة واختبار استعادة، استعادة شركة مستقلة |
| D-033 | التحديثات | يدوية فقط، متصلة أو Offline عبر حزم موقعة مع فحوص توافق واستعادة |
| D-034 | التراخيص | Named/Concurrent؛ اشتراك أو دائم؛ كل جلسة نشطة مقعد مستقل |
| D-035 | نقل الترخيص | حصراً عن طريق فريق HASM الفني، لا نقل ذاتي عند عطل/استبدال السيرفر |
| D-036 | Trial | بلا مفتاح أو مدة، 1 شركة/2 مستخدم/1 فرع/1 مستودع/100 معاملات أعمال مرحّلة، وحدات أساسية |
| D-037 | دعم الإصدارات | **36 شهراً لكل إصدار رئيسي**؛ إصلاحات أمنية/عيوب مجانية حتى لو انتهت صيانة الترخيص الدائم |
| D-038 | الأداء | 50 جلسة على 8 vCPU/32 GB/500 GB SSD، 5 شركات ومقاييس P95 وحمل 60 دقيقة |
| D-039 | الاختبارات | Unit/Integration/E2E وأمن/أداء/ترقية/استعادة إلزامية؛ لا إصدار بأخطاء Critical/High |
| D-040 | Offline client | مسودات محلية آمنة، لا ترحيل محاسبي أو مخزني Offline |
| D-041 | التشغيل والمراقبة | Logs وتشخيص وتنبيهات محلية/SMTP وحزمة دعم مع حماية الأسرار |
| D-042 | العمل الجماعي | GitHub Private، `main` مستقر، فروع منفصلة، Pull Request قبل الدمج |
| D-043 | بدء البرمجة | **استوفي شرط الاعتماد بتاريخ 2026-11-08**؛ يبدأ التطوير بمهام صغيرة، واختبارات، وفروع Git منفصلة، وفق SRS v1.0 |
| D-046 | Temporary development dependency security policy | Owner conditional approval, 2026-11-18: only GHSA-fc5q-39xc-vjpf and the six exact reviewed development-only records; no activation before actual Node24/Linux CI and production Web/Tauri artifact inspection, independent review and exact-head checks pass. Production audit and every other High/Critical stay blocking; frozen inputs and fail-closed policy; automatic expiry 2026-11-24T00:00:00Z, no renewal. Implementation/review: `review-records/PR-50-conditional-activation.md`. D-044/D-045 remain reserved for the separately approved accounting decisions in open PR48. |
| D-047 | Retirement of the picomatch exception (supersedes D-046) | Owner approval A, 2026-11-20: GitHub withdrew GHSA-fc5q-39xc-vjpf on 2026-11-18 and the full frontend audit reports zero findings. Applied exactly three reviewed patches (PICOMATCH-RETIREMENT, ARTIFACT-PIN-RETIREMENT, RETIREMENT-HARDENING-ADDENDUM): strict full audit, ZERO exceptions, every High/Critical blocks; the exception-era frozen source-tree pins are lifted only for the hash-bound retirement record; every mandatory job, the same-commit manifest, the production module blacklist, the asset inventory and the installer payload comparison remain. No exception, expiry renewal or optional job is authorized. Open, not part of this decision: mandatory Rust dependency audit, malware scanning and attachment quotas (production-readiness work, not claimed to exist). Review: `docs/security/COMBINED-REVIEW.md` and the review record of the applying PR. |
| D-048 | Standing autonomous decision-making authorization | Owner, 2026-11-21: agents decide routine technical, architectural, implementation and workflow questions autonomously (evaluating the approved SRS and register, accounting correctness and data integrity, security, maintainability, industry practice, UX), prefer the recommended option when consistent with approved requirements but do not accept it blindly, make reasonable configurable assumptions where the SRS is silent, and record significant autonomous decisions with rationale and alternatives here. Owner escalation only for: conflict with an approved requirement; fundamental accounting, valuation or reporting policy; weakening database integrity, security controls or protected CI/release gates; irreversible data change or significant legal/compliance implication; licensing, commercial scope or an explicitly owner-reserved policy. One consolidated recommendation package per escalation; only the affected phase is blocked. Its text was submitted to CLAUDE.md as PR #82 (protected file: merged only on the owner's explicit instruction; status in docs/INTEGRATION-STATUS.md). Autonomous decisions are recorded here; escalations and the owner's answers stay in the INTEGRATION-STATUS owner-decision register. |
| D-049 | Phase 3.13 Q1 | Owner, 2026-11-21: a generic commercial document engine with ONE internal test-only document type registered only in the test composition (no production switch); real document types arrive in their approved phases (3.15, 3.16). |
| D-050 | Phase 3.13 Q7 | Owner, 2026-11-21: the official document number is allocated at first approval, preserved through re-approval, and a voided number is never reused. |
| D-051 | Phase 3.13 Q8 | Owner, 2026-11-21: an approved document may be cancelled only before any stock or ledger effect, with an explicit cancel permission, a reason and an audit record. |
| D-052 | Phase 3.13 Q15 | Owner, 2026-11-21: the direct or separate stock-effect mode is chosen per document at draft creation and frozen at approval. |
| D-053 | Phase 4.3 restore: source database locale captured at dump time | Lead, autonomous under D-048, 2026-11-21: the encoding, LC_COLLATE, LC_CTYPE and locale provider of the source company database are captured when it is dumped and carried in the backup set manifest (optional `databaseLocale` record; older manifests stay readable). A restore uses the captured values; without them it reads the live source; with neither it is refused (`source_locale_unknown`), never created with template0 defaults. On PostgreSQL 15+ the restored database pins `LOCALE_PROVIDER libc`. Rationale: a disaster-recovery restore must not depend on the source database still existing, and the collation must never change silently. Alternatives rejected: parse the dump's TOC (tool-output parsing), refuse when the source is gone (defeats disaster recovery), silent template0 defaults. Details: `docs/PHASE-4.3.md` (round 4 of PR #78). |
| D-054 | Phase 3.13 S1a snapshot columns | Agent decision under the standing authorization, 2026-11-21: the price/tax snapshot columns of a commercial document line are NOT created in company migration 0028 (S1a lines carry no price, tax or cost); S1b adds them with an additive `ALTER TABLE ADD COLUMN` migration. Rationale: the migration loader forbids `DROP`, so a NULL-only CHECK created now could never be relaxed, and columns that exist but must stay NULL would be unreachable state. Alternatives: (a) create the columns now with a NULL-only CHECK (rejected: needs DROP later); (b) create them now without a CHECK (rejected: a direct statement could fill them before the server-side sealing exists); (c) additive columns in S1b (chosen). Deviation from the plan sentence "columns exist but stay NULL" recorded here. |
| D-055 | Phase 3.13 S1a guard and line deletion | Agent decision, 2026-11-21: the 0028 status guard allows only draft->submitted, submitted->draft, draft/submitted->cancelled; approval, rejection and cancel-of-approved arrive with S1b through a MANDATORY new migration that replaces the guard (0028 is never edited after merge). A draft's lines are replaced by DELETE + INSERT while the document is a draft (a PostgreSQL trigger refuses any line change otherwise), so the line table is added to the exact DELETE-capable allow-lists (as the stock-document and tax tables were); every replacement is audited. Alternatives: soft-void flags on lines (rejected: more state, no integrity gain). |
| D-056 | Phase 3.13 S1a permissions | Agent decision, 2026-11-21: group of branch-scoped permissions `company.commercialdocs.view|create|update|submit|cancel` (system migration 0028, Company Administrator preset), branch-scoped like the journal (SRS 3.5, 4.2); withdrawal needs the submit permission; a caller with no grant never makes the company database answer and cannot learn whether a document exists. Approve/reject permissions arrive with S1b and will stay outside the Company Administrator preset. |
| D-057 | Phase 3.13 S1b defaults (recorded now, implemented in S1b/S3) | Agent decision, 2026-11-21, using the plan's recommended defaults: Q19 the price/tax snapshot is re-quoted at submit and at approval, a difference is refused with `pricing_changed`, and the snapshot is sealed at approval (SRS 8.4); Q9 a margin-flagged line needs an explicit approval record and cost/margin evaluation belong to S3 (Q18: `cost_unavailable` under Block/RequireApproval, not computed under Warn); the proposed MATERIAL fields that send an approved document back to approval: party, branch, currency, document date, mode, and every line's item, unit, quantity, warehouse, price, discount and tax (non-material: note and reference text). |

## توضيحات اتساق مستمدة من القرارات المعتمدة (ليست قرارات جديدة)

- **D-004 وD-029:** `Hasm_System` تسمية منطقية؛ اسم قاعدة PostgreSQL الفعلي مُرمّز وغير وصفي.
- **D-008 وD-009 وD-014:** المستند المعتمد غير المرحّل قد يُعاد إلى الموافقة عند تعديل مؤثر؛ أما القيد المرحّل فلا يُعدّل ولا يُحذف. حدث التسليم المادي وحالة القيد المالي قد يقعان في مرحلتين مختلفتين، وتُمنع ازدواجية أي منهما.
- **D-004 وD-006 وD-009:** الذرية داخل قاعدة شركة واحدة؛ لا يُفترض Commit موزع بينها وبين قاعدة النظام أو شركة أخرى، وتُستخدم سجلات موثوقة وإعادة محاولة/تسوية للأحداث العابرة للقواعد.
- **D-031 وD-032:** المرفقات الحساسة على خادم HASM تُشفّر على مستوى التطبيق، وتستعاد باتساق مع قاعدة شركتها ومعلوماتها الوصفية.
- **D-043:** وافق صاحب المشروع على الـ SRS النهائي كـ Baseline v1.0 بتاريخ 2026-11-08؛ لا يجيز الاعتماد تجاوز المواصفات أو إجراء تغييرات مالية/معمارية غير موثقة.

## تفاصيل قابلة للإعداد، لا تستدعي دورة أسئلة جديدة

| العنصر | الوضع |
|---|---|
| حد مرفقات Trial بالميغابايت | غير معتمد رقمياً؛ يدخل كإعداد ترخيص مع قيمة افتراضية يحددها التنفيذ ويعلنها |
| RPO/RTO | يحددان حسب بيئة العميل وخطة النسخ والاختبارات، ولا يُعد بوقت تعافٍ غير مقاس |
| مهلة Heartbeat/Seat release | إعداد تشغيلي قابل للضبط والاختبار، لا رقم مُعتمد مسبقاً |
| تفصيل البنية الداخلية (ORM، أسماء المشاريع) | تصميم تقني لاحق شريطة احترام الحدود المعتمدة |
| مواصفات خوادم الإنتاج الدنيا | لا تُستنتج من عتاد الأداء المرجعي دون قياسات |
| برامج ضريبية حكومية وإصدار فواتير إلكترونية بلد محدد | متروكة لوحدة توطين عندما يحدد بلد وسند الامتثال |

## محطات إدارة النسخة

1. **Approved baseline (مكتمل):** دمج القرارات وتدقيق اتساق الوثائق واعتماد SRS v1.0 من صاحب المشروع بتاريخ 2026-11-08.
2. **Repository sign-off:** حفظ الاعتماد في `docs/APPROVAL.md` ودمجه عبر PR في فرع `main`.
3. **Implementation planning:** تفكيك المواصفات إلى Milestones وIssues واختبارات قبول قبل إعطاء Claude مهام برمجية محددة.
4. **Release controls:** PR واختبارات آلية وفحص أمان وترحيل قاعدة بيانات وتجربة استعادة قبل إصدار تجاري.
