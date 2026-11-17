import { ApiError, type NegativeCloseReport, type NegativeItemFlag, type NegativeObligation, type NegativeStockSettings } from "@hasm/api-client";
import { Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { useReportUnsaved } from "../unsaved";
import { blankToNull, invName } from "./inventoryCommon";

/**
 * The negative-stock exception (Phase 3.8B-3): ONE shared screen for Web and Desktop. Tabs by business meaning: the versioned company ENABLEMENT (off by default, optional second approver), the ELIGIBLE items (moving
 * average, untracked), the OBLIGATIONS (open or all, with their settlements) and the CLOSE REPORT of a period end (what blocks the Final Close and the reconciliation of the obligation and variance accounts). Every
 * rule is enforced by the server; this screen only shows and submits. The exception itself is approved on the issue document (stock documents screen).
 */
const num = (n: number | null | undefined) => (n === null || n === undefined ? "—" : String(Number(n.toFixed(10))));
const money = (n: number) => n.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 6, useGrouping: false });
const monthEnd = () => { const d = new Date(); const e = new Date(d.getFullYear(), d.getMonth() + 1, 0); const p = (n: number) => String(n).padStart(2, "0"); return `${e.getFullYear()}-${p(e.getMonth() + 1)}-${p(e.getDate())}`; };

export function NegativeStockPage() {
  const { t } = useI18n();
  const auth = useAuth();
  const canSettings = auth.can("company.inventorysettings.view") || auth.can("company.inventorysettings.configure");
  const canReport = auth.can("company.inventoryaccounting.view") || auth.can("company.inventoryaccounting.configure");
  const [tab, setTab] = useState(canSettings ? "settings" : "obligations");
  const tabs: TabItem[] = [
    ...(canSettings ? [{ id: "settings", label: t("neg.tab.settings"), content: <SettingsTab /> }, { id: "items", label: t("neg.tab.items"), content: <ItemsTab /> }] : []),
    { id: "obligations", label: t("neg.tab.obligations"), content: <ObligationsTab /> },
    ...(canReport ? [{ id: "close", label: t("neg.tab.close"), content: <CloseTab /> }] : []),
  ];
  return (
    <section aria-labelledby="page-title" data-testid="negative-stock">
      <PageHeader title={t("neg.title")} description={t("neg.subtitle")} />
      <Banner kind="info" testId="neg-note">{t("neg.note")}</Banner>
      <Card>
        <Tabs label={t("neg.tabs_label")} tabs={tabs} value={tabs.some((x) => x.id === tab) ? tab : tabs[0].id} onChange={setTab} testId="neg-tabs" />
      </Card>
    </section>
  );
}

function SettingsTab() {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [data, setData] = useState<{ settings: NegativeStockSettings; canConfigure: boolean } | null>(null);
  const [form, setForm] = useState({ enabled: false, second: false, note: "" });
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const load = useCallback(async () => {
    try {
      const d = await auth.api.getNegativeStockSettings();
      setData(d); setForm({ enabled: d.settings.enabled, second: d.settings.requireSecondApprover, note: "" }); setConflict(false);
    } catch (e) { if (!auth.handleError(e)) setError(errorText(tRef.current, e, "neg.error.")); }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);
  const dirty = !!data && (form.enabled !== data.settings.enabled || form.second !== data.settings.requireSecondApprover || form.note.trim() !== "");
  useReportUnsaved("negative-stock-settings", dirty, t("neg.tab.settings"));
  if (!data) return error ? <Banner kind="error">{error}</Banner> : <Loading />;
  const canEdit = data.canConfigure;
  async function save(e: FormEvent) {
    e.preventDefault();
    if (!canEdit || !data) return;
    setBusy(true); setError(null); setNotice(null); setConflict(false);
    try {
      await auth.api.putNegativeStockSettings({ enabled: form.enabled, requireSecondApprover: form.second, note: blankToNull(form.note) }, data.settings.versionNo);
      await load();
      setNotice(t("neg.saved"));
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else setError(errorText(t, err, "neg.error."));
    } finally { setBusy(false); }
  }
  return (
    <form onSubmit={save} noValidate data-testid="neg-settings">
      <p><span className="chip" data-testid="neg-version">{data.settings.versionNo === 0 ? t("neg.not_configured") : t("neg.version_n", { n: String(data.settings.versionNo) })}</span></p>
      {!canEdit && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {conflict && <Banner kind="error" testId="conflict">{t("neg.error.settings_version_conflict")} <button type="button" onClick={() => void load()}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error" testId="neg-error">{error}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
      <div className="fields">
        <Field id="neg-enabled" label={t("neg.enabled")} hint={t("neg.enabled_hint")}>{(aria) => <input {...aria} type="checkbox" checked={form.enabled} disabled={!canEdit} onChange={(e) => setForm((f) => ({ ...f, enabled: e.target.checked }))} />}</Field>
        <Field id="neg-second" label={t("neg.second_approver")} hint={t("neg.second_approver_hint")}>{(aria) => <input {...aria} type="checkbox" checked={form.second} disabled={!canEdit} onChange={(e) => setForm((f) => ({ ...f, second: e.target.checked }))} />}</Field>
        <Field id="neg-note" label={t("neg.note_field")}>{(aria) => <input {...aria} value={form.note} readOnly={!canEdit} maxLength={500} onChange={(e) => setForm((f) => ({ ...f, note: e.target.value }))} />}</Field>
      </div>
      <div className="actions">
        <button type="button" disabled={!dirty || busy} onClick={() => setForm({ enabled: data.settings.enabled, second: data.settings.requireSecondApprover, note: "" })}>{t("inv.cancel")}</button>
        {canEdit && <button type="submit" className="primary" disabled={!dirty || busy}>{busy ? t("loading") : t("inv.save")}</button>}
      </div>
    </form>
  );
}

function ItemsTab() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [items, setItems] = useState<NegativeItemFlag[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const canEdit = auth.can("company.inventorysettings.configure");
  const load = useCallback(async () => {
    try { setItems((await auth.api.listNegativeItemFlags()).items); }
    catch (e) { if (!auth.handleError(e)) setError(errorText(tRef.current, e, "neg.error.")); }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);
  if (!items) return error ? <Banner kind="error">{error}</Banner> : <Loading />;
  async function toggle(i: NegativeItemFlag) {
    setBusy(i.itemId); setError(null);
    try { const v = await auth.api.setNegativeItemFlag(i.itemId, !i.eligible, null); setItems((list) => (list ?? []).map((x) => (x.itemId === v.itemId ? v : x))); }
    catch (e) { if (!auth.handleError(e)) setError(errorText(t, e, "neg.error.")); }
    finally { setBusy(null); }
  }
  return (
    <div data-testid="neg-items">
      <p className="hint">{t("neg.items_hint")}</p>
      {error && <Banner kind="error" testId="neg-items-error">{error}</Banner>}
      <div className="table-wrap">
        <table className="data-table">
          <caption className="visually-hidden">{t("neg.tab.items")}</caption>
          <thead><tr><th scope="col">{t("inv.code")}</th><th scope="col">{t("neg.item")}</th><th scope="col">{t("neg.valuation")}</th><th scope="col">{t("neg.tracked")}</th><th scope="col">{t("neg.eligible")}</th><th scope="col">{t("neg.open_obligation")}</th><th scope="col"><span className="visually-hidden">{t("neg.eligible")}</span></th></tr></thead>
          <tbody>
            {items.length === 0 && <tr><td colSpan={7}>{t("neg.items_none")}</td></tr>}
            {items.map((i) => {
              const possible = i.eligible || (i.effectiveValuation === 2 && !i.tracked && i.isActive);
              return (
                <tr key={i.itemId} data-testid={`neg-item-${i.code}`}>
                  <td><bdi dir="ltr">{i.code}</bdi></td><td><bdi>{invName(i, locale)}</bdi></td>
                  <td>{i.effectiveValuation ? t(`neg.valuation.${i.effectiveValuation}`) : t("neg.valuation.none")}</td>
                  <td>{i.tracked ? "✓" : ""}</td><td data-testid={`neg-eligible-${i.code}`}>{i.eligible ? "✓" : ""}</td><td>{i.hasOpenObligation ? "✓" : ""}</td>
                  <td>{canEdit && possible && <button type="button" disabled={busy !== null || (i.eligible && i.hasOpenObligation)} onClick={() => void toggle(i)}>{i.eligible ? t("neg.remove_eligible") : t("neg.make_eligible")}</button>}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function ObligationRows({ obligations }: { obligations: NegativeObligation[] }) {
  const { t, locale } = useI18n();
  return (
    <div className="table-wrap">
      <table className="data-table" data-testid="neg-obligations">
        <caption className="visually-hidden">{t("neg.tab.obligations")}</caption>
        <thead><tr><th scope="col">{t("neg.document")}</th><th scope="col">{t("neg.item")}</th><th scope="col">{t("neg.date")}</th><th scope="col">{t("neg.original_qty")}</th><th scope="col">{t("neg.provisional_total")}</th><th scope="col">{t("neg.open_qty")}</th><th scope="col">{t("neg.open_value")}</th><th scope="col">{t("neg.source")}</th><th scope="col">{t("neg.age")}</th><th scope="col">{t("neg.status")}</th><th scope="col">{t("neg.settlements")}</th></tr></thead>
        <tbody>
          {obligations.length === 0 && <tr><td colSpan={11}>{t("neg.no_obligations")}</td></tr>}
          {obligations.map((o) => (
            <tr key={o.id} data-testid={`neg-obligation-${o.documentNumber ?? o.id}-${o.lineNo}`}>
              <td><bdi dir="ltr">{o.documentNumber ?? "—"}</bdi> / {o.lineNo}</td>
              <td><bdi dir="ltr">{o.itemCode}</bdi> <bdi>{invName({ nameAr: o.itemNameAr, nameEn: o.itemNameEn, code: o.itemCode }, locale)}</bdi></td>
              <td><bdi dir="ltr">{o.effectiveDate}</bdi></td>
              <td><bdi dir="ltr">{num(o.originalQuantity)}</bdi></td><td><bdi dir="ltr">{money(o.provisionalTotal)}</bdi></td>
              <td><bdi dir="ltr" data-testid="neg-open-qty">{num(o.openQuantity)}</bdi></td><td><bdi dir="ltr">{money(o.openProvisional)}</bdi></td>
              <td title={o.justification ?? undefined}>{t(`neg.source.${o.source}`)}</td><td>{o.ageDays}</td>
              <td><span className={`chip ${o.status === 2 ? "chip-ok" : o.status === 1 ? "chip-off" : ""}`}>{t(`neg.status.${o.status}`)}</span></td>
              <td>
                {o.settlements.length === 0 ? (o.status === 1 ? <span className="hint">{t("neg.resolve")}</span> : "—") : (
                  <ul className="plain">
                    {o.settlements.map((s) => (
                      <li key={s.id}><bdi dir="ltr">{s.receiptNumber ?? "—"} {s.effectiveDate}: {num(s.matchedQuantity)} × {money(s.provisionalAmount)} → {money(s.actualAmount)} ({t("neg.difference")} {money(s.difference)})</bdi>{s.reversed ? ` — ${t("neg.reversed")}` : ""}</li>
                    ))}
                  </ul>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ObligationsTab() {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [status, setStatus] = useState<"open" | "all">("open");
  const [obligations, setObligations] = useState<NegativeObligation[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async (s: "open" | "all") => {
    setObligations(null); setError(null);
    try { setObligations((await auth.api.listNegativeObligations(s)).obligations); }
    catch (e) { if (!auth.handleError(e)) setError(errorText(tRef.current, e, "neg.error.")); }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(status); }, [load, status]);
  return (
    <div data-testid="neg-obligations-tab">
      <div className="fields">
        <Field id="neg-status" label={t("neg.filter")}>
          {(aria) => (
            <select {...aria} value={status} onChange={(e) => setStatus(e.target.value as "open" | "all")}>
              <option value="open">{t("neg.filter.open")}</option>
              <option value="all">{t("neg.filter.all")}</option>
            </select>
          )}
        </Field>
      </div>
      {error && <Banner kind="error" testId="neg-obligations-error">{error}</Banner>}
      {!obligations ? (!error && <Loading />) : <ObligationRows obligations={obligations} />}
    </div>
  );
}

function CloseTab() {
  const { t } = useI18n();
  const auth = useAuth();
  const [end, setEnd] = useState(monthEnd());
  const [report, setReport] = useState<NegativeCloseReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function show(e: FormEvent) {
    e.preventDefault();
    if (!/^\d{4}-\d{2}-\d{2}$/.test(end)) { setError(t("field.invalid")); return; }
    setBusy(true); setError(null);
    try { setReport(await auth.api.getNegativeCloseReport(end)); }
    catch (err) { if (!auth.handleError(err)) setError(errorText(t, err, "neg.error.")); }
    finally { setBusy(false); }
  }
  return (
    <div data-testid="neg-close">
      <p className="hint">{t("neg.close_hint")}</p>
      <form onSubmit={show} noValidate className="fields">
        <Field id="neg-end" label={t("neg.period_end")}>{(aria) => <input {...aria} type="date" dir="ltr" value={end} onChange={(e) => setEnd(e.target.value)} />}</Field>
        <div className="actions"><button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("neg.show_report")}</button></div>
      </form>
      {error && <Banner kind="error" testId="neg-close-error">{error}</Banner>}
      {report && (
        <div data-testid="neg-report">
          {report.open.length > 0 && <Banner kind="warning" testId="neg-soft-warning">{t("neg.soft_warning")}</Banner>}
          <p><span className={`chip ${report.blocksFinalClose ? "chip-off" : "chip-ok"}`} data-testid="neg-blocks">{report.blocksFinalClose ? t("neg.blocks_final") : t("neg.final_ok")}</span>{" "}
            <span className={`chip ${report.reconciled ? "chip-ok" : "chip-off"}`} data-testid="neg-reconciled">{report.reconciled ? t("neg.reconciled") : t("neg.not_reconciled")}</span></p>
          <dl className="summary">
            <dt>{t("neg.expected_o")}</dt><dd><bdi dir="ltr" data-testid="neg-expected">{money(report.expectedObligationBalance)}</bdi></dd>
            <dt>{t("neg.ledger_o")}</dt><dd><bdi dir="ltr" data-testid="neg-ledger">{money(report.ledgerObligationBalance)}</bdi></dd>
            <dt>{t("neg.differences")}</dt><dd><bdi dir="ltr">{money(report.settlementDifferences)}</bdi></dd>
            <dt>{t("neg.ledger_v")}</dt><dd><bdi dir="ltr">{money(report.ledgerVariance)}</bdi></dd>
            <dt>{t("neg.failed_journals")}</dt><dd>{report.failedSettlementJournals}</dd>
          </dl>
          <ObligationRows obligations={report.open} />
        </div>
      )}
    </div>
  );
}
