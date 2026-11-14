import { ApiError } from "@hasm/api-client";
import type { Branch, CostCenter, FiscalYear, ReportComparison, ReportDefinitionInfo, ReportFormat, StatementKind, StatementMappingVersionInfo, StatementQuery, StatementResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { localName } from "../ledgerCommon";
import { navigate } from "../router";

/**
 * Financial statements (Phase 3.3 S3): Profit and Loss, Balance Sheet and Trading Account / Cost of Sales, computed by the server from POSTED ledger data with the company's versioned classification. A statement whose
 * accounts are not all classified is shown as INCOMPLETE (unclassified accounts appear in their own block, never dropped), a statement that does not apply says why, and the reconciliation checks are shown. Exports
 * (CSV, XLSX, PDF as offered by the server) hold the complete authorized result and need the separate export permission. Nothing here computes or filters a figure.
 */

type Mode = "custom" | "year" | "period";
const KINDS: StatementKind[] = ["profit-and-loss", "balance-sheet", "trading-account"];
const iso = (d: Date) => d.toISOString().slice(0, 10);

function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url; a.download = name;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function statementError(t: (k: string) => string, e: unknown): string {
  if (e instanceof ApiError) {
    const code = e.details.fields?.[0]?.code;
    if (code) { const key = `rep.error.${code}`; if (t(key) !== key) return t(key); }
    const own = `st.error.${e.code}`;
    if (t(own) !== own) return t(own);
  }
  return errorText(t, e, "rep.error.");
}

export function FinancialStatementsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const year = new Date().getFullYear();
  const [kind, setKind] = useState<StatementKind>("profit-and-loss");
  const [f, setF] = useState({ mode: "custom" as Mode, from: `${year}-01-01`, to: iso(new Date()), fiscalYearId: "", fiscalPeriodId: "", comparison: "none" as ReportComparison, branchId: "", costCenterId: "", mappingVersion: "" });
  const [years, setYears] = useState<FiscalYear[]>([]);
  const [branches, setBranches] = useState<Branch[]>([]);
  const [centers, setCenters] = useState<CostCenter[]>([]);
  const [versions, setVersions] = useState<StatementMappingVersionInfo[]>([]);
  const [defs, setDefs] = useState<ReportDefinitionInfo[]>([]);
  const [data, setData] = useState<StatementResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [ready, setReady] = useState(false);
  const canExport = auth.canAny("company.financialstatements.export");
  const canMap = auth.can("company.statementmapping.view");

  useEffect(() => {
    void (async () => {
      try { setYears((await auth.api.listFiscalYears()).years ?? []); } catch { setYears([]); }
      try { setBranches((await auth.api.listBranches()).branches); } catch { setBranches([]); }
      try { setCenters((await auth.api.listCostCenters()).costCenters); } catch { setCenters([]); }
      try { setDefs((await auth.api.getReportDefinitions()).reports ?? []); } catch { setDefs([]); }
      if (canMap) { try { setVersions((await auth.api.listStatementMappingVersions()).versions ?? []); } catch { setVersions([]); } }
      setReady(true);
    })();
  }, [auth.api, canMap]);

  const formats = (defs.find((d) => d.id === kind)?.formats ?? ["csv"]) as ReportFormat[];
  const selectedYear = years.find((y) => y.id === f.fiscalYearId);
  const periodValid = f.mode === "custom" ? !!f.from && !!f.to && f.from <= f.to : f.mode === "year" ? !!f.fiscalYearId : !!f.fiscalPeriodId;
  const asOf = kind === "balance-sheet";

  const query = (): StatementQuery => {
    const q: StatementQuery = { branchId: f.branchId || undefined, costCenterId: f.costCenterId || undefined };
    if (f.mode === "period" && f.fiscalPeriodId) q.fiscalPeriodId = f.fiscalPeriodId;
    else if (f.mode === "year" && f.fiscalYearId) q.fiscalYearId = f.fiscalYearId;
    else { q.from = f.from; q.to = f.to; }
    if (f.comparison !== "none") q.comparison = f.comparison;
    if (f.mappingVersion) q.mappingVersion = Number(f.mappingVersion);
    return q;
  };
  const run = async (action: () => Promise<void>) => {
    setBusy(true); setError(null);
    try { await action(); } catch (err) { if (!auth.handleError(err)) setError(statementError(tRef.current, err)); } finally { setBusy(false); }
  };
  const show = async (e?: FormEvent) => { e?.preventDefault(); if (periodValid) await run(async () => setData(await auth.api.getStatement(kind, query()))); };
  const download = async (format: ReportFormat) => run(async () => saveBlob(await auth.api.exportStatement(kind, query(), format), `${kind}-${f.mode === "custom" ? `${f.from}-${f.to}` : f.mode}.${format}`));

  const lineTitle = (key: string) => {
    const k = `st.line.${key}`;
    if (t(k) !== k) return t(k);
    if (key.startsWith("total_")) { const base = `st.line.${key.slice(6)}`; return t(base) !== base ? `${t("st.total")} ${t(base)}` : key; }
    return key;
  };
  const statusText = (s: string) => t(`st.status.${s}`);
  const mappingLabel = data?.mapping ? t("st.mapping_version_n", { n: String(data.mapping.versionNo) }) : t("st.mapping_none");

  if (!ready) return <Loading />;
  return (
    <>
      <PageHeader title={t("st.title")} description={t("st.subtitle")} />
      <div role="tablist" aria-label={t("st.title")} className="tabs">
        {KINDS.map((k) => <button key={k} type="button" role="tab" aria-selected={kind === k} onClick={() => { setKind(k); setData(null); }}>{t(`st.tab.${k}`)}</button>)}
      </div>
      <Card>
        <form onSubmit={(e) => void show(e)} className="filters" data-testid="st-form">
          <fieldset disabled={busy}>
            {years.length > 0 && (
              <Field id="st-mode" label={t("rep.period_mode")}>
                {(a) => <select {...a} value={f.mode} onChange={(x) => setF({ ...f, mode: x.target.value as Mode })}><option value="custom">{t("rep.period_custom")}</option><option value="year">{t("rep.period_year")}</option><option value="period">{t("rep.period_period")}</option></select>}
              </Field>
            )}
            {f.mode !== "custom" && (
              <Field id="st-year" label={t("rep.fiscal_year")} required>
                {(a) => <select {...a} value={f.fiscalYearId} onChange={(x) => setF({ ...f, fiscalYearId: x.target.value, fiscalPeriodId: "" })}><option value="">{t("rep.choose")}</option>{years.map((y) => <option key={y.id} value={y.id}>{y.name} ({y.startDate} .. {y.endDate})</option>)}</select>}
              </Field>
            )}
            {f.mode === "period" && (
              <Field id="st-period" label={t("rep.fiscal_period")} required>
                {(a) => <select {...a} value={f.fiscalPeriodId} onChange={(x) => setF({ ...f, fiscalPeriodId: x.target.value })}><option value="">{t("rep.choose")}</option>{(selectedYear?.periods ?? []).map((p) => <option key={p.id} value={p.id}>{p.number} ({p.startDate} .. {p.endDate})</option>)}</select>}
              </Field>
            )}
            {f.mode === "custom" && (
              <>
                {!asOf && <Field id="st-from" label={t("rep.from")} required>{(a) => <input {...a} type="date" value={f.from} onChange={(x) => setF({ ...f, from: x.target.value })} />}</Field>}
                <Field id="st-to" label={asOf ? t("st.as_of") : t("rep.to")} required error={f.from > f.to ? t("rep.range_invalid") : undefined}>{(a) => <input {...a} type="date" value={f.to} onChange={(x) => setF({ ...f, to: x.target.value })} />}</Field>
              </>
            )}
            <Field id="st-comparison" label={t("rep.comparison")}>
              {(a) => <select {...a} value={f.comparison} onChange={(x) => setF({ ...f, comparison: x.target.value as ReportComparison })}><option value="none">{t("rep.cmp_none")}</option><option value="previous_period">{t("rep.cmp_previous_period")}</option><option value="previous_year">{t("rep.cmp_previous_year")}</option></select>}
            </Field>
            <Field id="st-branch" label={t("rep.branch")}>{(a) => <select {...a} value={f.branchId} onChange={(x) => setF({ ...f, branchId: x.target.value })}><option value="">{t("common.all")}</option>{branches.map((b) => <option key={b.id} value={b.id}>{b.code} — {localName(b, locale)}</option>)}</select>}</Field>
            {centers.length > 0 && <Field id="st-cc" label={t("rep.cost_center")}>{(a) => <select {...a} value={f.costCenterId} onChange={(x) => setF({ ...f, costCenterId: x.target.value })}><option value="">{t("common.all")}</option>{centers.map((c) => <option key={c.id} value={c.id}>{c.code} — {localName(c, locale)}</option>)}</select>}</Field>}
            {versions.length > 0 && (
              <Field id="st-version" label={t("st.mapping_version")}>
                {(a) => <select {...a} value={f.mappingVersion} onChange={(x) => setF({ ...f, mappingVersion: x.target.value })}><option value="">{t("st.mapping_current")}</option>{versions.map((v) => <option key={v.id} value={v.versionNo}>{t("st.mapping_version_n", { n: String(v.versionNo) })} — {v.createdAtUtc.slice(0, 10)}</option>)}</select>}
              </Field>
            )}
            <button type="submit" className="primary" disabled={!periodValid}>{t("rep.show")}</button>
            {canExport && formats.map((fmt) => <button key={fmt} type="button" disabled={!periodValid} data-testid={`st-download-${fmt}`} onClick={() => void download(fmt)}>{t(`rep.download_${fmt}`)}</button>)}
          </fieldset>
        </form>
      </Card>
      {error && <Banner kind="error" testId="st-error">{error}</Banner>}
      {data && (
        <Card title={t(`st.tab.${data.kind}`)}>
          <p className="hint" data-testid="st-meta">
            {data.asOf ? t("st.as_of") : t("st.period")}: <bdi dir="ltr">{data.asOf ? data.period.current.to : `${data.period.current.from} .. ${data.period.current.to}`}</bdi> — {t("rep.base_currency")}: <bdi dir="ltr">{data.baseCurrency}</bdi> — {t("st.mapping_version")}: <span data-testid="st-mapping">{mappingLabel}</span>
          </p>
          {data.period.comparative && <p className="hint" data-testid="st-comparative-period">{t("rep.comparative_period")}: <bdi dir="ltr">{data.asOf ? data.period.comparative.to : `${data.period.comparative.from} .. ${data.period.comparative.to}`}</bdi> ({data.period.comparative.label})</p>}
          {data.period.comparativeUnavailable && <Banner kind="info" testId="st-comparative-unavailable">{t(`rep.unavailable.${data.period.comparativeUnavailable}`)}</Banner>}
          {!data.applicable && <Banner kind="info" testId="st-not-applicable">{t(`st.not_applicable.${data.notApplicableReason ?? "unknown"}`)}</Banner>}
          {data.applicable && !data.complete && (
            <Banner kind="warning" testId="st-incomplete">
              {data.issues.some((i) => i.code === "no_mapping") ? t("st.no_mapping_banner") : t("st.incomplete_banner")}
              {" "}{canMap && <button type="button" data-testid="st-open-mapping" onClick={() => navigate("/accounting/statement-mapping")}>{t("st.open_mapping")}</button>}
            </Banner>
          )}
          {data.applicable && (
            <>
              <div className="table-scroll">
                <table className="table" data-testid="st-table">
                  <thead><tr><th>{t("st.col.item")}</th><th>{t("st.col.amount")}</th>{data.hasComparative && <><th>{t("st.col.comparative")}</th><th>{t("st.col.change")}</th></>}</tr></thead>
                  <tbody>
                    {data.lines.map((l, i) => {
                      const cls = l.kind === "header" ? "row-group" : l.kind === "total" ? "row-total" : l.kind === "subtotal" ? "row-subtotal" : undefined;
                      const label = l.accountId || l.key === "unclosed_result" ? localName({ nameAr: l.nameAr, nameEn: l.nameEn, code: l.code ?? "" }, locale) : lineTitle(l.key);
                      return (
                        <tr key={`${l.key}-${l.accountId ?? i}-${i}`} className={cls} data-testid={`st-line-${l.accountId ? l.code : l.key}`}>
                          <td>{l.accountId ? <><bdi dir="ltr">{l.code}</bdi> {label}</> : label}</td>
                          <td>{l.kind === "header" ? "" : <bdi dir="ltr">{l.current}</bdi>}</td>
                          {data.hasComparative && <><td>{l.kind === "header" ? "" : <bdi dir="ltr">{l.comparative ?? ""}</bdi>}</td><td>{l.kind === "header" ? "" : <bdi dir="ltr">{l.change ?? ""}</bdi>}</td></>}
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              {data.lines.length === 0 && <p data-testid="st-empty">{t("rep.empty")}</p>}
              {data.issues.filter((i) => i.code === "unclassified_accounts").map((i) => <p key={i.code} className="hint" data-testid="st-issue">{t("st.issue.unclassified_accounts", { n: String(i.count), amount: i.amount })}</p>)}
            </>
          )}
          {data.checks.length > 0 && (
            <>
              <h3>{t("st.checks_title")}</h3>
              <ul data-testid="st-checks">{data.checks.map((c) => <li key={c.code} data-testid={`st-check-${c.code}`}>{t(`st.check.${c.code}`)}: <strong>{statusText(c.status)}</strong>{c.status === "failed" && c.difference !== "0.0000" ? <> (<bdi dir="ltr">{c.difference}</bdi>)</> : null}</li>)}</ul>
            </>
          )}
        </Card>
      )}
    </>
  );
}
