import { ApiError } from "@hasm/api-client";
import type { Account, Branch, CostCenter, FiscalYear, GeneralLedgerResponse, ReconciliationResponse, ReportComparison, ReportDefinitionInfo, ReportFormat, ReportQuery, TrialBalanceResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { localName, OPEN_JOURNAL_KEY } from "../ledgerCommon";

/**
 * Ledger reports (Phase 3.2B S6, completed in 3.3 S2): the trial balance and the general ledger, computed by the server from POSTED entries only (reversals net the originals out; nothing is estimated or cached).
 * The period is a fiscal year, a fiscal period or a date range; the trial balance can be compared with the previous period or year (actual values from the same engine, nothing invented). Amounts are the base-currency
 * amounts stored on the posted lines, shown as exact decimal text. Exports (CSV, and XLSX/PDF when the server offers them) contain the COMPLETE authorized result with the same options as the screen and need the separate
 * export permission. Every figure and every authorization rule comes from the server; nothing here filters data.
 */

type Tab = "tb" | "gl";
type Mode = "custom" | "year" | "period";
const iso = (d: Date) => d.toISOString().slice(0, 10);

function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url; a.download = name;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

const TB_COLUMNS = ["code", "name", "opening", "debit", "credit", "closing"] as const;
const TB_CMP_COLUMNS = ["cmpClosing", "change"] as const;
const GL_COLUMNS = ["date", "number", "description", "currency", "debit", "credit", "rate", "baseDebit", "baseCredit", "running"] as const;
/** Screen column keys → the server's column keys (a screen column can map to several export columns). */
const SERVER_KEYS: Record<string, string[]> = { code: ["code"], name: ["nameAr", "nameEn"], opening: ["opening"], debit: ["debit"], credit: ["credit"], closing: ["closing"], cmpClosing: ["cmpClosing"], change: ["change"],
  date: ["date"], number: ["number"], description: ["description", "lineDescription"], currency: ["currency"], rate: ["rate"], baseDebit: ["baseDebit"], baseCredit: ["baseCredit"], running: ["runningBalance"] };

/** A translated message: field-level codes of a refused request first (for example an invalid sort), then the screen's own error texts. */
function reportError(t: (k: string) => string, e: unknown): string {
  if (e instanceof ApiError) {
    const code = e.details.fields?.[0]?.code;
    if (code) { const key = `rep.error.${code}`; if (t(key) !== key) return t(key); }
  }
  return errorText(t, e, "rep.error.");
}

export function LedgerReportsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [tab, setTab] = useState<Tab>("tb");
  const year = new Date().getFullYear();
  const [f, setF] = useState({
    mode: "custom" as Mode, from: `${year}-01-01`, to: iso(new Date()), fiscalYearId: "", fiscalPeriodId: "", comparison: "none" as ReportComparison, branchId: "", costCenterId: "", accountId: "",
    includeZero: false, excludeClosing: false, maxLevel: "", sort: "code", grouping: "none" as "none" | "month",
  });
  const [visible, setVisible] = useState<Record<string, boolean>>(() => Object.fromEntries([...TB_COLUMNS, ...TB_CMP_COLUMNS, ...GL_COLUMNS].map((k) => [k, true])));
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [branches, setBranches] = useState<Branch[]>([]);
  const [centers, setCenters] = useState<CostCenter[]>([]);
  const [years, setYears] = useState<FiscalYear[]>([]);
  const [defs, setDefs] = useState<ReportDefinitionInfo[]>([]);
  const [tb, setTb] = useState<TrialBalanceResponse | null>(null);
  const [gl, setGl] = useState<GeneralLedgerResponse | null>(null);
  const [rec, setRec] = useState<ReconciliationResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [ready, setReady] = useState(false);
  const canExport = auth.canAny("company.ledgerreports.export");
  const canOpenEntry = auth.canAny("company.journal.view");

  useEffect(() => {
    void (async () => {
      try { setAccounts((await auth.api.listAccounts()).accounts); } catch { setAccounts([]); }
      try { setBranches((await auth.api.listBranches()).branches); } catch { setBranches([]); }
      try { setYears((await auth.api.listFiscalYears()).years ?? []); } catch { setYears([]); }
      try { setDefs((await auth.api.getReportDefinitions()).reports ?? []); } catch { setDefs([]); }
      try { setCenters((await auth.api.listCostCenters()).costCenters); }
      catch {
        // A cost-center-scoped viewer cannot list the administration tree: offer the cost centers the server allows them instead.
        try { setCenters((await auth.api.listAllocatableCostCenters()).costCenters.map((c) => ({ ...c, isGroup: false, isActive: true, canReceiveAllocations: true, hasChildren: false, revision: 0, createdAtUtc: "", updatedAtUtc: "" }))); } catch { setCenters([]); }
      }
      setReady(true);
    })();
  }, [auth.api]);

  const def = defs.find((d) => d.id === (tab === "tb" ? "trial-balance" : "general-ledger"));
  const formats = (def?.formats ?? ["csv"]) as ReportFormat[];
  const selectedYear = years.find((y) => y.id === f.fiscalYearId);

  const query = (withPresentation: boolean): ReportQuery => {
    const base: ReportQuery = { branchId: f.branchId || undefined, costCenterId: f.costCenterId || undefined, excludeClosing: f.excludeClosing || undefined };
    if (f.mode === "period" && f.fiscalPeriodId) base.fiscalPeriodId = f.fiscalPeriodId;
    else if (f.mode === "year" && f.fiscalYearId) base.fiscalYearId = f.fiscalYearId;
    else { base.from = f.from; base.to = f.to; }
    if (!withPresentation) return base;
    if (tab === "tb") {
      if (f.comparison !== "none") base.comparison = f.comparison;
      if (f.maxLevel !== "") base.maxLevel = Number(f.maxLevel);
      if (f.sort !== "code") base.sort = f.sort;
    } else if (f.grouping !== "none") base.grouping = f.grouping;
    return base;
  };
  const exportColumns = (keys: readonly string[]) => keys.filter((k) => visible[k]).flatMap((k) => SERVER_KEYS[k] ?? []);

  const periodValid = f.mode === "custom" ? !!f.from && !!f.to && f.from <= f.to : f.mode === "year" ? !!f.fiscalYearId : !!f.fiscalPeriodId;
  const valid = periodValid && (tab === "tb" || !!f.accountId);

  const run = async (action: () => Promise<void>) => {
    setBusy(true); setError(null);
    try { await action(); } catch (err) { if (!auth.handleError(err)) setError(reportError(tRef.current, err)); } finally { setBusy(false); }
  };
  async function show(e?: FormEvent, offset = 0) {
    e?.preventDefault();
    if (!valid) return;
    await run(async () => {
      setRec(null);
      if (tab === "tb") setTb(await auth.api.getTrialBalance({ ...query(true), includeZero: f.includeZero }));
      else setGl(await auth.api.getGeneralLedger(f.accountId, query(true), offset));
    });
  }
  async function download(format: ReportFormat) {
    await run(async () => {
      const keys = tab === "tb" ? [...TB_COLUMNS, ...(f.comparison !== "none" ? TB_CMP_COLUMNS : [])] : GL_COLUMNS;
      const q = { ...query(true), columns: exportColumns(keys) };
      let blob: Blob;
      if (tab === "tb") blob = format === "csv" ? await auth.api.downloadTrialBalanceCsv({ ...q, includeZero: f.includeZero }) : await auth.api.exportTrialBalance({ ...q, includeZero: f.includeZero }, format);
      else blob = format === "csv" ? await auth.api.downloadGeneralLedgerCsv(f.accountId, q) : await auth.api.exportGeneralLedger(f.accountId, q, format);
      saveBlob(blob, `${tab === "tb" ? "trial-balance" : "general-ledger"}-${f.mode === "custom" ? `${f.from}-${f.to}` : f.mode}.${format}`);
    });
  }
  async function reconcile() {
    await run(async () => setRec(await auth.api.getReconciliation(query(false))));
  }
  const openEntry = (entryId: string) => { try { sessionStorage.setItem(OPEN_JOURNAL_KEY, entryId); } catch { /* the journal simply opens its list */ } };

  const cmpOn = tb?.rows.some((r) => r.comparative) ?? false;
  const tbCols = useMemo(() => [...TB_COLUMNS, ...(cmpOn ? TB_CMP_COLUMNS : [])].filter((k) => visible[k]), [cmpOn, visible]);
  const glCols = useMemo(() => GL_COLUMNS.filter((k) => visible[k]), [visible]);
  const chooser = (keys: readonly string[]) => (
    <fieldset className="column-chooser" data-testid="rep-columns">
      <legend>{t("rep.columns")}</legend>
      {keys.map((k) => <label key={k}><input type="checkbox" checked={visible[k]} onChange={(x) => setVisible({ ...visible, [k]: x.target.checked })} /> {t(`rep.col.${k}`)}</label>)}
    </fieldset>
  );

  if (!ready) return <Loading />;
  return (
    <>
      <PageHeader title={t("rep.title")} description={t("rep.subtitle")} />
      <div role="tablist" aria-label={t("rep.title")} className="tabs">
        <button type="button" role="tab" aria-selected={tab === "tb"} onClick={() => setTab("tb")}>{t("rep.trial_balance")}</button>
        <button type="button" role="tab" aria-selected={tab === "gl"} onClick={() => setTab("gl")}>{t("rep.general_ledger")}</button>
      </div>
      <Card>
        <form onSubmit={(e) => void show(e)} className="filters" data-testid="rep-form">
          <fieldset disabled={busy}>
            {tab === "gl" && (
              <Field id="rep-account" label={t("rep.account")} required>
                {(a) => <select {...a} value={f.accountId} onChange={(x) => setF({ ...f, accountId: x.target.value })}><option value="">{t("jrn.choose_account")}</option>{accounts.map((x) => <option key={x.id} value={x.id}>{x.code} — {localName(x, locale)}</option>)}</select>}
              </Field>
            )}
            {years.length > 0 && (
              <Field id="rep-mode" label={t("rep.period_mode")}>
                {(a) => <select {...a} value={f.mode} onChange={(x) => setF({ ...f, mode: x.target.value as Mode })}><option value="custom">{t("rep.period_custom")}</option><option value="year">{t("rep.period_year")}</option><option value="period">{t("rep.period_period")}</option></select>}
              </Field>
            )}
            {f.mode !== "custom" && (
              <Field id="rep-year" label={t("rep.fiscal_year")} required>
                {(a) => <select {...a} value={f.fiscalYearId} onChange={(x) => setF({ ...f, fiscalYearId: x.target.value, fiscalPeriodId: "" })}><option value="">{t("rep.choose")}</option>{years.map((y) => <option key={y.id} value={y.id}>{y.name} ({y.startDate} .. {y.endDate})</option>)}</select>}
              </Field>
            )}
            {f.mode === "period" && (
              <Field id="rep-period" label={t("rep.fiscal_period")} required>
                {(a) => <select {...a} value={f.fiscalPeriodId} onChange={(x) => setF({ ...f, fiscalPeriodId: x.target.value })}><option value="">{t("rep.choose")}</option>{(selectedYear?.periods ?? []).map((p) => <option key={p.id} value={p.id}>{p.number} ({p.startDate} .. {p.endDate})</option>)}</select>}
              </Field>
            )}
            {f.mode === "custom" && (
              <>
                <Field id="rep-from" label={t("rep.from")} required>{(a) => <input {...a} type="date" value={f.from} onChange={(x) => setF({ ...f, from: x.target.value })} />}</Field>
                <Field id="rep-to" label={t("rep.to")} required error={f.from > f.to ? t("rep.range_invalid") : undefined}>{(a) => <input {...a} type="date" value={f.to} onChange={(x) => setF({ ...f, to: x.target.value })} />}</Field>
              </>
            )}
            {tab === "tb" && (
              <>
                <Field id="rep-comparison" label={t("rep.comparison")}>
                  {(a) => <select {...a} value={f.comparison} onChange={(x) => setF({ ...f, comparison: x.target.value as ReportComparison })}><option value="none">{t("rep.cmp_none")}</option><option value="previous_period">{t("rep.cmp_previous_period")}</option><option value="previous_year">{t("rep.cmp_previous_year")}</option></select>}
                </Field>
                <Field id="rep-level" label={t("rep.max_level")}>
                  {(a) => <select {...a} value={f.maxLevel} onChange={(x) => setF({ ...f, maxLevel: x.target.value })}><option value="">{t("rep.level_all")}</option>{[0, 1, 2, 3].map((n) => <option key={n} value={n}>{t("rep.level_n", { n: String(n + 1) })}</option>)}</select>}
                </Field>
                <Field id="rep-sort" label={t("rep.sort")}>
                  {(a) => <select {...a} value={f.sort} onChange={(x) => setF({ ...f, sort: x.target.value })}>{["code", "name", "closing_desc", "closing_asc"].map((s) => <option key={s} value={s}>{t(`rep.sort_${s}`)}</option>)}</select>}
                </Field>
              </>
            )}
            {tab === "gl" && (
              <Field id="rep-grouping" label={t("rep.grouping")}>
                {(a) => <select {...a} value={f.grouping} onChange={(x) => setF({ ...f, grouping: x.target.value as "none" | "month" })}><option value="none">{t("rep.group_none")}</option><option value="month">{t("rep.group_month")}</option></select>}
              </Field>
            )}
            <Field id="rep-branch" label={t("rep.branch")}>{(a) => <select {...a} value={f.branchId} onChange={(x) => setF({ ...f, branchId: x.target.value })}><option value="">{t("common.all")}</option>{branches.map((b) => <option key={b.id} value={b.id}>{b.code} — {localName(b, locale)}</option>)}</select>}</Field>
            {centers.length > 0 && <Field id="rep-cc" label={t("rep.cost_center")}>{(a) => <select {...a} value={f.costCenterId} onChange={(x) => setF({ ...f, costCenterId: x.target.value })}><option value="">{t("common.all")}</option>{centers.map((c) => <option key={c.id} value={c.id}>{c.code} — {localName(c, locale)}</option>)}</select>}</Field>}
            <div className="field"><label><input type="checkbox" checked={f.excludeClosing} onChange={(x) => setF({ ...f, excludeClosing: x.target.checked })} /> {t("rep.exclude_closing")}</label></div>
            {tab === "tb" && <div className="field"><label><input type="checkbox" checked={f.includeZero} onChange={(x) => setF({ ...f, includeZero: x.target.checked })} /> {t("rep.include_zero")}</label></div>}
            {tab === "tb" ? chooser([...TB_COLUMNS, ...(f.comparison !== "none" ? TB_CMP_COLUMNS : [])]) : chooser(GL_COLUMNS)}
            <button type="submit" className="primary" disabled={!valid}>{t("rep.show")}</button>
            {canExport && formats.map((fmt) => <button key={fmt} type="button" disabled={!valid} data-testid={`rep-download-${fmt}`} onClick={() => void download(fmt)}>{t(`rep.download_${fmt}`)}</button>)}
            <button type="button" disabled={!periodValid} data-testid="rep-reconcile" onClick={() => void reconcile()}>{t("rep.reconcile")}</button>
          </fieldset>
        </form>
      </Card>
      {error && <Banner kind="error" testId="rep-error">{error}</Banner>}
      {rec && (
        <Card title={t("rep.reconcile_title")}>
          <Banner kind={rec.allPassed ? "success" : "error"} testId="rep-rec-summary">{rec.allPassed ? t("rep.reconcile_ok") : t("rep.reconcile_failed")}</Banner>
          <ul data-testid="rep-rec-checks">
            {rec.checks.map((c) => (
              <li key={c.code} data-testid={`rep-rec-${c.code}`}>
                <strong>{t(`rep.check.${c.code}`)}</strong>: {t(`rep.status.${c.status}`)}{c.differences > 0 ? ` — ${t("rep.differences", { n: String(c.differences) })}` : ""}
                {c.samples.length > 0 && <ul>{c.samples.map((s, i) => <li key={i}><bdi dir="ltr">{s.code}</bdi> {s.item}: <bdi dir="ltr">{s.expected}</bdi> → <bdi dir="ltr">{s.actual}</bdi></li>)}</ul>}
              </li>
            ))}
          </ul>
        </Card>
      )}
      {tab === "tb" && tb && (
        <Card title={t("rep.trial_balance")}>
          {!tb.balanced && <Banner kind="warning" testId="rep-unbalanced">{t("rep.unbalanced_note")}</Banner>}
          <p className="hint">{t("rep.base_currency")}: <bdi dir="ltr">{tb.baseCurrency}</bdi> — {t("rep.posted_only")}</p>
          {tb.period?.comparative && <p className="hint" data-testid="rep-comparative-period">{t("rep.comparative_period")}: <bdi dir="ltr">{tb.period.comparative.from} .. {tb.period.comparative.to}</bdi> ({tb.period.comparative.label})</p>}
          {tb.period?.comparativeUnavailable && <Banner kind="info" testId="rep-comparative-unavailable">{t(`rep.unavailable.${tb.period.comparativeUnavailable}`)}</Banner>}
          <div className="table-scroll">
            <table className="table" data-testid="rep-tb-table">
              <thead><tr>{tbCols.map((k) => <th key={k}>{t(`rep.col.${k}`)}</th>)}</tr></thead>
              <tbody>
                {tb.rows.map((r) => (
                  <tr key={r.accountId} data-testid={`rep-tb-${r.code}`} className={r.isGroup ? "row-group" : undefined}>
                    {tbCols.map((k) => k === "code" ? <td key={k}><bdi dir="ltr" style={{ paddingInlineStart: `${r.level * 1.25}rem` }}>{r.code}</bdi></td>
                      : k === "name" ? <td key={k}>{localName(r, locale)}</td>
                      : k === "cmpClosing" ? <td key={k}><bdi dir="ltr">{r.comparative?.closing ?? ""}</bdi></td>
                      : k === "change" ? <td key={k}><bdi dir="ltr">{r.change ?? ""}</bdi></td>
                      : <td key={k}><bdi dir="ltr">{r[k]}</bdi></td>)}
                  </tr>
                ))}
              </tbody>
              <tfoot><tr data-testid="rep-tb-total">{tbCols.map((k) => k === "code" ? <th key={k} colSpan={tbCols.includes("name") ? 2 : 1}>{t("jrn.total")}</th>
                : k === "name" ? null
                : k === "cmpClosing" ? <td key={k}><bdi dir="ltr">{tb.comparativeTotals?.closing ?? ""}</bdi></td>
                : k === "change" ? <td key={k} />
                : <td key={k}><bdi dir="ltr">{tb.totals[k]}</bdi></td>)}</tr></tfoot>
            </table>
          </div>
          {tb.rows.length === 0 && <p data-testid="rep-empty">{t("rep.empty")}</p>}
        </Card>
      )}
      {tab === "gl" && gl && (
        <Card title={t("rep.general_ledger")}>
          <p className="hint">{t("rep.opening")}: <bdi dir="ltr">{gl.opening}</bdi> — {t("rep.closing")}: <bdi dir="ltr">{gl.closing} {gl.baseCurrency}</bdi>{gl.includesDescendants ? ` — ${t("rep.includes_children")}` : ""} — {t("rep.total_lines", { n: String(gl.totalLines) })}</p>
          <div className="table-scroll">
            <table className="table" data-testid="rep-gl-table">
              <thead><tr>{glCols.map((k) => <th key={k}>{t(`rep.col.${k}`)}</th>)}</tr></thead>
              <tbody>
                {gl.lines.map((l, i) => (
                  <tr key={`${l.entryId}-${i}`} data-testid={`rep-gl-row-${i}`}>
                    {glCols.map((k) => k === "date" ? <td key={k}>{l.entryDate}</td>
                      : k === "number" ? <td key={k}>{canOpenEntry ? <a href="#/accounting/journal" data-testid={`rep-gl-open-${i}`} onClick={() => openEntry(l.entryId)}><bdi dir="ltr">{l.number}</bdi></a> : <bdi dir="ltr">{l.number}</bdi>}</td>
                      : k === "description" ? <td key={k}>{l.lineDescription ?? l.entryDescription ?? ""}</td>
                      : k === "currency" ? <td key={k}><bdi dir="ltr">{l.currency}</bdi></td>
                      : k === "running" ? <td key={k}><bdi dir="ltr">{l.runningBalance}</bdi></td>
                      : <td key={k}><bdi dir="ltr">{l[k]}</bdi></td>)}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {gl.lines.length === 0 && <p data-testid="rep-empty">{t("rep.empty")}</p>}
          {gl.totalLines > gl.offset + gl.lines.length && <button type="button" onClick={() => void show(undefined, gl.offset + gl.lines.length)}>{t("rep.next_page")}</button>}
          {gl.offset > 0 && <button type="button" onClick={() => void show(undefined, Math.max(0, gl.offset - 200))}>{t("rep.prev_page")}</button>}
        </Card>
      )}
    </>
  );
}
