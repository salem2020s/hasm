import { ApiError, ImportRowStatus } from "@hasm/api-client";
import type { ImportAdapterInfo, ImportAdaptersResponse, ImportBatch, ImportDecision, ImportEvent, ImportFieldDef, ImportFieldsResponse, ImportMapping, ImportRowsPage, ImportSettings, ImportStatus, ImportTemplate } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, Loading, PageHeader } from "../components";
import { localName } from "../ledgerCommon";

/**
 * Imports (Phase 3.2D): ONE shared screen for the Web app and the Windows Desktop app. The screen only drives the server: it uploads the raw file, shows what the server found and records the user's
 * explicit choices. Every rule (file validation and security scan, encryption, normalization, duplicate and conflict detection, the plan, independent approval, atomic application, retention) is enforced
 * by the server and PostgreSQL; nothing here decides anything. Imported values are only ever rendered as text.
 */

const STATUSES: ImportStatus[] = ["uploaded", "staged", "mapped", "validated", "submitted", "approved", "applying", "applied", "failed", "rejected", "cancelled", "expired"];
const TERMINAL: ImportStatus[] = ["applied", "rejected", "cancelled", "expired"];
const PAGE = 50;
const STEPS = ["upload", "read", "map", "validate", "approve", "apply"] as const;
const DONE_STEPS: Record<ImportStatus, number> = { uploaded: 1, staged: 2, mapped: 3, validated: 4, submitted: 5, approved: 5, applying: 6, applied: 6, failed: 5, rejected: 5, cancelled: 1, expired: 1 };

/** Translated message for a failed call: field-level codes (for example the refusal reason of a file) first, then the screen's own error texts, then the generic ones. */
function importError(t: (k: string, v?: Record<string, string>) => string, e: unknown): string {
  if (e instanceof ApiError) {
    const fields = e.details.fields ?? [];
    if (fields.length > 0) {
      const seen = new Set<string>();
      for (const f of fields) { const key = `imp.error.${f.code}`; seen.add(t(key) === key ? t("field.invalid") : t(key)); }
      return [...seen].join(" ");
    }
    return errorText(t, e, "imp.error.");
  }
  return errorText(t, e);
}

function useAction(reload: () => Promise<void>) {
  const { t } = useI18n();
  const auth = useAuth();
  const tRef = useRef(t);
  tRef.current = t;
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const run = useCallback(async <T,>(action: () => Promise<T>, message: string): Promise<T | undefined> => {
    setBusy(true); setError(null); setNotice(null);
    try {
      const result = await action();
      await reload();
      setNotice(message);
      return result;
    } catch (e) {
      if (auth.handleError(e)) return undefined;
      setError(importError(tRef.current, e));
      if (e instanceof ApiError && e.status === 412) await reload().catch(() => undefined);
      return undefined;
    } finally { setBusy(false); }
  }, [auth, reload]);
  return { busy, error, notice, run, setError, setNotice };
}

const shortHash = (h: string | null) => (h ? h.slice(0, 16) : "—");
const mb = (bytes: number) => String(Math.max(1, Math.round(bytes / 1024 / 1024)));

function Status({ status }: { status: ImportStatus }) {
  const { t } = useI18n();
  return <span className={`badge badge-${status}`} data-testid="imp-status">{t(`imp.status.${status}`)}</span>;
}

// ============================================================================================================================================================== page

export function ImportsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [adapters, setAdapters] = useState<ImportAdaptersResponse | null>(null);
  const [list, setList] = useState<ImportBatch[] | null>(null);
  const [filter, setFilter] = useState<ImportStatus | "">("");
  const [openId, setOpenId] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [a, l] = await Promise.all([auth.api.importAdapters(), auth.api.listImports({ status: filter || undefined, limit: 100 })]);
      setAdapters(a); setList(l.imports); setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(importError(tRef.current, e)); }
  }, [auth.api, auth.handleError, filter]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);

  if (loadError) return <Banner kind="error" testId="imp-load-error">{loadError}</Banner>;
  if (!adapters || !list) return <Loading />;
  return (
    <>
      <PageHeader title={t("imp.title")} description={t("imp.subtitle")} />
      <Banner kind="info">{t("imp.retention_note")}</Banner>
      {openId ? (
        <BatchPanel key={openId} id={openId} adapters={adapters} onChanged={load} onClose={() => setOpenId(null)} />
      ) : (
        <>
          <UploadCard adapters={adapters} onUploaded={async (b) => { await load(); setOpenId(b.id); }} />
          <Card title={t("imp.history_title")}>
            <Field id="imp-filter" label={t("imp.filter_status")}>
              {(a) => (
                <select {...a} value={filter} onChange={(e) => setFilter(e.target.value as ImportStatus | "")}>
                  <option value="">{t("imp.all_statuses")}</option>
                  {STATUSES.map((s) => <option key={s} value={s}>{t(`imp.status.${s}`)}</option>)}
                </select>
              )}
            </Field>
            {list.length === 0 ? <p data-testid="imp-none">{t("imp.none")}</p> : (
              <table className="table" data-testid="imp-list">
                <thead><tr><th>{t("imp.col.file")}</th><th>{t("imp.col.adapter")}</th><th>{t("imp.col.status")}</th><th>{t("imp.col.rows")}</th><th>{t("imp.col.created")}</th><th>{t("imp.col.expires")}</th><th /></tr></thead>
                <tbody>
                  {list.map((b) => {
                    const adapter = adapters.adapters.find((a) => a.code === b.adapter);
                    return (
                      <tr key={b.id} data-testid={`imp-row-${b.id}`}>
                        <td><bdi dir="auto">{b.fileName}</bdi></td>
                        <td>{adapter ? localName(adapter, locale) : b.adapter}</td>
                        <td><Status status={b.status} /></td>
                        <td>{b.rowCount}</td>
                        <td><bdi dir="ltr">{b.createdAtUtc.slice(0, 10)}</bdi></td>
                        <td><bdi dir="ltr">{b.expiresAtUtc.slice(0, 10)}</bdi></td>
                        <td><button type="button" onClick={() => setOpenId(b.id)}>{t("imp.open")}</button></td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            )}
          </Card>
          <SettingsCard />
        </>
      )}
    </>
  );
}

// ============================================================================================================================================================== upload

function UploadCard({ adapters, onUploaded }: { adapters: ImportAdaptersResponse; onUploaded: (b: ImportBatch) => Promise<void> }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const usable = adapters.adapters.filter((a) => a.canUpload && auth.can("company.imports.upload"));
  const [adapter, setAdapter] = useState(usable[0]?.code ?? "");
  const [file, setFile] = useState<File | null>(null);
  const key = useRef<string>("");
  const noop = useCallback(async () => undefined, []);
  const act = useAction(noop);
  if (usable.length === 0) return <Card title={t("imp.new_title")}><p data-testid="imp-no-adapter">{t("imp.no_adapter")}</p></Card>;
  const upload = async () => {
    if (!file || !adapter) return;
    if (!key.current) key.current = typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
    const batch = await act.run(() => auth.api.uploadImport(adapter, file, file.name, key.current), t("imp.uploaded"));
    if (batch) { key.current = ""; setFile(null); await onUploaded(batch); }
  };
  return (
    <Card title={t("imp.new_title")} hint={t("imp.file_hint", { mb: mb(adapters.limits.maxFileBytes), rows: String(adapters.limits.maxRows) })}>
      {act.error && <Banner kind="error" testId="imp-upload-error">{act.error}</Banner>}
      <Field id="imp-adapter" label={t("imp.adapter")} required>
        {(a) => (
          <select {...a} value={adapter} onChange={(e) => setAdapter(e.target.value)}>
            {usable.map((u: ImportAdapterInfo) => <option key={u.code} value={u.code}>{localName(u, locale)}</option>)}
          </select>
        )}
      </Field>
      <Field id="imp-file" label={t("imp.file")} required>
        {(a) => <input {...a} type="file" accept=".xlsx,.csv" onChange={(e) => { setFile(e.target.files?.[0] ?? null); key.current = ""; }} />}
      </Field>
      <button type="button" className="primary" disabled={!file || !adapter || act.busy} onClick={() => void upload()}>{t("imp.upload")}</button>
    </Card>
  );
}

// ============================================================================================================================================================== batch panel

function BatchPanel({ id, adapters, onChanged, onClose }: { id: string; adapters: ImportAdaptersResponse; onChanged: () => Promise<void>; onClose: () => void }) {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [batch, setBatch] = useState<ImportBatch | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<"apply" | "cancel" | null>(null);

  const reload = useCallback(async () => {
    try { setBatch(await auth.api.getImport(id)); setLoadError(null); await onChanged(); }
    catch (e) { if (!auth.handleError(e)) setLoadError(importError(tRef.current, e)); }
  }, [auth.api, auth.handleError, id, onChanged]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void reload(); }, [reload]);
  const act = useAction(reload);

  if (loadError) return <Banner kind="error" testId="imp-batch-error">{loadError}</Banner>;
  if (!batch) return <Loading />;
  const adapter = adapters.adapters.find((a) => a.code === batch.adapter);
  const canUpload = auth.can("company.imports.upload") && (adapter?.canUpload ?? false);
  const canSubmit = auth.can("company.imports.submit") && (adapter?.canUpload ?? false);
  const canApply = auth.can("company.imports.apply") && (adapter?.canUpload ?? false);
  const canCancel = auth.can("company.imports.cancel") && (adapter?.canUpload ?? false);
  const status = batch.status;
  const finished = TERMINAL.includes(status);
  const done = DONE_STEPS[status];

  return (
    <>
      <PageHeader title={batch.fileName} description={adapter ? localName(adapter, locale) : batch.adapter} actions={<button type="button" onClick={onClose}>{t("imp.back")}</button>} />
      {act.error && <Banner kind="error" testId="imp-error">{act.error}</Banner>}
      {act.notice && <Banner kind="success" testId="imp-notice">{act.notice}</Banner>}
      <Card>
        <ol className="steps" aria-label={t("imp.title")} data-testid="imp-steps">
          {STEPS.map((s, i) => <li key={s} className={i < done ? "step-done" : ""} aria-current={i === done ? "step" : undefined}>{t(`imp.step.${s}`)}</li>)}
        </ol>
        <dl className="facts" data-testid="imp-facts">
          <dt>{t("imp.col.status")}</dt><dd><Status status={status} /></dd>
          <dt>{t("imp.fact.size")}</dt><dd><bdi dir="ltr">{batch.fileSize}</bdi></dd>
          <dt>{t("imp.fact.sha")}</dt><dd><bdi dir="ltr" title={batch.fileSha256}>{shortHash(batch.fileSha256)}</bdi></dd>
          <dt>{t("imp.fact.rows")}</dt><dd>{batch.rowCount}</dd>
          <dt>{t("imp.fact.plan")}</dt><dd><bdi dir="ltr" data-testid="imp-plan-hash" title={batch.planHash ?? ""}>{shortHash(batch.planHash)}</bdi></dd>
          <dt>{t("imp.fact.expires")}</dt><dd><bdi dir="ltr">{batch.expiresAtUtc.slice(0, 10)}</bdi></dd>
          {batch.purgeAfterUtc && <><dt>{t("imp.fact.purge")}</dt><dd><bdi dir="ltr">{batch.purgeAfterUtc.slice(0, 10)}</bdi></dd></>}
        </dl>
      </Card>

      {canUpload && batch.originalAvailable && ["uploaded", "staged", "mapped", "validated", "failed"].includes(status) && <StageCard batch={batch} act={act} />}
      {canUpload && batch.rowsAvailable && ["staged", "mapped", "validated", "failed"].includes(status) && <MappingCard batch={batch} act={act} />}
      {canUpload && batch.mapping && ["mapped", "validated", "failed"].includes(status) && (
        <Card title={t("imp.validate_title")} hint={t("imp.validate_hint")}>
          <button type="button" className="primary" disabled={act.busy} onClick={() => void act.run(() => auth.api.dryRunImport(batch.id, batch.revision), t("imp.validated"))}>{t("imp.validate_button")}</button>
        </Card>
      )}
      {batch.plan && <PlanCard batch={batch} />}
      {batch.plan && batch.rowsAvailable && <RowsCard batch={batch} act={act} canDecide={canUpload && status === "validated"} />}

      <ApprovalCard batch={batch} act={act} canSubmit={canSubmit} />
      {(status === "approved" || status === "failed") && canApply && (
        <Card title={t("imp.apply_title")} hint={t("imp.apply_hint")}>
          {status === "failed" && <FailureBlock batch={batch} />}
          <button type="button" className="primary" data-testid="imp-apply" disabled={act.busy} onClick={() => setConfirm("apply")}>{t("imp.apply")}</button>
        </Card>
      )}
      {status === "failed" && !canApply && <Card><FailureBlock batch={batch} /></Card>}
      {status === "applied" && batch.result && (
        <Card title={t("imp.result_title")}>
          <dl className="facts" data-testid="imp-result">
            <dt>{t("imp.result.operations")}</dt><dd>{String(batch.result["operations"] ?? "")}</dd>
            <dt>{t("imp.result.applied_at")}</dt><dd><bdi dir="ltr">{String(batch.result["appliedAtUtc"] ?? "").slice(0, 19).replace("T", " ")}</bdi></dd>
          </dl>
        </Card>
      )}

      {!finished && status !== "applying" && (
        <Card>
          <div className="actions">
            {canUpload && status !== "approved" && <button type="button" disabled={act.busy} onClick={() => void act.run(() => auth.api.renewImport(batch.id, batch.revision), t("imp.renewed"))}>{t("imp.renew")}</button>}
            {canCancel && <button type="button" className="danger" disabled={act.busy} data-testid="imp-cancel" onClick={() => setConfirm("cancel")}>{t("imp.cancel")}</button>}
          </div>
        </Card>
      )}
      <EventsCard id={batch.id} revision={batch.revision} />

      {confirm === "apply" && (
        <ConfirmDialog title={t("imp.apply_confirm_title")} message={t("imp.apply_confirm_message", { n: String(batch.plan?.summary.operations ?? 0) })} confirmLabel={t("imp.apply")}
          onCancel={() => setConfirm(null)} onConfirm={() => { setConfirm(null); void act.run(() => auth.api.applyImport(batch.id, batch.revision), t("imp.applied")); }} />
      )}
      {confirm === "cancel" && (
        <ConfirmDialog title={t("imp.cancel_confirm_title")} message={t("imp.cancel_confirm_message")} confirmLabel={t("imp.cancel")}
          onCancel={() => setConfirm(null)} onConfirm={() => { setConfirm(null); void act.run(() => auth.api.cancelImport(batch.id, batch.revision), t("imp.cancelled")); }} />
      )}
    </>
  );
}

type Act = ReturnType<typeof useAction>;

function FailureBlock({ batch }: { batch: ImportBatch }) {
  const { t } = useI18n();
  const code = String(batch.result?.["code"] ?? "");
  const key = `imp.error.${code}`;
  return (
    <div data-testid="imp-failure">
      <Banner kind="error"><strong>{t("imp.failed_title")}.</strong> {t("imp.failed_hint")} <span>{t("imp.failure_code")}: {code && t(key) !== key ? t(key) : code}</span></Banner>
    </div>
  );
}

// ============================================================================================================================================================== stage

const ENCODINGS = ["utf-8", "utf-16", "windows-1256"];
const DELIMS: [string, string][] = [["", "imp.delim.auto"], [",", "imp.delim.comma"], [";", "imp.delim.semicolon"], ["\t", "imp.delim.tab"], ["|", "imp.delim.pipe"]];

function StageCard({ batch, act }: { batch: ImportBatch; act: Act }) {
  const { t } = useI18n();
  const auth = useAuth();
  const [sheet, setSheet] = useState(batch.options.sheet ?? 0);
  const [header, setHeader] = useState(String(batch.options.headerRow ?? 1));
  const [encoding, setEncoding] = useState(batch.options.encoding ?? "utf-8");
  const [delimiter, setDelimiter] = useState(batch.options.delimiter ?? "");
  const csv = batch.fileKind === "csv";
  const headerValid = /^\d{1,4}$/.test(header) && Number(header) >= 1 && Number(header) <= 1000;
  return (
    <Card title={t("imp.stage_title")} hint={t("imp.stage_hint")}>
      {!csv && (
        <Field id="imp-sheet" label={t("imp.sheet")}>
          {(a) => (
            <select {...a} value={sheet} onChange={(e) => setSheet(Number(e.target.value))}>
              {batch.sheets.map((s) => <option key={s.index} value={s.index}>{s.name}{s.hidden ? ` ${t("imp.sheet_hidden")}` : ""} ({s.rows})</option>)}
            </select>
          )}
        </Field>
      )}
      <Field id="imp-header" label={t("imp.header_row")} error={headerValid ? undefined : t("field.invalid")}>
        {(a) => <input {...a} inputMode="numeric" value={header} onChange={(e) => setHeader(e.target.value)} />}
      </Field>
      {csv && (
        <>
          <Field id="imp-encoding" label={t("imp.encoding")}>
            {(a) => <select {...a} value={encoding} onChange={(e) => setEncoding(e.target.value)}>{ENCODINGS.map((x) => <option key={x} value={x}>{x}</option>)}</select>}
          </Field>
          <Field id="imp-delimiter" label={t("imp.delimiter")}>
            {(a) => <select {...a} value={delimiter} onChange={(e) => setDelimiter(e.target.value)}>{DELIMS.map(([v, k]) => <option key={k} value={v}>{t(k)}</option>)}</select>}
          </Field>
        </>
      )}
      <button type="button" className="primary" disabled={act.busy || !headerValid} data-testid="imp-stage"
        onClick={() => void act.run(() => auth.api.stageImport(batch.id, csv ? { headerRow: Number(header), encoding, delimiter: delimiter || undefined } : { sheet, headerRow: Number(header) }, batch.revision), t("imp.staged"))}>{t("imp.stage_button")}</button>
    </Card>
  );
}

// ============================================================================================================================================================== mapping

interface MapState { cols: Record<string, string>; defaults: Record<string, string>; decimal: string; thousands: string; date: string }

function fromMapping(m: ImportMapping | null): MapState {
  const cols: Record<string, string> = {}; const defaults: Record<string, string> = {};
  for (const [k, v] of Object.entries(m?.fields ?? {})) { cols[k] = v.column === null || v.column === undefined ? "" : String(v.column); defaults[k] = v.default ?? ""; }
  return { cols, defaults, decimal: m?.options?.decimalSeparator ?? ".", thousands: m?.options?.thousandsSeparator ?? ",", date: m?.options?.dateFormat ?? "yyyy-MM-dd" };
}

function toMapping(fields: ImportFieldDef[], s: MapState): ImportMapping {
  const out: ImportMapping["fields"] = {};
  for (const f of fields) {
    const col = s.cols[f.key] ?? ""; const def = (s.defaults[f.key] ?? "").trim();
    if (col !== "" || def !== "") out[f.key] = { column: col === "" ? null : Number(col), default: def === "" ? null : def };
  }
  return { fields: out, options: { decimalSeparator: s.decimal, thousandsSeparator: s.thousands, dateFormat: s.date } };
}

function MappingCard({ batch, act }: { batch: ImportBatch; act: Act }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const [defs, setDefs] = useState<ImportFieldsResponse | null>(null);
  const [state, setState] = useState<MapState>(() => fromMapping(batch.mapping));
  const headers = batch.options.headers ?? [];
  const tRef = useRef(t);
  tRef.current = t;
  // reusable mapping templates (by header name): applying one only PRE-FILLS the form below; saving the mapping, validation, dry run and approval are unchanged
  const [templates, setTemplates] = useState<ImportTemplate[]>([]);
  const [tplName, setTplName] = useState("");
  const [tplPick, setTplPick] = useState("");
  const [tplNote, setTplNote] = useState<string | null>(null);
  const [tplBusy, setTplBusyState] = useState(false);
  const [tplRetire, setTplRetire] = useState(false);
  const tplInFlight = useRef(false);                      // synchronous guard: a double click must not send two requests on the same version
  const setTplBusy = (v: boolean) => { tplInFlight.current = v; setTplBusyState(v); };
  const loadTemplates = useCallback(async () => { try { setTemplates((await auth.api.listImportTemplates(batch.adapter)).templates); } catch { setTemplates([]); } }, [auth.api, batch.adapter]);
  useEffect(() => { void loadTemplates(); }, [loadTemplates]);

  useEffect(() => {
    let alive = true;
    void (async () => {
      try {
        const d = await auth.api.importFields(batch.id);
        if (!alive) return;
        setDefs(d);
        if (!batch.mapping) { const s = await auth.api.suggestImportMapping(batch.id); if (alive) setState(fromMapping(s.mapping)); }
      } catch (e) { if (!auth.handleError(e) && alive) act.setError(importError(tRef.current, e)); }
    })();
    return () => { alive = false; };
  }, [batch.id]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!defs) return <Loading />;
  const suggest = async () => { try { const s = await auth.api.suggestImportMapping(batch.id); setState(fromMapping(s.mapping)); } catch (e) { if (!auth.handleError(e)) act.setError(importError(tRef.current, e)); } };
  return (
    <Card title={t("imp.map_title")} hint={t("imp.map_hint")}>
      <table className="table" data-testid="imp-mapping">
        <thead><tr><th>{t("imp.map_field")}</th><th>{t("imp.map_column")}</th><th>{t("imp.map_default")}</th></tr></thead>
        <tbody>
          {defs.fields.map((f) => (
            <tr key={f.key} data-testid={`imp-map-${f.key}`}>
              <td>{localName(f, locale)}{f.required && <span aria-hidden="true" className="required"> *</span>}</td>
              <td>
                <select aria-label={`${localName(f, locale)} — ${t("imp.map_column")}`} value={state.cols[f.key] ?? ""} onChange={(e) => setState({ ...state, cols: { ...state.cols, [f.key]: e.target.value } })}>
                  <option value="">{t("imp.map_none")}</option>
                  {headers.map((h, i) => <option key={i} value={String(i)}>{h || `#${i + 1}`}</option>)}
                </select>
              </td>
              <td><input aria-label={`${localName(f, locale)} — ${t("imp.map_default")}`} value={state.defaults[f.key] ?? ""} maxLength={f.maxLength} onChange={(e) => setState({ ...state, defaults: { ...state.defaults, [f.key]: e.target.value } })} /></td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="form-row">
        <Field id="imp-decimal" label={t("imp.opt.decimal")}>
          {(a) => <select {...a} value={state.decimal} onChange={(e) => setState({ ...state, decimal: e.target.value, thousands: e.target.value === "," ? "." : "," })}>{defs.options.decimalSeparators.map((x) => <option key={x} value={x}>{x}</option>)}</select>}
        </Field>
        <Field id="imp-thousands" label={t("imp.opt.thousands")}>
          {(a) => <select {...a} value={state.thousands} onChange={(e) => setState({ ...state, thousands: e.target.value })}>{defs.options.thousandsSeparators.map((x) => <option key={x} value={x}>{x === "" ? t("imp.opt.none") : x === " " ? t("imp.opt.space") : x}</option>)}</select>}
        </Field>
        <Field id="imp-date" label={t("imp.opt.date")}>
          {(a) => <select {...a} value={state.date} onChange={(e) => setState({ ...state, date: e.target.value })}>{defs.options.dateFormats.map((x) => <option key={x} value={x}>{x}</option>)}</select>}
        </Field>
      </div>
      <fieldset disabled={act.busy || tplBusy} data-testid="imp-templates">
        <legend>{t("imp.tpl.title")}</legend>
        <p className="hint">{t("imp.tpl.hint")}</p>
        <div className="form-row">
          <Field id="imp-tpl-pick" label={t("imp.tpl.apply_label")}>
            {(a) => <select {...a} value={tplPick} onChange={(e) => { setTplPick(e.target.value); setTplNote(null); }}><option value="">{t("imp.map_none")}</option>{templates.filter((x) => !x.isRetired).map((x) => <option key={x.id} value={x.name}>{x.name} (v{x.version})</option>)}</select>}
          </Field>
          <button type="button" disabled={!tplPick} data-testid="imp-tpl-apply" onClick={() => void (async () => {
            if (tplInFlight.current) return;
            setTplBusy(true);
            try {
              const res = await auth.api.applyImportTemplate(batch.id, tplPick);
              setState(fromMapping(res.mapping));
              setTplNote(res.unmatched.length === 0 ? t("imp.tpl.applied") : t("imp.tpl.applied_unmatched", { n: String(res.unmatched.length), fields: res.unmatched.map((k) => { const d = defs.fields.find((f) => f.key === k); return d ? localName(d, locale) : k; }).join(t("list.separator")) }));
            } catch (e) { if (!auth.handleError(e)) act.setError(importError(tRef.current, e)); }
            finally { setTplBusy(false); }
          })()}>{t("imp.tpl.apply")}</button>
          <button type="button" className="danger" disabled={!tplPick} data-testid="imp-tpl-retire" onClick={() => setTplRetire(true)}>{t("imp.tpl.retire")}</button>
        </div>
        {tplNote && <p role="status" data-testid="imp-tpl-note">{tplNote}</p>}
        <div className="form-row">
          <Field id="imp-tpl-name" label={t("imp.tpl.save_label")} hint={batch.mapping ? t("imp.tpl.save_hint") : t("imp.tpl.save_first")}>
            {(a) => <input {...a} value={tplName} maxLength={100} onChange={(e) => setTplName(e.target.value)} />}
          </Field>
          <button type="button" disabled={!batch.mapping || tplName.trim() === ""} data-testid="imp-tpl-save" onClick={() => void (async () => {
            if (tplInFlight.current) return;
            setTplBusy(true);
            try {
              const name = tplName.trim().normalize("NFC");   // the server stores NFC names; look the version up under the same form
              const current = templates.find((x) => x.name === name)?.version ?? 0;
              await auth.api.saveImportTemplate(batch.id, name, current);
              setTplNote(t("imp.tpl.saved")); setTplName(""); await loadTemplates();
            } catch (e) { if (!auth.handleError(e)) { act.setError(importError(tRef.current, e)); await loadTemplates(); } }
            finally { setTplBusy(false); }
          })()}>{t("imp.tpl.save")}</button>
        </div>
      </fieldset>
      {tplRetire && (
        <ConfirmDialog title={t("imp.tpl.retire_title")} message={t("imp.tpl.retire_message", { name: tplPick })} confirmLabel={t("imp.tpl.retire")}
          onCancel={() => setTplRetire(false)} onConfirm={() => void (async () => {
            setTplRetire(false);
            if (tplInFlight.current) { act.setError(t("imp.tpl.busy")); return; }
            const version = templates.find((x) => x.name === tplPick && !x.isRetired)?.version;   // the version this screen read: the server refuses a stale one
            if (version === undefined) { act.setError(t("imp.tpl.retire_missing")); setTplPick(""); await loadTemplates(); return; }
            setTplBusy(true);
            try {
              await auth.api.retireImportTemplate(batch.adapter, tplPick, version);
              setTplNote(t("imp.tpl.retired")); setTplPick(""); await loadTemplates();
            } catch (e) { if (!auth.handleError(e)) { act.setError(importError(tRef.current, e)); await loadTemplates(); } }
            finally { setTplBusy(false); }
          })()} />
      )}
      <div className="actions">
        <button type="button" disabled={act.busy || tplBusy} onClick={() => void suggest()}>{t("imp.map_suggest")}</button>
        <button type="button" className="primary" disabled={act.busy || tplBusy} data-testid="imp-map-save" onClick={() => void act.run(() => auth.api.setImportMapping(batch.id, toMapping(defs.fields, state), batch.revision), t("imp.mapped"))}>{t("imp.map_save")}</button>
      </div>
    </Card>
  );
}

// ============================================================================================================================================================== plan and rows

function PlanCard({ batch }: { batch: ImportBatch }) {
  const { t } = useI18n();
  const plan = batch.plan!;
  const s = plan.summary;
  const items: [string, number][] = [["total", s.total], ["valid", s.valid], ["exact", s.exact], ["invalid", s.invalid], ["conflict", s.conflict], ["resolved", s.resolved], ["operations", s.operations], ["blocking", s.blocking]];
  return (
    <Card title={t("imp.validate_title")}>
      <dl className="facts" data-testid="imp-summary">
        {items.map(([k, v]) => <div key={k} className="fact"><dt>{t(`imp.summary.${k}`)}</dt><dd data-testid={`imp-summary-${k}`}>{v}</dd></div>)}
      </dl>
      {plan.reconciliation.length > 0 && (
        <>
          <h3>{t("imp.recon_title")}</h3>
          <dl className="facts" data-testid="imp-recon">
            {plan.reconciliation.map((r) => { const key = `imp.recon.${r.key}`; return <div key={r.key} className="fact"><dt>{t(key) === key ? r.key : t(key)}</dt><dd><bdi dir="ltr">{r.value}</bdi></dd></div>; })}
          </dl>
        </>
      )}
      <Banner kind={plan.canApply ? "success" : "warning"} testId="imp-can-apply">{plan.canApply ? t("imp.can_apply") : t("imp.cannot_apply")}</Banner>
    </Card>
  );
}

const FILTERS: [string, number][] = [["all", 0], ["invalid", ImportRowStatus.invalid], ["conflict", ImportRowStatus.conflict], ["resolved", ImportRowStatus.resolved], ["exact", ImportRowStatus.exact], ["valid", ImportRowStatus.valid]];

function RowsCard({ batch, act, canDecide }: { batch: ImportBatch; act: Act; canDecide: boolean }) {
  const { t } = useI18n();
  const auth = useAuth();
  const tRef = useRef(t);
  tRef.current = t;
  const [status, setStatus] = useState(0);
  const [offset, setOffset] = useState(0);
  const [page, setPage] = useState<ImportRowsPage | null>(null);
  const [decisions, setDecisions] = useState<Record<number, string>>({});
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    void (async () => {
      try { const p = await auth.api.importRows(batch.id, { offset, limit: PAGE, status: status || undefined }); if (alive) { setPage(p); setError(null); } }
      catch (e) { if (!auth.handleError(e) && alive) setError(importError(tRef.current, e)); }
    })();
    return () => { alive = false; };
  }, [batch.id, batch.revision, status, offset]); // eslint-disable-line react-hooks/exhaustive-deps

  const pending = useMemo(() => Object.entries(decisions), [decisions]);
  const total = page?.total ?? 0;
  return (
    <Card title={t("imp.rows_title")}>
      {error && <Banner kind="error">{error}</Banner>}
      <Field id="imp-rows-filter" label={t("imp.filter")}>
        {(a) => (
          <select {...a} value={status} onChange={(e) => { setStatus(Number(e.target.value)); setOffset(0); }}>
            {FILTERS.map(([k, v]) => <option key={k} value={v}>{t(`imp.filter.${k}`)}</option>)}
          </select>
        )}
      </Field>
      {!page ? <Loading /> : page.rows.length === 0 ? <p data-testid="imp-no-rows">{t("imp.no_rows")}</p> : (
        <table className="table" data-testid="imp-rows">
          <thead><tr><th>{t("imp.row_no")}</th><th>{t("imp.row_status")}</th><th>{t("imp.row_values")}</th><th>{t("imp.row_issues")}</th><th>{t("imp.row_resolution")}</th></tr></thead>
          <tbody>
            {page.rows.map((r) => {
              const allowed = canDecide && (r.status === ImportRowStatus.conflict || r.status === ImportRowStatus.resolved) ? (r.allowedResolutions ?? []) : [];
              const chosen = decisions[r.rowNo] ?? r.resolution ?? "";
              return (
                <tr key={r.rowNo} data-testid={`imp-r-${r.rowNo}`}>
                  <td><bdi dir="ltr">{r.rowNo}</bdi></td>
                  <td>{t(`imp.rowstatus.${r.status}`)}</td>
                  <td><bdi dir="auto">{r.cells.slice(0, 6).map((c) => c ?? "").join(" | ")}</bdi></td>
                  <td>
                    {(r.issues ?? []).map((i, n) => { const key = `imp.issue.${i.code}`; return <div key={n}>{t(key) === key ? t("imp.issues_generic", { code: i.code }) : t(key, { detail: i.detail ?? "" })}</div>; })}
                    {r.explanation && <div className="hint"><bdi dir="auto">{r.explanation}</bdi></div>}
                  </td>
                  <td>
                    {allowed.length > 0 ? (
                      <select aria-label={`${t("imp.row_resolution")} ${r.rowNo}`} value={chosen} onChange={(e) => setDecisions({ ...decisions, [r.rowNo]: e.target.value })}>
                        <option value="">{t("imp.resolution.none")}</option>
                        {allowed.map((a) => <option key={a} value={a}>{t(`imp.resolution.${a}`)}</option>)}
                      </select>
                    ) : r.resolution ? t(`imp.resolution.${r.resolution}`) : ""}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      <div className="actions">
        <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>{t("imp.prev")}</button>
        <span data-testid="imp-page">{total === 0 ? "0" : t("imp.page", { from: String(offset + 1), to: String(Math.min(total, offset + PAGE)), total: String(total) })}</span>
        <button type="button" disabled={offset + PAGE >= total} onClick={() => setOffset(offset + PAGE)}>{t("imp.next")}</button>
        {canDecide && (
          <button type="button" className="primary" data-testid="imp-decide" disabled={act.busy || pending.length === 0}
            onClick={() => void act.run(async () => { const r = await auth.api.setImportResolutions(batch.id, pending.map(([rowNo, action]) => ({ rowNo: Number(rowNo), action: action === "" ? null : action })), batch.revision); setDecisions({}); return r; }, t("imp.decisions_saved"))}>{t("imp.apply_decisions")}</button>
        )}
      </div>
    </Card>
  );
}

// ============================================================================================================================================================== approval

function ApprovalCard({ batch, act, canSubmit }: { batch: ImportBatch; act: Act; canSubmit: boolean }) {
  const { t } = useI18n();
  const auth = useAuth();
  const [comment, setComment] = useState("");
  const [decisions, setDecisions] = useState<ImportDecision[]>([]);
  const [commentError, setCommentError] = useState(false);
  const status = batch.status;
  const me = auth.user?.id;
  const independent = me !== undefined && batch.createdBy !== me && batch.submittedBy !== me;
  const canApprove = auth.can("company.imports.approve");
  const show = ["validated", "submitted", "approved", "failed", "applied", "rejected"].includes(status);

  useEffect(() => {
    if (!show || status === "validated") { setDecisions([]); return; }
    let alive = true;
    void auth.api.importDecisions(batch.id).then((d) => { if (alive) setDecisions(d.decisions); }).catch(() => undefined);
    return () => { alive = false; };
  }, [batch.id, batch.revision, status]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!show) return null;
  const reject = () => {
    if (comment.trim() === "") { setCommentError(true); return; }
    setCommentError(false);
    void act.run(() => auth.api.rejectImport(batch.id, comment.trim(), batch.revision), t("imp.rejected"));
  };
  return (
    <Card title={t("imp.approval_title")} hint={t("imp.approval_hint")}>
      <div className="actions">
        {status === "validated" && canSubmit && batch.plan?.canApply && (
          <button type="button" className="primary" data-testid="imp-submit" disabled={act.busy} onClick={() => void act.run(() => auth.api.submitImport(batch.id, batch.revision), t("imp.submitted"))}>{t("imp.submit")}</button>
        )}
        {["submitted", "approved", "failed"].includes(status) && canSubmit && (
          <button type="button" data-testid="imp-withdraw" disabled={act.busy} onClick={() => void act.run(() => auth.api.withdrawImport(batch.id, batch.revision), t("imp.withdrawn"))}>{t("imp.withdraw")}</button>
        )}
      </div>
      {status === "submitted" && canApprove && (
        independent ? (
          <>
            <Field id="imp-comment" label={t("imp.comment")} error={commentError ? t("imp.comment_required_reject") : undefined}>
              {(a) => <textarea {...a} rows={2} maxLength={500} value={comment} onChange={(e) => setComment(e.target.value)} />}
            </Field>
            <div className="actions">
              <button type="button" className="primary" data-testid="imp-approve" disabled={act.busy} onClick={() => void act.run(() => auth.api.approveImport(batch.id, comment.trim() || null, batch.revision), t("imp.approved"))}>{t("imp.approve")}</button>
              <button type="button" className="danger" data-testid="imp-reject" disabled={act.busy} onClick={reject}>{t("imp.reject")}</button>
            </div>
          </>
        ) : <Banner kind="info" testId="imp-sod">{t("imp.sod_note")}</Banner>
      )}
      {decisions.length > 0 && (
        <>
          <h3>{t("imp.decisions_title")}</h3>
          <ul data-testid="imp-decisions">
            {decisions.map((d, i) => <li key={i}><bdi dir="ltr">{d.atUtc.slice(0, 16).replace("T", " ")}</bdi> — {d.approved ? t("imp.decision.approved") : t("imp.decision.rejected")}{d.comment ? <>: <bdi dir="auto">{d.comment}</bdi></> : null}</li>)}
          </ul>
        </>
      )}
    </Card>
  );
}

// ============================================================================================================================================================== history and settings

function EventsCard({ id, revision }: { id: string; revision: number }) {
  const { t } = useI18n();
  const auth = useAuth();
  const [events, setEvents] = useState<ImportEvent[]>([]);
  useEffect(() => {
    let alive = true;
    void auth.api.importEvents(id).then((e) => { if (alive) setEvents(e.events); }).catch(() => undefined);
    return () => { alive = false; };
  }, [id, revision]); // eslint-disable-line react-hooks/exhaustive-deps
  if (events.length === 0) return null;
  return (
    <Card title={t("imp.events_title")}>
      <table className="table" data-testid="imp-events">
        <thead><tr><th>{t("imp.event.col.event")}</th><th>{t("imp.event.col.when")}</th></tr></thead>
        <tbody>{events.map((e) => <tr key={e.id}><td><bdi dir="ltr">{e.kind}</bdi></td><td><bdi dir="ltr">{e.atUtc.slice(0, 19).replace("T", " ")}</bdi></td></tr>)}</tbody>
      </table>
    </Card>
  );
}

function SettingsCard() {
  const { t } = useI18n();
  const auth = useAuth();
  const tRef = useRef(t);
  tRef.current = t;
  const [settings, setSettings] = useState<ImportSettings | null>(null);
  const [retention, setRetention] = useState("30");
  const [pending, setPending] = useState("30");
  const canView = auth.can("company.imports.view");
  const canConfigure = auth.can("company.imports.configure");
  const load = useCallback(async () => {
    const s = await auth.api.getImportSettings();
    setSettings(s.settings); setRetention(String(s.settings.retentionDays)); setPending(String(s.settings.pendingExpiryDays));
  }, [auth.api]);
  const act = useAction(load);
  useEffect(() => { if (canView) void load().catch((e) => { if (!auth.handleError(e)) act.setError(importError(tRef.current, e)); }); }, [canView]); // eslint-disable-line react-hooks/exhaustive-deps
  if (!canView || !settings) return null;
  const valid = /^\d{1,4}$/.test(retention) && Number(retention) >= 1 && /^\d{1,4}$/.test(pending) && Number(pending) >= 1;
  return (
    <Card title={t("imp.settings_title")}>
      {act.error && <Banner kind="error" testId="imp-settings-error">{act.error}</Banner>}
      {act.notice && <Banner kind="success">{act.notice}</Banner>}
      <Field id="imp-retention" label={t("imp.settings_retention")}>
        {(a) => <input {...a} inputMode="numeric" value={retention} disabled={!canConfigure} onChange={(e) => setRetention(e.target.value)} />}
      </Field>
      <Field id="imp-pending" label={t("imp.settings_pending")}>
        {(a) => <input {...a} inputMode="numeric" value={pending} disabled={!canConfigure} onChange={(e) => setPending(e.target.value)} />}
      </Field>
      {canConfigure && (
        <div className="actions">
          <button type="button" className="primary" disabled={act.busy || !valid} data-testid="imp-settings-save" onClick={() => void act.run(() => auth.api.putImportSettings(Number(retention), Number(pending), settings.revision), t("imp.settings_saved"))}>{t("imp.settings_save")}</button>
          <button type="button" disabled={act.busy} data-testid="imp-maintain" onClick={() => void act.run(async () => { const r = await auth.api.runImportMaintenance(); act.setNotice(t("imp.maintenance_done", { expired: String(r.expired), purged: String(r.purged) })); return r; }, t("imp.maintenance_done", { expired: "0", purged: "0" }))}>{t("imp.run_maintenance")}</button>
        </div>
      )}
    </Card>
  );
}
