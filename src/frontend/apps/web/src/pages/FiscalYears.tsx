import { ApiError, type FiscalPeriod, type FiscalReopenRequest, type FiscalYear, type FiscalYearsResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, fieldErrors, Loading, PageHeader } from "../components";
import { useReportUnsaved, useUnsaved } from "../unsaved";

/**
 * Fiscal calendar (Phase 3.1D). ONE implementation for the Web app and the Windows Desktop app. Every rule (the state machine, contiguity, reasons,
 * segregation of duties, close checks, audit, concurrency, RBAC) is enforced by the server and PostgreSQL; this screen validates for convenience only
 * and shows or hides controls from what the server reports the caller may do. Closing records a STATE: no journal entry, balance carry-forward or
 * posting exists yet (they need the general ledger), and the screen says so while the server reports the close checks as not implemented.
 */

const pad = (n: number) => String(n).padStart(2, "0");
const addDay = (iso: string) => { const d = new Date(`${iso}T00:00:00Z`); d.setUTCDate(d.getUTCDate() + 1); return d.toISOString().slice(0, 10); };

type Dialog =
  | { kind: "soft"; period: FiscalPeriod }
  | { kind: "reopen"; period: FiscalPeriod }
  | { kind: "final"; period: FiscalPeriod }
  | { kind: "year"; year: FiscalYear }
  | { kind: "request-period"; period: FiscalPeriod }
  | { kind: "request-year"; year: FiscalYear }
  | { kind: "approve"; request: FiscalReopenRequest }
  | { kind: "cancel"; request: FiscalReopenRequest }
  | { kind: "deny"; request: FiscalReopenRequest };

export function FiscalYearsPage() {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [data, setData] = useState<FiscalYearsResponse | null>(null);
  const [requests, setRequests] = useState<FiscalReopenRequest[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const years = await auth.api.listFiscalYears();
      setRequests(years.can.viewRequests ? (await auth.api.listFiscalReopenRequests()).requests : []);
      setData(years);
      setLoadError(null);
    } catch (e) {
      if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "fiscal.error."));
    }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!data) return <Loading />;

  const can = data.can;
  const anyAction = can.create || can.closeYear || can.softClose || can.reopen || can.finalClose || can.requestReopen || can.approveReopen;

  async function run(action: () => Promise<unknown>, message = t("fiscal.changed")) {
    setBusy(true);
    setError(null);
    setConflict(false);
    setNotice(null);
    try {
      await action();
      await load();
      setNotice(message);
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else setError(errorText(t, err, "fiscal.error."));
    } finally {
      setBusy(false);
    }
  }

  const apply = (d: Dialog, reason?: string) => {
    setDialog(null);
    switch (d.kind) {
      case "soft": return void run(() => auth.api.softCloseFiscalPeriod(d.period.id, d.period.revision));
      case "reopen": return void run(() => auth.api.reopenFiscalPeriod(d.period.id, d.period.revision));
      case "final": return void run(() => auth.api.finalCloseFiscalPeriod(d.period.id, d.period.revision));
      case "year": return void run(() => auth.api.closeFiscalYear(d.year.id, d.year.revision));
      case "request-period": return void run(() => auth.api.requestFiscalPeriodReopen(d.period.id, d.period.revision, reason ?? ""), t("fiscal.request_sent"));
      case "request-year": return void run(() => auth.api.requestFiscalYearReopen(d.year.id, d.year.revision, reason ?? ""), t("fiscal.request_sent"));
      case "approve": return void run(() => auth.api.approveFiscalReopen(d.request.id, d.request.revision));
      case "deny": return void run(() => auth.api.denyFiscalReopen(d.request.id, d.request.revision, reason || undefined));
      case "cancel": return void run(() => auth.api.cancelFiscalReopen(d.request.id, d.request.revision));
    }
  };

  return (
    <section aria-labelledby="page-title">
      <PageHeader
        title={t("fiscal.title")}
        description={t("fiscal.subtitle")}
        actions={can.create && <button type="button" className="primary" disabled={busy || creating || data.today === null} onClick={() => guard(() => { setNotice(null); setError(null); setCreating(true); })}>{t("fiscal.new")}</button>}
      />
      {!anyAction && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {data.today === null && <Banner kind="warning" testId="no-time-zone">{t("fiscal.no_time_zone")}</Banner>}
      {!data.closeChecksImplemented && <Banner kind="info" testId="checks-not-available">{t("fiscal.checks_not_available")}</Banner>}
      {conflict && (
        <Banner kind="error" testId="conflict">
          {t("fiscal.error.revision_conflict")} <button type="button" onClick={() => { setConflict(false); void load(); }}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error">{error}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}

      {creating && (
        <YearForm
          years={data.years}
          today={data.today}
          onClose={() => guard(() => setCreating(false))}
          onSaved={() => { setCreating(false); setNotice(t("fiscal.created")); void load(); }}
        />
      )}

      {data.years.length === 0 && <Card title={t("fiscal.title")}><p>{t("fiscal.none")}</p></Card>}
      {data.years.map((y) => (
        <Card key={y.id} title={y.name}>
          <div data-testid={`year-${y.name}`}>
            <p>
              <bdi dir="ltr">{t("fiscal.year_range", { from: y.startDate, to: y.endDate })}</bdi>{" "}
              <span className={`chip ${y.closed ? "chip-off" : "chip-ok"}`}>{y.closed ? t("fiscal.year_closed") : t("fiscal.year_open")}</span>{" "}
              <span className="chip chip-base">{y.periodType === "monthly" ? t("fiscal.period_monthly") : t("fiscal.period_quarterly")}</span>{" "}
              <span className="hint"><bdi dir="ltr">{t("fiscal.time_zone", { zone: y.timeZone })}</bdi></span>
            </p>
            <div className="actions">
              {can.closeYear && !y.closed && <button type="button" disabled={busy} onClick={() => setDialog({ kind: "year", year: y })}>{t("fiscal.close_year")}</button>}
              {can.requestReopen && y.closed && <button type="button" disabled={busy} onClick={() => setDialog({ kind: "request-year", year: y })}>{t("fiscal.request_reopen")}</button>}
            </div>
            <div className="table-wrap">
              <table className="data-table">
                <caption className="visually-hidden">{t("fiscal.periods_of", { name: y.name })}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t("fiscal.col_number")}</th><th scope="col">{t("fiscal.col_range")}</th>
                    <th scope="col">{t("fiscal.col_status")}</th><th scope="col">{t("fiscal.col_actions")}</th>
                  </tr>
                </thead>
                <tbody>
                  {y.periods.map((p) => (
                    <tr key={p.id} data-testid={`period-${y.name}-${p.number}`}>
                      <td><bdi dir="ltr">{p.number}</bdi></td>
                      <td><bdi dir="ltr">{p.startDate} → {p.endDate}</bdi></td>
                      <td><span className={`chip ${p.status === "open" ? "chip-ok" : p.status === "soft_closed" ? "chip-base" : "chip-off"}`}>{t(`fiscal.status.${p.status}`)}</span></td>
                      <td>
                        {/* A closed year freezes its periods: no period action is offered until the year is reopened. */}
                        {!y.closed && p.status === "open" && can.softClose && <button type="button" disabled={busy} onClick={() => setDialog({ kind: "soft", period: p })}>{t("fiscal.soft_close")}</button>}
                        {!y.closed && p.status === "soft_closed" && can.reopen && <button type="button" disabled={busy} onClick={() => setDialog({ kind: "reopen", period: p })}>{t("fiscal.reopen")}</button>}{" "}
                        {!y.closed && p.status === "soft_closed" && can.finalClose && <button type="button" disabled={busy} onClick={() => setDialog({ kind: "final", period: p })}>{t("fiscal.final_close")}</button>}
                        {!y.closed && p.status === "final_closed" && can.requestReopen && <button type="button" disabled={busy} onClick={() => setDialog({ kind: "request-period", period: p })}>{t("fiscal.request_reopen")}</button>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </Card>
      ))}

      {requests.length > 0 || can.viewRequests ? (
        <RequestsCard requests={requests} busy={busy} onAction={setDialog} />
      ) : null}

      {dialog && (dialog.kind === "request-period" || dialog.kind === "request-year") && (
        <ReasonDialog
          title={dialog.kind === "request-period" ? t("fiscal.request_period_title") : t("fiscal.request_year_title")}
          message={dialog.kind === "request-period" ? t("fiscal.request_period_message", { n: String(dialog.period.number) }) : t("fiscal.request_year_message", { name: dialog.year.name })}
          label={t("fiscal.request_reason_label")}
          confirmLabel={t("fiscal.request_reopen")}
          minLength={10}
          onCancel={() => setDialog(null)}
          onConfirm={(reason) => apply(dialog, reason)}
        />
      )}
      {dialog?.kind === "deny" && (
        <ReasonDialog
          title={t("fiscal.deny_title")}
          message={t("fiscal.deny_message")}
          label={t("fiscal.deny_note_label")}
          confirmLabel={t("fiscal.deny")}
          minLength={0}
          onCancel={() => setDialog(null)}
          onConfirm={(note) => apply(dialog, note)}
        />
      )}
      {dialog && dialog.kind !== "request-period" && dialog.kind !== "request-year" && dialog.kind !== "deny" && (
        <ConfirmDialog
          title={dialog.kind === "soft" ? t("fiscal.soft_close_title") : dialog.kind === "reopen" ? t("fiscal.reopen_title") : dialog.kind === "final" ? t("fiscal.final_close_title")
            : dialog.kind === "year" ? t("fiscal.close_year_title") : dialog.kind === "approve" ? t("fiscal.approve_title") : t("fiscal.cancel_title")}
          message={dialog.kind === "approve" ? t("fiscal.approve_message") : dialog.kind === "cancel" ? t("fiscal.cancel_message") : dialog.kind === "year" ? t("fiscal.close_year_message", { name: dialog.year.name })
            : t(dialog.kind === "soft" ? "fiscal.soft_close_message" : dialog.kind === "reopen" ? "fiscal.reopen_message" : "fiscal.final_close_message", { n: String(dialog.period.number) })}
          confirmLabel={t("form.confirm")}
          onCancel={() => setDialog(null)}
          onConfirm={() => apply(dialog)}
        />
      )}
    </section>
  );
}

/** A reason dialog: a request needs at least 10 characters (recorded by the server with the request); a denial note is optional. */
function ReasonDialog({ title, message, label, confirmLabel, minLength, onCancel, onConfirm }: { title: string; message: string; label: string; confirmLabel: string; minLength: number; onCancel: () => void; onConfirm: (reason: string) => void }) {
  const { t } = useI18n();
  const cancel = useRef<HTMLButtonElement>(null);
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | undefined>();
  useEffect(() => cancel.current?.focus(), []);
  return (
    <div className="dialog-backdrop">
      <div role="dialog" aria-modal="true" aria-labelledby="confirm-title" aria-describedby="confirm-message" className="dialog" onKeyDown={(e) => { if (e.key === "Escape") onCancel(); }}>
        <h2 id="confirm-title">{title}</h2>
        <p id="confirm-message">{message}</p>
        <Field id="f-reason" label={label} error={error} required={minLength > 0}>
          {(aria) => <textarea {...aria} rows={3} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />}
        </Field>
        <div className="actions">
          <button type="button" ref={cancel} onClick={onCancel}>{t("cancel")}</button>
          <button type="button" className="danger" onClick={() => { if (reason.trim().length < minLength) setError(t("fiscal.reason_required")); else onConfirm(reason.trim()); }}>{confirmLabel}</button>
        </div>
      </div>
    </div>
  );
}

function YearForm({ years, today, onClose, onSaved }: { years: FiscalYear[]; today: string | null; onClose: () => void; onSaved: () => void }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  // The first year may start in any month; every later year starts the day after the latest one ends (the server enforces contiguity).
  const next = years.length > 0 ? addDay(years.reduce((m, y) => (y.endDate > m ? y.endDate : m), years[0].endDate)) : null;
  const baseYear = next ? Number(next.slice(0, 4)) : Number((today ?? new Date().toISOString()).slice(0, 4));
  const baseMonth = next ? Number(next.slice(5, 7)) : 1;
  const suggest = (y: number, m: number) => (m === 1 ? `FY ${y}` : `FY ${y}/${y + 1}`);
  const [name, setName] = useState(suggest(baseYear, baseMonth));
  const [month, setMonth] = useState(baseMonth);
  const [year, setYear] = useState(String(baseYear));
  const [periodType, setPeriodType] = useState<"monthly" | "quarterly">("monthly");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const dirty = name !== suggest(baseYear, baseMonth) || periodType !== "monthly" || (!next && (month !== baseMonth || year !== String(baseYear)));
  useReportUnsaved("fiscal-form", dirty, t("fiscal.form_section"));
  const monthName = (m: number) => new Intl.DateTimeFormat(locale === "en" ? "en" : "ar", { month: "long" }).format(new Date(2026, m - 1, 1));

  async function submit(e: FormEvent) {
    e.preventDefault();
    const v: Record<string, string> = {};
    if (!name.trim()) v.name = t("field.required");
    if (!/^\d{4}$/.test(year.trim()) || Number(year) < 1900 || Number(year) > 2200) v.startYear = t("field.invalid_format");
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); return; }
    setBusy(true);
    setError(null);
    try {
      await auth.api.createFiscalYear({ name: name.trim(), startDate: `${year.trim()}-${pad(month)}-01`, periodType });
      onSaved();
    } catch (err) {
      if (auth.handleError(err)) return;
      // A failed save changes nothing: the form keeps what the user typed.
      if (err instanceof ApiError && err.status === 400) { setErrors(fieldErrors(t, err)); setError(t("form.fix_errors")); }
      else if (err instanceof ApiError && err.status === 409 && err.code === "fiscal_year_name_taken") { setErrors({ name: t("fiscal.error.fiscal_year_name_taken") }); setError(t("form.fix_errors")); }
      else setError(errorText(t, err, "fiscal.error."));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card title={t("fiscal.form_title")}>
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={submit} noValidate className="form-grid">
        <div className="fields">
          <Field id="fy-name" label={t("fiscal.name")} error={errors.name} required>
            {(aria) => <input {...aria} name="name" value={name} onChange={(e) => setName(e.target.value)} maxLength={100} autoComplete="off" />}
          </Field>
          <Field id="fy-month" label={t("fiscal.start_month")} error={errors.startDate} hint={next ? t("fiscal.start_fixed") : t("fiscal.start_hint")}>
            {(aria) => (
              <select {...aria} name="month" value={month} onChange={(e) => setMonth(Number(e.target.value))} disabled={next !== null}>
                {Array.from({ length: 12 }, (_, i) => <option key={i + 1} value={i + 1}>{monthName(i + 1)}</option>)}
              </select>
            )}
          </Field>
          <Field id="fy-year" label={t("fiscal.start_year")} error={errors.startYear} required>
            {(aria) => <input {...aria} name="startYear" value={year} onChange={(e) => setYear(e.target.value)} readOnly={next !== null} dir="ltr" inputMode="numeric" maxLength={4} autoComplete="off" />}
          </Field>
          <Field id="fy-type" label={t("fiscal.period_type")} error={errors.periodType}>
            {(aria) => (
              <select {...aria} name="periodType" value={periodType} onChange={(e) => setPeriodType(e.target.value as typeof periodType)}>
                <option value="monthly">{t("fiscal.period_monthly")}</option>
                <option value="quarterly">{t("fiscal.period_quarterly")}</option>
              </select>
            )}
          </Field>
        </div>
        <div className="actions">
          <button type="button" onClick={onClose}>{t("fiscal.close_form")}</button>
          <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("fiscal.save")}</button>
        </div>
      </form>
    </Card>
  );
}

/** Pending and decided reopening requests. What each viewer may do comes from the server (canApprove / canCancel); approving one's own request is never offered. */
function RequestsCard({ requests, busy, onAction }: { requests: FiscalReopenRequest[]; busy: boolean; onAction: (d: Dialog) => void }) {
  const { t, locale } = useI18n();
  const when = (iso: string) => new Intl.DateTimeFormat(locale === "en" ? "en" : "ar", { dateStyle: "medium", timeStyle: "short" }).format(new Date(iso));
  return (
    <Card title={t("fiscal.requests_title")} hint={t("fiscal.requests_hint")}>
      <div className="table-wrap" data-testid="reopen-requests">
        <table className="data-table">
          <caption className="visually-hidden">{t("fiscal.requests_title")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("fiscal.requests_col_target")}</th><th scope="col">{t("fiscal.requests_col_reason")}</th><th scope="col">{t("fiscal.requests_col_status")}</th>
              <th scope="col">{t("fiscal.requests_col_date")}</th><th scope="col">{t("fiscal.requests_col_actions")}</th>
            </tr>
          </thead>
          <tbody>
            {requests.length === 0 && <tr><td colSpan={5}>{t("fiscal.requests_none")}</td></tr>}
            {requests.map((r) => (
              <tr key={r.id} data-testid={`request-${r.id}`}>
                <td><bdi>{r.target === "period" ? t("fiscal.target_period", { year: r.yearName, n: String(r.periodNumber ?? "") }) : t("fiscal.target_year", { year: r.yearName })}</bdi>{r.mine && <> <span className="chip chip-base">{t("fiscal.mine")}</span></>}</td>
                <td><bdi>{r.reason}</bdi>{r.decisionNote && <p className="hint"><bdi>{r.decisionNote}</bdi></p>}</td>
                <td><span className={`chip ${r.status === "pending" ? "chip-base" : r.status === "approved" ? "chip-ok" : "chip-off"}`}>{t(`fiscal.request_status.${r.status}`)}</span></td>
                <td><bdi dir="ltr">{when(r.requestedAtUtc)}</bdi></td>
                <td>
                  {r.canApprove && <button type="button" disabled={busy} onClick={() => onAction({ kind: "approve", request: r })}>{t("fiscal.approve")}</button>}{" "}
                  {r.canApprove && <button type="button" disabled={busy} onClick={() => onAction({ kind: "deny", request: r })}>{t("fiscal.deny")}</button>}
                  {r.canCancel && <button type="button" disabled={busy} onClick={() => onAction({ kind: "cancel", request: r })}>{t("fiscal.cancel_request")}</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}
