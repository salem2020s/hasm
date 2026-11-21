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
| D-058 | Phase 4.3 orchestration: entry points and registry binding | Lead, autonomous under D-048, 2026-11-21 (planned; implementation after the plan review): three `Hasm.Admin` commands (`backup-company`, `trial-restore-company`, `drop-trial-restore`) over one infrastructure service; the company and its database come only from the company registry and only in state Ready (no caller-supplied database name); attachment storage must be configured (fail closed, no override); PostgreSQL client tools from `PATH`; a physical database name is never printed. Alternatives rejected: an API endpoint now (RBAC, licensing and UI belong to the monitoring slice), accepting a database name, treating missing storage configuration as no attachments, printing the kept trial database name. Details: `docs/PHASE-4.3.md` slice 3. D-054..D-057 are reserved by PR #87. |
| D-059 | Phase 4.3: restorability at backup time and trust of the manifest locale | Lead, autonomous under D-048, 2026-11-21 (planned): a backup set is written only if the captured locale passes the restore rules (ICU/builtin refused up front, `locale_unsupported`); the unsigned manifest locale is always validated, cross-checked against the live company database when it exists (provider null and `c` are the same libc provider; otherwise `locale_mismatch`) and trusted alone only when that database is gone, until set signing exists. Alternatives rejected: checking only at restore time, ignoring the live database, refusing whenever the database is gone (defeats D-053). |
| D-060 | Phase 4.3: trial restore and its pre-cutover checks | Lead, autonomous under D-048, 2026-11-21 (planned): a trial restore goes into a NEW generated, unregistered, token-tagged database and an attachment folder outside live storage and runs the read-only checks of SRS 16.2 / Scenario 21.8: shared party links resolve in `Hasm_System`, attachment rows match their files, hashes and keys, migration compatibility (D-062); the database is dropped afterwards by default; `--keep` writes an owner-only file next to the set and `drop-trial-restore` drops only a database whose comment carries that file's token. No registry, grant or checkpoint change; the cutover and the restore acknowledgement stay BLOCKED, so this slice does not fully meet 21.8. Alternatives rejected: deferring the 21.8 checks to the cutover, restoring under the registered name or into live storage (a cutover), keeping every trial database, a manual DBA drop. |
| D-061 | Phase 4.3: key coverage reported, not refused | Lead, autonomous under D-048, 2026-11-21 (planned): missing master key ids for attachment envelopes are reported at backup and trial restore (ids and exit code 8), never a reason to refuse the backup; a row key id that differs from its envelope is a warning (decryption uses the envelope). Alternative rejected: refusing (the gap already exists in the live system and the company would be left without any backup). |
| D-062 | Phase 4.3: migration compatibility of a backup set | Lead, autonomous under D-048, 2026-11-21 (planned): the company migration level is captured at backup time in the manifest (optional; older sets are checked after the restore); a level newer than this installation is refused before anything is created, an older level is a warning; the restored journal must equal the manifest level and match the embedded script checksums. Alternatives rejected: checking only after a restore, migrating the trial copy automatically, ignoring checksums. |
| D-063 | Isolated measurement tests (unit suite flake) | Lead, autonomous under D-048, 2026-11-21: under heavy machine load the unit suite failed once in ~20 runs in `XlsxReportRendererTests.A_very_large_report_streams_row_by_row_with_bounded_memory` (`peak managed memory above the baseline was 149 MB`, limit 120 MB): the test reads the WHOLE process heap while xUnit runs other test classes in parallel, so other tests' allocations were counted; a second wall-clock test, `TaxEngineTests.Per_document_mode_scales_to_the_maximum_document_size`, reached 21 s of its 30 s budget under the same load. Decision: both classes join one serial xUnit collection (`DisableParallelization`, so nothing runs beside them), and the tax scale test measures the process CPU time (`TotalProcessorTime` delta) instead of wall-clock time. NO threshold changes (120 MB and 30 s stay), nothing is skipped or removed, every assertion is kept; `PgBackupToolTests` timeout test (1.2-3.1 s against 30 s) is left alone. The absolute wall-clock performance budget belongs to the Phase 5.1 performance suite. Alternatives rejected for now: a `WeakReference` streaming proof (deterministic, immune to other tests; recorded as later hardening of the memory test), running the measured export in a child process (heavier), asserting relative scaling (still timing), and raising the thresholds (weakens the checks). |

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
