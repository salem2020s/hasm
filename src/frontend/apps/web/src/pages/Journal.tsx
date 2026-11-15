import type { Account, AllocatableCostCenter, Branch, JournalEntry, JournalHeader, JournalInput, JournalLine, JournalListResponse, JournalStatus } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { addDecimal, isDecimal, localName, toLatinDigits, useRunner, OPEN_JOURNAL_KEY } from "../ledgerCommon";
import { useReportUnsaved, useUnsaved } from "../unsaved";

/**
 * Journal entries (Phase 3.2B S5). The whole workflow lives on the server and in PostgreSQL: balance in base currency, approvals for exactly this revision, separation of duties,
 * fiscal periods, rates, accounts, atomic numbering and immutability. This screen only edits drafts and calls the workflow; it never decides whether an entry may post, and it shows
 * or hides controls from what the server reports the caller may do. Amounts stay exact decimal text.
 */

const STATUSES: JournalStatus[] = ["draft", "pending_approval", "approved", "posted", "reversed", "cancelled"];

interface AllocDraft { costCenterId: string; amount: string }
interface LineDraft { allocMode: "amount" | "percent"; key: string; accountId: string; description: string; currency: string; debit: string; credit: string; branchId: string; rateOverride: string; rateReason: string; allocations: AllocDraft[] }
interface Lookups { accounts: Account[]; currencies: string[]; baseCurrency: string | null; branches: Branch[]; costCenters: AllocatableCostCenter[]; accountsDenied: boolean }

let keySeed = 0;
const newKey = () => `l${++keySeed}`;
const blankLine = (currency: string, branchId = ""): LineDraft => ({ allocMode: "amount", key: newKey(), accountId: "", description: "", currency, debit: "", credit: "", branchId, rateOverride: "", rateReason: "", allocations: [] });
const fromLine = (l: JournalLine): LineDraft => ({
  allocMode: "amount", key: newKey(), accountId: l.accountId, description: l.description ?? "", currency: l.currency, debit: Number(l.debit) > 0 ? l.debit : "", credit: Number(l.credit) > 0 ? l.credit : "",
  branchId: l.branchId, rateOverride: l.rateId === null && l.currency && l.rateOverrideReason ? l.rate : "", rateReason: l.rateOverrideReason ?? "",
  allocations: l.allocations.map((a) => ({ costCenterId: a.costCenterId, amount: a.amount })),
});

function toInput(date: string, description: string, branchId: string, lines: LineDraft[], base: string | null, adjustsId?: string | null): JournalInput {
  return {
    entryDate: date, description: description.trim() || null, branchId, adjustsId: adjustsId ?? null,
    lines: lines.map((l) => ({
      accountId: l.accountId, description: l.description.trim() || null, currency: l.currency && l.currency !== base ? l.currency : null,
      debit: l.debit.trim() ? toLatinDigits(l.debit.trim()) : null, credit: l.credit.trim() ? toLatinDigits(l.credit.trim()) : null,
      branchId: l.branchId && l.branchId !== branchId ? l.branchId : null,
      rateOverride: l.rateOverride.trim() ? toLatinDigits(l.rateOverride.trim()) : null, rateOverrideReason: l.rateOverride.trim() ? l.rateReason.trim() : null,
      allocations: l.allocations.filter((a) => a.costCenterId).map((a) => (l.allocMode === "percent"
        ? { costCenterId: a.costCenterId, percent: toLatinDigits(a.amount.trim()) }
        : { costCenterId: a.costCenterId, amount: toLatinDigits(a.amount.trim()) })),
    })),
  };
}

function lineProblem(l: LineDraft): string | null {
  const deb = toLatinDigits(l.debit.trim()), cre = toLatinDigits(l.credit.trim());
  if (!l.accountId) return "jrn.problem_account";
  if ((deb !== "") === (cre !== "")) return "jrn.problem_side";
  if (!isDecimal(deb || cre, 4) || Number(deb || cre) <= 0) return "jrn.problem_amount";
  return null;
}

/** Sums of the typed amounts per currency, exact (scaled integers). */
function totals(lines: LineDraft[], base: string | null) {
  const out = new Map<string, { debit: string; credit: string }>();
  for (const l of lines) {
    const c = l.currency || base || "";
    const cur = out.get(c) ?? { debit: "0.0000", credit: "0.0000" };
    if (isDecimal(toLatinDigits(l.debit.trim()), 4)) cur.debit = addDecimal(cur.debit, toLatinDigits(l.debit.trim()));
    if (isDecimal(toLatinDigits(l.credit.trim()), 4)) cur.credit = addDecimal(cur.credit, toLatinDigits(l.credit.trim()));
    out.set(c, cur);
  }
  return out;
}

function Prompt({ title, label, confirm, required, kind, onConfirm, onCancel }: { title: string; label: string; confirm: string; required: boolean; kind: "text" | "date"; onConfirm: (v: string) => void; onCancel: () => void }) {
  const { t } = useI18n();
  const [v, setV] = useState(kind === "date" ? new Date().toISOString().slice(0, 10) : "");
  const ok = !required || v.trim() !== "";
  return (
    <div className="dialog-backdrop">
      <div role="dialog" aria-modal="true" aria-labelledby="prompt-title" className="dialog" onKeyDown={(e) => { if (e.key === "Escape") onCancel(); }}>
        <h2 id="prompt-title">{title}</h2>
        <Field id="prompt-value" label={label} required={required}>
          {(a) => kind === "date" ? <input {...a} type="date" value={v} onChange={(e) => setV(e.target.value)} autoFocus /> : <textarea {...a} rows={3} maxLength={500} value={v} onChange={(e) => setV(e.target.value)} autoFocus />}
        </Field>
        <div className="actions">
          <button type="button" onClick={onCancel}>{t("cancel")}</button>
          <button type="button" className="primary" disabled={!ok} onClick={() => onConfirm(v.trim())}>{confirm}</button>
        </div>
      </div>
    </div>
  );
}

function EntryScreen({ id, adjustsId, lookups, can, onBack, onOpen }: { id: string | null; adjustsId: string | null; lookups: Lookups; can: JournalListResponse["can"]; onBack: () => void; onOpen: (id: string) => void }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const tRef = useRef(t);
  tRef.current = t;
  const base = lookups.baseCurrency;
  const [entry, setEntry] = useState<JournalEntry | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const defaultBranch = lookups.branches.find((b) => b.isMain)?.id ?? lookups.branches[0]?.id ?? "";
  const [date, setDate] = useState(new Date().toISOString().slice(0, 10));
  const [description, setDescription] = useState("");
  const [branchId, setBranchId] = useState(defaultBranch);
  const [lines, setLines] = useState<LineDraft[]>([blankLine(base ?? "", defaultBranch), blankLine(base ?? "", defaultBranch)]);
  const [dirty, setDirty] = useState(false);
  const [prompt, setPrompt] = useState<null | "reverse" | "reject" | "approve">(null);
  useReportUnsaved("journal-form", dirty, t("jrn.form"));

  const apply = useCallback((e: JournalEntry) => {
    setEntry(e);
    setDate(e.entry.entryDate); setDescription(e.entry.description ?? ""); setBranchId(e.entry.branchId);
    setLines(e.lines.filter((l) => !l.isRounding).map(fromLine)); setDirty(false);
  }, []);
  const load = useCallback(async () => {
    if (!id) return;
    try { apply(await auth.api.getJournalEntry(id)); setLoadError(null); }
    catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "jrn.error.")); }
  }, [auth.api, auth.handleError, id, apply]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);
  const runner = useRunner("jrn.error.", load);

  const status = entry?.entry.status;
  const editable = !entry ? can.create : status === "draft" && entry.entry.type !== "reversal" && can.create;
  const accountById = useMemo(() => new Map(lookups.accounts.map((a) => [a.id, a])), [lookups.accounts]);
  const problems = lines.map(lineProblem);
  const formOk = !!date && !!branchId && lines.length >= 2 && problems.every((p) => p === null);
  const sums = totals(lines, base);
  const mark = (fn: () => void) => { fn(); setDirty(true); };
  const setLine = (key: string, patch: Partial<LineDraft>) => mark(() => setLines((ls) => ls.map((l) => (l.key === key ? { ...l, ...patch } : l))));

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!formOk) return;
    const input = toInput(date, description, branchId, lines, base, entry ? entry.entry.adjustsId : adjustsId);
    const saved = await runner.run(async () => {
      const result = entry ? await auth.api.updateJournalEntry(entry.entry.id, input, entry.entry.revision) : await auth.api.createJournalEntry(input);
      apply(result);
      return result;
    }, t("jrn.saved"));
    if (saved && !entry) onOpen(saved.entry.id);
  }

  const step = (fn: (e: JournalEntry) => Promise<JournalEntry>, message: string) => entry && runner.run(async () => { const r = await fn(entry); apply(r); return r; }, message);

  if (loadError) return <Banner kind="error" testId="jrn-load-error">{loadError}</Banner>;
  if (id && !entry) return <Loading />;
  const h = entry?.entry;
  const lineView = (l: LineDraft, i: number) => {
    const acc = accountById.get(l.accountId);
    const foreign = l.currency && l.currency !== base;
    return (
      <tr key={l.key} data-testid={`jrn-line-${i}`}>
        <td>
          {editable ? (
            <select aria-label={t("jrn.account")} value={l.accountId} onChange={(e) => setLine(l.key, { accountId: e.target.value })}>
              <option value="">{t("jrn.choose_account")}</option>
              {lookups.accounts.filter((a) => a.postable).map((a) => <option key={a.id} value={a.id}>{a.code} — {localName(a, locale)}</option>)}
            </select>
          ) : <span>{acc ? `${acc.code} — ${localName(acc, locale)}` : l.accountId}</span>}
        </td>
        <td>{editable ? <input aria-label={t("jrn.line_description")} maxLength={500} value={l.description} onChange={(e) => setLine(l.key, { description: e.target.value })} /> : l.description}</td>
        <td>
          {editable && lookups.currencies.length > 1 ? (
            <select aria-label={t("jrn.currency")} value={l.currency || base || ""} onChange={(e) => setLine(l.key, { currency: e.target.value })}>{lookups.currencies.map((c) => <option key={c} value={c}>{c}</option>)}</select>
          ) : <bdi dir="ltr">{l.currency || base}</bdi>}
        </td>
        <td>{editable ? <input aria-label={t("jrn.debit")} inputMode="decimal" dir="ltr" value={l.debit} onChange={(e) => setLine(l.key, { debit: e.target.value, credit: e.target.value ? "" : l.credit })} /> : <bdi dir="ltr">{l.debit}</bdi>}</td>
        <td>{editable ? <input aria-label={t("jrn.credit")} inputMode="decimal" dir="ltr" value={l.credit} onChange={(e) => setLine(l.key, { credit: e.target.value, debit: e.target.value ? "" : l.debit })} /> : <bdi dir="ltr">{l.credit}</bdi>}</td>
        <td>
          {editable && lookups.branches.length > 1 ? (
            <select aria-label={t("jrn.branch")} value={l.branchId || branchId} onChange={(e) => setLine(l.key, { branchId: e.target.value })}>{lookups.branches.map((b) => <option key={b.id} value={b.id}>{localName(b, locale)}</option>)}</select>
          ) : null}
        </td>
        <td>
          {editable && <button type="button" className="ghost" disabled={lines.length <= 2} onClick={() => mark(() => setLines((ls) => ls.filter((x) => x.key !== l.key)))}>{t("jrn.remove_line")}</button>}
        </td>
        {(foreign && editable && can.rateOverride) || l.allocations.length > 0 || (editable && lookups.costCenters.length > 0) ? (
          <td className="line-extra">
            {foreign && editable && can.rateOverride && (
              <div>
                <input aria-label={t("jrn.rate_override")} placeholder={t("jrn.rate_override")} inputMode="decimal" dir="ltr" value={l.rateOverride} onChange={(e) => setLine(l.key, { rateOverride: e.target.value })} />
                {l.rateOverride && <input aria-label={t("jrn.rate_reason")} placeholder={t("jrn.rate_reason")} maxLength={200} value={l.rateReason} onChange={(e) => setLine(l.key, { rateReason: e.target.value })} />}
              </div>
            )}
            {l.allocations.map((a, k) => (
              <div key={k}>
                {editable ? (
                  <select aria-label={t("jrn.cost_center")} value={a.costCenterId} onChange={(e) => setLine(l.key, { allocations: l.allocations.map((x, j) => (j === k ? { ...x, costCenterId: e.target.value } : x)) })}>
                    <option value="">{t("jrn.choose_cost_center")}</option>
                    {lookups.costCenters.map((c) => <option key={c.id} value={c.id}>{c.code} — {localName(c, locale)}</option>)}
                  </select>
                ) : <span>{lookups.costCenters.find((c) => c.id === a.costCenterId)?.code ?? a.costCenterId}</span>}
                {editable ? <input aria-label={t(l.allocMode === "percent" ? "jrn.allocation_percent" : "jrn.allocation_amount")} inputMode="decimal" dir="ltr" value={a.amount} onChange={(e) => setLine(l.key, { allocations: l.allocations.map((x, j) => (j === k ? { ...x, amount: e.target.value } : x)) })} /> : <bdi dir="ltr">{a.amount}</bdi>}
                {editable && <button type="button" className="ghost" onClick={() => setLine(l.key, { allocations: l.allocations.filter((_, j) => j !== k) })}>{t("jrn.remove_allocation")}</button>}
              </div>
            ))}
            {editable && lookups.costCenters.length > 0 && <button type="button" className="ghost" onClick={() => setLine(l.key, { allocations: [...l.allocations, { costCenterId: "", amount: "" }] })}>{t("jrn.add_allocation")}</button>}
            {editable && l.allocations.length > 0 && (
              <select aria-label={t("jrn.alloc_mode")} value={l.allocMode} onChange={(e) => setLine(l.key, { allocMode: e.target.value as "amount" | "percent", allocations: l.allocations.map((x) => ({ ...x, amount: "" })) })}>
                <option value="amount">{t("jrn.alloc_mode_amount")}</option>
                <option value="percent">{t("jrn.alloc_mode_percent")}</option>
              </select>
            )}
          </td>
        ) : <td />}
      </tr>
    );
  };

  return (
    <>
      <PageHeader title={h ? `${t("jrn.entry")} ${h.number ?? t("jrn.no_number")}` : t("jrn.new")} description={h ? t(`jrn.status.${h.status}`) : t("jrn.new_hint")} actions={<button type="button" onClick={onBack}>{t("jrn.back")}</button>} />
      {lookups.accountsDenied && <Banner kind="warning" testId="jrn-no-accounts">{t("jrn.accounts_denied")}</Banner>}
      {h?.type === "reversal" && <Banner kind="info">{t("jrn.is_reversal")}</Banner>}
      {h?.type === "adjusting" && <Banner kind="info">{t("jrn.is_adjusting")}</Banner>}
      {h && h.status === "pending_approval" && entry && <Banner kind="info" testId="jrn-pending">{t("jrn.pending_note").replace("{n}", String(entry.requiredApprovals))}</Banner>}
      {h && h.status === "pending_approval" && entry?.plan && entry.plan.length > 1 && (
        <Card title={t("jrn.plan")}>
          <ol data-testid="jrn-plan">
            {entry.plan.map((s, i) => {
              const done = s.approved >= s.required;
              const open = !done && entry.plan!.slice(0, i).every((x) => x.approved >= x.required);
              return <li key={s.stage} data-testid={`jrn-plan-${s.stage}`}>{t("jrn.plan_stage", { n: String(s.stage) })}: <bdi dir="ltr">{s.approved}/{s.required}</bdi> — {done ? t("jrn.plan_done") : open ? t("jrn.plan_open") : t("jrn.plan_waiting")}</li>;
            })}
          </ol>
        </Card>
      )}
      <form onSubmit={save} noValidate data-testid="jrn-form">
        <Card title={t("jrn.header")}>
          <fieldset disabled={!editable || runner.busy}>
            <Field id="jrn-date" label={t("jrn.date")} required error={runner.fields.entryDate}>{(a) => <input {...a} type="date" value={date} onChange={(e) => mark(() => setDate(e.target.value))} />}</Field>
            <Field id="jrn-branch" label={t("jrn.branch")} required error={runner.fields.branchId}>
              {(a) => <select {...a} value={branchId} onChange={(e) => mark(() => setBranchId(e.target.value))}>{lookups.branches.map((b) => <option key={b.id} value={b.id}>{localName(b, locale)}</option>)}</select>}
            </Field>
            <Field id="jrn-desc" label={t("jrn.description")} error={runner.fields.description}>{(a) => <input {...a} maxLength={500} value={description} onChange={(e) => mark(() => setDescription(e.target.value))} />}</Field>
          </fieldset>
        </Card>
        <Card title={t("jrn.lines")}>
          <div className="table-scroll">
            <table className="table" data-testid="jrn-lines">
              <thead><tr><th>{t("jrn.account")}</th><th>{t("jrn.line_description")}</th><th>{t("jrn.currency")}</th><th>{t("jrn.debit")}</th><th>{t("jrn.credit")}</th><th>{t("jrn.branch")}</th><th /><th>{t("jrn.extras")}</th></tr></thead>
              <tbody>{lines.map(lineView)}</tbody>
              <tfoot>
                {[...sums.entries()].map(([cur, s]) => (
                  <tr key={cur} data-testid={`jrn-total-${cur}`}><th colSpan={3}>{t("jrn.total")} <bdi dir="ltr">{cur}</bdi></th><td><bdi dir="ltr">{s.debit}</bdi></td><td><bdi dir="ltr">{s.credit}</bdi></td><td colSpan={3} /></tr>
                ))}
              </tfoot>
            </table>
          </div>
          {Object.entries(runner.fields).filter(([k]) => k.startsWith("lines")).map(([k, v]) => <p key={k} className="field-error">{k}: {v}</p>)}
          {editable && <button type="button" onClick={() => mark(() => setLines((ls) => [...ls, blankLine(base ?? "", branchId)]))}>{t("jrn.add_line")}</button>}
          {entry && (
            <p data-testid="jrn-balance" className={entry.balanced ? "hint" : "field-error"}>
              {entry.balanced ? t("jrn.balanced") : t("jrn.unbalanced")} — {t("jrn.base_totals")}: <bdi dir="ltr">{entry.baseDebitTotal} / {entry.baseCreditTotal} {h?.baseCurrency}</bdi>
            </p>
          )}
          {editable && problems.some(Boolean) && <p className="hint" data-testid="jrn-problems">{t(problems.find(Boolean)!)}</p>}
        </Card>
        {runner.error && <Banner kind="error" testId="jrn-error">{runner.error}</Banner>}
        {runner.conflict && <Banner kind="warning" testId="jrn-conflict">{t("common.conflict")}</Banner>}
        {runner.notice && <Banner kind="success" testId="jrn-notice">{runner.notice}</Banner>}
        <div className="actions">
          {editable && <button type="submit" className="primary" disabled={!formOk || runner.busy}>{t("jrn.save_draft")}</button>}
          {entry && h && can.create && status === "draft" && !dirty && <button type="button" className="primary" disabled={runner.busy} onClick={() => void step((e) => auth.api.submitJournalEntry(e.entry.id, e.entry.revision), t("jrn.submitted"))}>{t("jrn.submit")}</button>}
          {entry && h && (status === "pending_approval" || status === "approved") && can.create && <button type="button" disabled={runner.busy} onClick={() => void step((e) => auth.api.withdrawJournalEntry(e.entry.id, e.entry.revision), t("jrn.withdrawn"))}>{t("jrn.withdraw")}</button>}
          {entry && h && status === "pending_approval" && can.approve && <><button type="button" className="primary" disabled={runner.busy} onClick={() => setPrompt("approve")}>{t("jrn.approve")}</button><button type="button" disabled={runner.busy} onClick={() => setPrompt("reject")}>{t("jrn.reject")}</button></>}
          {entry && h && status === "approved" && can.post && <button type="button" className="primary" disabled={runner.busy} onClick={() => void step((e) => auth.api.postJournalEntry(e.entry.id, e.entry.revision), t("jrn.posted"))}>{t("jrn.post")}</button>}
          {entry && h && status === "posted" && h.type !== "reversal" && can.reverse && <button type="button" disabled={runner.busy} onClick={() => setPrompt("reverse")}>{t("jrn.reverse")}</button>}
          {entry && h && (status === "posted" || status === "reversed") && can.create && <button type="button" onClick={() => onOpen(`adjust:${h.id}`)}>{t("jrn.adjust")}</button>}
          {entry && h && (status === "draft" || status === "pending_approval" || status === "approved") && can.create && <button type="button" className="danger" disabled={runner.busy} onClick={() => void step((e) => auth.api.cancelJournalEntry(e.entry.id, e.entry.revision), t("jrn.cancelled"))}>{t("jrn.cancel_entry")}</button>}
        </div>
      </form>
      {prompt === "reverse" && entry && <Prompt title={t("jrn.reverse")} label={t("jrn.reverse_date")} confirm={t("jrn.reverse")} required kind="date" onCancel={() => setPrompt(null)}
        onConfirm={(v) => { setPrompt(null); void runner.run(async () => { const r = await auth.api.reverseJournalEntry(entry.entry.id, v, null, entry.entry.revision); onOpen(r.entry.id); return r; }, t("jrn.reversal_drafted")); }} />}
      {prompt === "approve" && entry && <Prompt title={t("jrn.approve")} label={t("jrn.comment_optional")} confirm={t("jrn.approve")} required={false} kind="text" onCancel={() => setPrompt(null)}
        onConfirm={(v) => { setPrompt(null); void step((e) => auth.api.decideJournalEntry(e.entry.id, true, v || null, e.entry.revision), t("jrn.decision_recorded")); }} />}
      {prompt === "reject" && entry && <Prompt title={t("jrn.reject")} label={t("jrn.reject_reason")} confirm={t("jrn.reject")} required kind="text" onCancel={() => setPrompt(null)}
        onConfirm={(v) => { setPrompt(null); void step((e) => auth.api.decideJournalEntry(e.entry.id, false, v, e.entry.revision), t("jrn.decision_recorded")); }} />}
      {entry && entry.approvals.length > 0 && (
        <Card title={t("jrn.approvals")}>
          <ul data-testid="jrn-approvals">{entry.approvals.map((a) => <li key={a.id}>{a.approved ? t("jrn.approved_by") : t("jrn.rejected_by")} <bdi>{a.approver.slice(0, 8)}</bdi> — r{a.documentRevision} — {new Date(a.decidedAtUtc).toLocaleString()}{a.comment ? ` — ${a.comment}` : ""}</li>)}</ul>
        </Card>
      )}
      {entry && (
        <Card title={t("jrn.history")}>
          <ul data-testid="jrn-events">{entry.events.map((ev) => <li key={ev.id}>{t(`jrn.event.${ev.kind}`)} — {new Date(ev.atUtc).toLocaleString()}{ev.detail ? ` — ${ev.detail}` : ""}</li>)}</ul>
        </Card>
      )}
    </>
  );
}

export function JournalPage() {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [list, setList] = useState<JournalListResponse | null>(null);
  const [lookups, setLookups] = useState<Lookups | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [filters, setFilters] = useState<{ status: "" | JournalStatus; from: string; to: string; text: string }>({ status: "", from: "", to: "", text: "" });
  const [applied, setApplied] = useState(filters);
  const [screen, setScreen] = useState<{ kind: "list" } | { kind: "entry"; id: string | null; adjustsId: string | null }>(() => {
    // a drill-down from a report opens its entry straight away (the server still decides whether the caller may see it)
    try { const id = sessionStorage.getItem(OPEN_JOURNAL_KEY); if (id) { sessionStorage.removeItem(OPEN_JOURNAL_KEY); return { kind: "entry", id, adjustsId: null }; } } catch { /* list */ }
    return { kind: "list" };
  });

  const load = useCallback(async () => {
    try {
      setList(await auth.api.listJournal({ status: applied.status || undefined, from: applied.from || undefined, to: applied.to || undefined, text: applied.text || undefined }));
      setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "jrn.error.")); }
  }, [auth.api, auth.handleError, applied]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    void (async () => {
      let accounts: Account[] = []; let denied = false; let currencies: string[] = []; let baseCurrency: string | null = null; let branches: Branch[] = []; let costCenters: AllocatableCostCenter[] = [];
      try { accounts = (await auth.api.listAccounts()).accounts; } catch { denied = true; }
      try { const c = await auth.api.getCurrencies(); currencies = c.currencies.filter((x) => x.enabled).map((x) => x.code); baseCurrency = c.baseCurrency; } catch { /* no permission: base currency lines only */ }
      try { branches = (await auth.api.listBranches()).branches.filter((b) => b.isActive); } catch { /* none */ }
      try { costCenters = (await auth.api.listAllocatableCostCenters()).costCenters; } catch { /* no permission */ }
      setLookups({ accounts, accountsDenied: denied, currencies, baseCurrency, branches, costCenters });
    })();
  }, [auth.api]);

  if (loadError) return <Banner kind="error" testId="jrn-load-error">{loadError}</Banner>;
  if (!list || !lookups) return <Loading />;

  if (screen.kind === "entry") {
    return <EntryScreen key={`${screen.id}-${screen.adjustsId}`} id={screen.id} adjustsId={screen.adjustsId} lookups={lookups} can={list.can}
      onBack={() => guard(() => { setScreen({ kind: "list" }); void load(); })}
      onOpen={(id) => { void load(); setScreen(id.startsWith("adjust:") ? { kind: "entry", id: null, adjustsId: id.slice(7) } : { kind: "entry", id, adjustsId: null }); }} />;
  }
  const nameOf = (h: JournalHeader) => h.description ?? "";
  return (
    <>
      <PageHeader title={t("jrn.title")} description={t("jrn.subtitle")} actions={list.can.create ? <button type="button" className="primary" onClick={() => setScreen({ kind: "entry", id: null, adjustsId: null })}>{t("jrn.new")}</button> : undefined} />
      <Card>
        <form className="filters" onSubmit={(e) => { e.preventDefault(); setApplied(filters); }}>
          <Field id="jf-status" label={t("jrn.filter_status")}>
            {(a) => <select {...a} value={filters.status} onChange={(e) => setFilters({ ...filters, status: e.target.value as "" | JournalStatus })}><option value="">{t("common.all")}</option>{STATUSES.map((s) => <option key={s} value={s}>{t(`jrn.status.${s}`)}</option>)}</select>}
          </Field>
          <Field id="jf-from" label={t("jrn.filter_from")}>{(a) => <input {...a} type="date" value={filters.from} onChange={(e) => setFilters({ ...filters, from: e.target.value })} />}</Field>
          <Field id="jf-to" label={t("jrn.filter_to")}>{(a) => <input {...a} type="date" value={filters.to} onChange={(e) => setFilters({ ...filters, to: e.target.value })} />}</Field>
          <Field id="jf-text" label={t("jrn.filter_text")}>{(a) => <input {...a} value={filters.text} onChange={(e) => setFilters({ ...filters, text: e.target.value })} />}</Field>
          <button type="submit">{t("jrn.apply_filters")}</button>
        </form>
      </Card>
      <Card>
        {list.entries.length === 0 ? <p data-testid="jrn-empty">{t("jrn.empty")}</p> : (
          <table className="table" data-testid="jrn-table">
            <thead><tr><th>{t("jrn.number")}</th><th>{t("jrn.date")}</th><th>{t("jrn.description")}</th><th>{t("jrn.type")}</th><th>{t("common.status")}</th><th>{t("jrn.base_total")}</th><th /></tr></thead>
            <tbody>
              {list.entries.map((h) => (
                <tr key={h.id} data-testid={`jrn-row-${h.id}`}>
                  <td><bdi dir="ltr">{h.number ?? "—"}</bdi></td><td>{h.entryDate}</td><td>{nameOf(h)}</td><td>{t(`jrn.type_${h.type}`)}</td>
                  <td><span className={`badge badge-${h.status}`}>{t(`jrn.status.${h.status}`)}</span></td>
                  <td><bdi dir="ltr">{h.baseTotal} {h.baseCurrency}</bdi></td>
                  <td><button type="button" className="ghost" onClick={() => setScreen({ kind: "entry", id: h.id, adjustsId: null })}>{t("jrn.open")}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}
