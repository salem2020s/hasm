import type { ApprovalPoliciesResponse, ApprovalPolicy, Branch, PolicyJournalType, PolicySource } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { blankToNull, isDecimal, localName, toLatinDigits, useRunner } from "../ledgerCommon";
import { useReportUnsaved, useUnsaved } from "../unsaved";

/**
 * Approval policies (Phase 3.2B S4). A policy says how many DISTINCT approvers a document needs when its base-currency amount reaches a threshold (optionally per branch and per
 * source). The strictest matching policy applies, automatic entries are covered too, and the creator or submitter can never approve their own entry (not configurable).
 * With no matching policy nothing requires approval. Policies are never deleted; deactivate them.
 */

type Editor = { mode: "new" } | { mode: "edit"; policy: ApprovalPolicy };
const triText = (v: boolean | null | undefined) => (v === true ? "yes" : v === false ? "no" : "");
const triValue = (v: string): boolean | null => (v === "yes" ? true : v === "no" ? false : null);

function Form({ editor, docTypes, branches, onDone, reload }: { editor: Editor; docTypes: string[]; branches: Branch[]; onDone: () => void; reload: () => Promise<void> }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("pol.error.", reload);
  const p = editor.mode === "edit" ? editor.policy : null;
  const initial = {
    docType: p?.docType ?? docTypes[0] ?? "journal_entry", source: (p?.source ?? "any") as PolicySource, branchId: p?.branchId ?? "", minAmount: p ? p.minAmount : "0",
    required: String(p && !p.isStaged ? p.requiredApprovals : 1), nameAr: p?.nameAr ?? "", nameEn: p?.nameEn ?? "",
    journalType: (p?.journalType && p.journalType !== "any" ? p.journalType : "") as PolicyJournalType | "", rateOverride: triText(p?.rateOverride), softClosed: triText(p?.softClosed),
    closingKind: (p?.closingKind && p.closingKind !== "any" ? p.closingKind : "") as "closing" | "opening" | "",
    staged: p?.isStaged ?? false, stages: (p?.stages ?? []).length > 0 ? p!.stages!.map((s) => ({ required: String(s.required), roleId: s.roleId })) : [{ required: "1", roleId: null as string | null }],
  };
  const [f, setF] = useState(initial);
  const dirty = JSON.stringify(f) !== JSON.stringify(initial);
  useReportUnsaved("pol-form", dirty, t("pol.form"));
  const min = toLatinDigits(f.minAmount.trim());
  const required = Number(toLatinDigits(f.required));
  const stageCounts = f.stages.map((s) => Number(toLatinDigits(s.required)));
  const stagesOk = f.stages.length >= 1 && stageCounts.every((n) => Number.isInteger(n) && n >= 1 && n <= 1000);
  const valid = isDecimal(min, 4) && (f.staged ? stagesOk : Number.isInteger(required) && required >= 1 && required <= 5) && (f.nameAr.trim() !== "" || f.nameEn.trim() !== "");

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!valid) return;
    const body = {
      source: f.source, branchId: f.branchId || null, minAmount: min, nameAr: blankToNull(f.nameAr), nameEn: blankToNull(f.nameEn),
      journalType: f.journalType || null, rateOverride: triValue(f.rateOverride), softClosed: triValue(f.softClosed), closingKind: f.closingKind || null,
      ...(f.staged ? { stages: f.stages.map((s, i) => ({ required: stageCounts[i]!, roleId: s.roleId })) } : { requiredApprovals: required }),
    };
    const done = await runner.run(() => (editor.mode === "new" ? auth.api.createApprovalPolicy({ docType: f.docType, ...body }) : auth.api.updateApprovalPolicy(editor.policy.id, body, editor.policy.revision)), t("pol.saved"));
    if (done) onDone();
  }

  return (
    <Card title={t(editor.mode === "new" ? "pol.form_new" : "pol.form_edit")}>
      <form onSubmit={submit} noValidate data-testid="pol-form">
        <fieldset disabled={runner.busy}>
          <Field id="pol-doc" label={t("pol.doc_type")}>
            {(a) => <select {...a} value={f.docType} disabled={editor.mode === "edit"} onChange={(e) => setF({ ...f, docType: e.target.value })}>{docTypes.map((d) => <option key={d} value={d}>{t(`numbering.doc.${d}`)}</option>)}</select>}
          </Field>
          <Field id="pol-source" label={t("pol.source")}>
            {(a) => <select {...a} value={f.source} onChange={(e) => setF({ ...f, source: e.target.value as PolicySource })}>{(["any", "manual", "automatic"] as const).map((s) => <option key={s} value={s}>{t(`pol.source_${s}`)}</option>)}</select>}
          </Field>
          <Field id="pol-branch" label={t("pol.branch")} hint={t("pol.branch_hint")}>
            {(a) => <select {...a} value={f.branchId} onChange={(e) => setF({ ...f, branchId: e.target.value })}><option value="">{t("pol.all_branches")}</option>{branches.map((b) => <option key={b.id} value={b.id}>{localName(b, locale)}</option>)}</select>}
          </Field>
          <Field id="pol-min" label={t("pol.min_amount")} hint={t("pol.min_hint")} required error={runner.fields.minAmount ?? (f.minAmount && !isDecimal(min, 4) ? t("pol.min_invalid") : undefined)}>
            {(a) => <input {...a} inputMode="decimal" dir="ltr" value={f.minAmount} onChange={(e) => setF({ ...f, minAmount: e.target.value })} />}
          </Field>
          <Field id="pol-jtype" label={t("pol.cond_type")} hint={t("pol.cond_hint")}>
            {(a) => <select {...a} value={f.journalType} onChange={(e) => setF({ ...f, journalType: e.target.value as PolicyJournalType | "" })}><option value="">{t("pol.cond_any")}</option>{(["normal", "reversal", "adjusting"] as const).map((x) => <option key={x} value={x}>{t(`pol.type_${x}`)}</option>)}</select>}
          </Field>
          <Field id="pol-closing" label={t("pol.cond_closing")} hint={t("pol.cond_closing_hint")}>
            {(a) => <select {...a} value={f.closingKind} onChange={(e) => setF({ ...f, closingKind: e.target.value as "closing" | "opening" | "" })}><option value="">{t("pol.cond_any")}</option><option value="closing">{t("pol.closing_closing")}</option><option value="opening">{t("pol.closing_opening")}</option></select>}
          </Field>
          <Field id="pol-rate" label={t("pol.cond_rate")}>
            {(a) => <select {...a} value={f.rateOverride} onChange={(e) => setF({ ...f, rateOverride: e.target.value })}><option value="">{t("pol.cond_any")}</option><option value="yes">{t("pol.cond_yes")}</option><option value="no">{t("pol.cond_no")}</option></select>}
          </Field>
          <Field id="pol-soft" label={t("pol.cond_soft")}>
            {(a) => <select {...a} value={f.softClosed} onChange={(e) => setF({ ...f, softClosed: e.target.value })}><option value="">{t("pol.cond_any")}</option><option value="yes">{t("pol.cond_yes")}</option><option value="no">{t("pol.cond_no")}</option></select>}
          </Field>
          <div className="field"><label><input type="checkbox" checked={f.staged} onChange={(e) => setF({ ...f, staged: e.target.checked })} /> {t("pol.staged")}</label><p className="hint">{t("pol.staged_hint")}</p></div>
          {f.staged ? (
            <div data-testid="pol-stages">
              {f.stages.map((s, i) => (
                <div className="field" key={i}>
                  <label htmlFor={`pol-stage-${i}`}>{t("pol.stage_n", { n: String(i + 1) })}{s.roleId ? ` (${t("pol.stage_role_bound")})` : ""}</label>
                  <input id={`pol-stage-${i}`} inputMode="numeric" aria-label={t("pol.stage_required", { n: String(i + 1) })} value={s.required} onChange={(e) => setF({ ...f, stages: f.stages.map((x, j) => (j === i ? { ...x, required: e.target.value } : x)) })} />
                  <button type="button" className="ghost" disabled={f.stages.length <= 1} onClick={() => setF({ ...f, stages: f.stages.filter((_, j) => j !== i) })}>{t("pol.remove_stage")}</button>
                </div>
              ))}
              <button type="button" className="ghost" onClick={() => setF({ ...f, stages: [...f.stages, { required: "1", roleId: null }] })}>{t("pol.add_stage")}</button>
              {runner.fields.stages && <p role="alert">{runner.fields.stages}</p>}
            </div>
          ) : (
            <Field id="pol-required" label={t("pol.required")} hint={t("pol.required_hint")} required error={runner.fields.requiredApprovals}>
              {(a) => <input {...a} inputMode="numeric" value={f.required} onChange={(e) => setF({ ...f, required: e.target.value })} />}
            </Field>
          )}
          <Field id="pol-name-ar" label={t("cc.name_ar")} error={runner.fields.nameAr}>{(a) => <input {...a} dir="rtl" maxLength={200} value={f.nameAr} onChange={(e) => setF({ ...f, nameAr: e.target.value })} />}</Field>
          <Field id="pol-name-en" label={t("cc.name_en")} error={runner.fields.nameEn}>{(a) => <input {...a} dir="ltr" maxLength={200} value={f.nameEn} onChange={(e) => setF({ ...f, nameEn: e.target.value })} />}</Field>
          <button type="submit" className="primary" disabled={!valid}>{t("common.save")}</button>
          <button type="button" onClick={onDone}>{t("common.close")}</button>
        </fieldset>
        {runner.error && <Banner kind="error" testId="pol-error">{runner.error}</Banner>}
        {runner.conflict && <Banner kind="warning" testId="pol-conflict">{t("common.conflict")}</Banner>}
      </form>
    </Card>
  );
}

export function ApprovalPoliciesPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [data, setData] = useState<ApprovalPoliciesResponse | null>(null);
  const [branches, setBranches] = useState<Branch[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const load = useCallback(async () => {
    try {
      setData(await auth.api.listApprovalPolicies());
      if (auth.canAny("company.branches.view")) { try { setBranches((await auth.api.listBranches()).branches.filter((b) => b.isActive)); } catch { setBranches([]); } }
      setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "pol.error.")); }
  }, [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);
  const runner = useRunner("pol.error.", load);

  if (loadError) return <Banner kind="error" testId="pol-load-error">{loadError}</Banner>;
  if (!data) return <Loading />;
  const open = (e: Editor) => guard(() => { runner.clear(); setEditor(e); });
  return (
    <>
      <PageHeader title={t("pol.title")} description={t("pol.subtitle")} actions={data.can.create ? <button type="button" className="primary" onClick={() => open({ mode: "new" })}>{t("pol.new")}</button> : undefined} />
      <Banner kind="info">{t("pol.sod_note")}</Banner>
      {runner.error && <Banner kind="error">{runner.error}</Banner>}
      {runner.conflict && <Banner kind="warning">{t("common.conflict")}</Banner>}
      {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      {editor && <Form key={editor.mode === "edit" ? editor.policy.id : "new"} editor={editor} docTypes={data.documentTypes} branches={branches} reload={load} onDone={() => guard(() => setEditor(null))} />}
      <Card>
        {data.policies.length === 0 ? <p data-testid="pol-empty">{t("pol.empty")}</p> : (
          <table className="table" data-testid="pol-table">
            <thead><tr><th>{t("cc.name")}</th><th>{t("pol.doc_type")}</th><th>{t("pol.source")}</th><th>{t("pol.branch")}</th><th>{t("pol.min_amount")}</th><th>{t("pol.required")}</th><th>{t("pol.conditions")}</th><th>{t("common.status")}</th><th>{t("common.actions")}</th></tr></thead>
            <tbody>
              {data.policies.map((p) => (
                <tr key={p.id} data-testid={`pol-row-${p.id}`}>
                  <td>{localName(p, locale)}</td>
                  <td>{t(`numbering.doc.${p.docType}`)}</td>
                  <td>{t(`pol.source_${p.source}`)}</td>
                  <td>{p.branchId ? localName(branches.find((b) => b.id === p.branchId) ?? { nameAr: null, nameEn: null, code: p.branchId.slice(0, 8) }, locale) : t("pol.all_branches")}</td>
                  <td><bdi dir="ltr">{p.minAmount}</bdi></td>
                  <td>{p.isStaged ? <span data-testid={`pol-stages-${p.id}`}>{(p.stages ?? []).map((s) => s.required).join(" › ")}</span> : p.requiredApprovals}</td>
                  <td>{[p.journalType && p.journalType !== "any" ? t(`pol.type_${p.journalType}`) : null, p.rateOverride === true ? t("pol.cond_rate_short") : p.rateOverride === false ? t("pol.cond_no_rate_short") : null, p.softClosed === true ? t("pol.cond_soft_short") : p.softClosed === false ? t("pol.cond_no_soft_short") : null, p.closingKind === "closing" || p.closingKind === "opening" ? t(`pol.closing_${p.closingKind}`) : null].filter(Boolean).join(" · ") || "—"}</td>
                  <td>{p.isActive ? t("common.active") : t("common.inactive")}</td>
                  <td>
                    {data.can.update && <button type="button" className="ghost" onClick={() => open({ mode: "edit", policy: p })}>{t("common.edit")}</button>}
                    {data.can.activate && (p.isActive
                      ? <button type="button" className="ghost" onClick={() => void runner.run(() => auth.api.deactivateApprovalPolicy(p.id, p.revision), t("pol.saved"))}>{t("cc.deactivate")}</button>
                      : <button type="button" className="ghost" onClick={() => void runner.run(() => auth.api.activateApprovalPolicy(p.id, p.revision), t("pol.saved"))}>{t("cc.activate")}</button>)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}
