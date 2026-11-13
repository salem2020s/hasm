import type { Branch, CostCenter, CostCentersResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { blankToNull, localName, toLatinDigits, useRunner } from "../ledgerCommon";
import { useReportUnsaved, useUnsaved } from "../unsaved";

/**
 * Cost centers (Phase 3.2B S3): a multi-level tree separate from branches. Only an ACTIVE LEAF can receive allocations; groups only organize, so roll-ups never double count.
 * Codes are re-coded and cost centers moved through their own controlled actions; nothing is deleted (deactivation keeps history).
 */

type Editor = { mode: "new"; parentId: string | null } | { mode: "edit" | "recode" | "move"; item: CostCenter };

const CODE_SHAPE = /^[A-Z0-9][A-Z0-9._-]{0,29}$/;
const normalizeCode = (s: string) => toLatinDigits(s.trim()).toUpperCase();

function Form({ editor, data, branches, onDone, reload }: { editor: Editor; data: CostCentersResponse; branches: Branch[]; onDone: () => void; reload: () => Promise<void> }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("cc.error.", reload);
  const item = editor.mode === "new" ? null : editor.item;
  const initial = {
    code: item?.code ?? "", nameAr: item?.nameAr ?? "", nameEn: item?.nameEn ?? "", isGroup: item?.isGroup ?? false, branchId: item?.branchId ?? "",
    parentId: editor.mode === "new" ? editor.parentId ?? "" : item?.parentId ?? "",
  };
  const [f, setF] = useState(initial);
  const dirty = JSON.stringify(f) !== JSON.stringify(initial);
  useReportUnsaved("cc-form", dirty, t("cc.form"));
  const code = normalizeCode(f.code);
  const nameOk = f.nameAr.trim() !== "" || f.nameEn.trim() !== "";
  const codeOk = CODE_SHAPE.test(code);
  const parents = data.costCenters.filter((c) => c.isGroup && c.isActive && c.id !== item?.id);

  async function submit(e: FormEvent) {
    e.preventDefault();
    const ok = editor.mode === "new" ? codeOk && nameOk : editor.mode === "edit" ? nameOk : editor.mode === "recode" ? codeOk : true;
    if (!ok) return;
    const names = { nameAr: blankToNull(f.nameAr), nameEn: blankToNull(f.nameEn), branchId: f.branchId || null, isGroup: f.isGroup };
    const done = await runner.run(async () => {
      if (editor.mode === "new") return auth.api.createCostCenter({ code, parentId: f.parentId || null, ...names });
      if (editor.mode === "edit") return auth.api.updateCostCenter(editor.item.id, names, editor.item.revision);
      if (editor.mode === "recode") return auth.api.recodeCostCenter(editor.item.id, code, editor.item.revision);
      return auth.api.moveCostCenter(editor.item.id, f.parentId || null, editor.item.revision);
    }, t("cc.saved"));
    if (done) onDone();
  }

  return (
    <Card title={t(`cc.form_${editor.mode}`)}>
      <form onSubmit={submit} noValidate data-testid="cc-form">
        <fieldset disabled={runner.busy}>
          {(editor.mode === "new" || editor.mode === "recode") && (
            <Field id="cc-code" label={t("cc.code")} required error={runner.fields.code ?? (f.code && !codeOk ? t("field.invalid_format") : undefined)}>
              {(a) => <input {...a} dir="ltr" maxLength={30} value={f.code} onChange={(e) => setF({ ...f, code: e.target.value })} />}
            </Field>
          )}
          {(editor.mode === "new" || editor.mode === "edit") && (
            <>
              <Field id="cc-name-ar" label={t("cc.name_ar")} error={runner.fields.nameAr}>{(a) => <input {...a} dir="rtl" maxLength={200} value={f.nameAr} onChange={(e) => setF({ ...f, nameAr: e.target.value })} />}</Field>
              <Field id="cc-name-en" label={t("cc.name_en")} error={runner.fields.nameEn}>{(a) => <input {...a} dir="ltr" maxLength={200} value={f.nameEn} onChange={(e) => setF({ ...f, nameEn: e.target.value })} />}</Field>
              <div className="field"><label><input type="checkbox" checked={f.isGroup} onChange={(e) => setF({ ...f, isGroup: e.target.checked })} /> {t("cc.is_group")}</label><p className="hint">{t("cc.group_hint")}</p></div>
              <Field id="cc-branch" label={t("cc.branch")} hint={t("cc.branch_hint")}>
                {(a) => <select {...a} value={f.branchId} onChange={(e) => setF({ ...f, branchId: e.target.value })}><option value="">{t("cc.no_branch")}</option>{branches.map((b) => <option key={b.id} value={b.id}>{localName(b, locale)}</option>)}</select>}
              </Field>
            </>
          )}
          {(editor.mode === "new" || editor.mode === "move") && (
            <Field id="cc-parent" label={t("cc.parent")} error={runner.fields.parentId}>
              {(a) => <select {...a} value={f.parentId} onChange={(e) => setF({ ...f, parentId: e.target.value })}><option value="">{t("cc.root")}</option>{parents.map((p) => <option key={p.id} value={p.id}>{p.code} — {localName(p, locale)}</option>)}</select>}
            </Field>
          )}
          <button type="submit" className="primary" disabled={editor.mode === "new" ? !codeOk || !nameOk : editor.mode === "edit" ? !nameOk : editor.mode === "recode" ? !codeOk : false}>{t("common.save")}</button>
          <button type="button" onClick={onDone}>{t("common.close")}</button>
        </fieldset>
        {runner.error && <Banner kind="error" testId="cc-error">{runner.error}</Banner>}
        {runner.conflict && <Banner kind="warning" testId="cc-conflict">{t("common.conflict")}</Banner>}
      </form>
    </Card>
  );
}

export function CostCentersPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [data, setData] = useState<CostCentersResponse | null>(null);
  const [branches, setBranches] = useState<Branch[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const load = useCallback(async () => {
    try {
      setData(await auth.api.listCostCenters());
      if (auth.canAny("company.branches.view")) { try { setBranches((await auth.api.listBranches()).branches.filter((b) => b.isActive)); } catch { setBranches([]); } }
      setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "cc.error.")); }
  }, [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);
  const runner = useRunner("cc.error.", load);

  const rows = useMemo(() => {
    const list = data?.costCenters ?? [];
    const byParent = new Map<string | null, CostCenter[]>();
    for (const c of list) { const k = c.parentId; if (!byParent.has(k)) byParent.set(k, []); byParent.get(k)!.push(c); }
    for (const v of byParent.values()) v.sort((a, b) => a.code.localeCompare(b.code, "en", { numeric: true }));
    const out: { c: CostCenter; level: number }[] = [];
    const walk = (parent: string | null, level: number) => { for (const c of byParent.get(parent) ?? []) { out.push({ c, level }); walk(c.id, level + 1); } };
    walk(null, 0);
    return out;
  }, [data]);

  if (loadError) return <Banner kind="error" testId="cc-load-error">{loadError}</Banner>;
  if (!data) return <Loading />;
  const can = data.can;
  const open = (e: Editor) => guard(() => { runner.clear(); setEditor(e); });
  return (
    <>
      <PageHeader title={t("cc.title")} description={t("cc.subtitle")} actions={can.create ? <button type="button" className="primary" onClick={() => open({ mode: "new", parentId: null })}>{t("cc.new")}</button> : undefined} />
      <Banner kind="info">{t("cc.allocation_note")}</Banner>
      {runner.error && <Banner kind="error">{runner.error}</Banner>}
      {runner.conflict && <Banner kind="warning">{t("common.conflict")}</Banner>}
      {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      {editor && <Form key={`${editor.mode}-${"item" in editor ? editor.item.id : "new"}`} editor={editor} data={data} branches={branches} reload={load} onDone={() => guard(() => setEditor(null))} />}
      <Card>
        {rows.length === 0 ? <p data-testid="cc-empty">{t("cc.empty")}</p> : (
          <table className="table" data-testid="cc-table">
            <thead><tr><th>{t("cc.code")}</th><th>{t("cc.name")}</th><th>{t("cc.kind")}</th><th>{t("common.status")}</th><th>{t("common.actions")}</th></tr></thead>
            <tbody>
              {rows.map(({ c, level }) => (
                <tr key={c.id} data-testid={`cc-row-${c.code}`}>
                  <td><bdi dir="ltr" style={{ paddingInlineStart: `${level * 1.25}rem` }}>{c.code}</bdi></td>
                  <td>{localName(c, locale)}</td>
                  <td>{c.isGroup ? t("cc.group") : t("cc.leaf")}</td>
                  <td>{c.isActive ? t("common.active") : t("common.inactive")}</td>
                  <td>
                    {can.create && c.isGroup && c.isActive && <button type="button" className="ghost" onClick={() => open({ mode: "new", parentId: c.id })}>{t("cc.add_child")}</button>}
                    {can.update && <button type="button" className="ghost" onClick={() => open({ mode: "edit", item: c })}>{t("common.edit")}</button>}
                    {can.recode && <button type="button" className="ghost" onClick={() => open({ mode: "recode", item: c })}>{t("cc.recode")}</button>}
                    {can.restructure && <button type="button" className="ghost" onClick={() => open({ mode: "move", item: c })}>{t("cc.move")}</button>}
                    {can.activate && (c.isActive
                      ? <button type="button" className="ghost" onClick={() => void runner.run(() => auth.api.deactivateCostCenter(c.id, c.revision), t("cc.saved"))}>{t("cc.deactivate")}</button>
                      : <button type="button" className="ghost" onClick={() => void runner.run(() => auth.api.activateCostCenter(c.id, c.revision), t("cc.saved"))}>{t("cc.activate")}</button>)}
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
