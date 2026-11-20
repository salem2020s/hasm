import type { TaxClass } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, errorText, Field, Loading } from "../components";
import { localName, useRunner } from "../ledgerCommon";
import { useReportUnsaved } from "../unsaved";

/**
 * The tax class of ONE item or local party (Phase 3.10). Shown inside the existing item and party editors as an extra tab for users who may view taxes; it saves on its own (a separate
 * API and permission: it never rides on the record's Save, so a record the user cannot edit can still be classified by a tax administrator, and the reverse). Items and parties use
 * separate class kinds; the server refuses a wrong kind. An unsaved choice is reported to the shared unsaved-changes guard.
 */
export function TaxClassPicker({ kind, subjectId }: { kind: "item" | "party"; subjectId: string }) {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [classes, setClasses] = useState<TaxClass[] | null>(null);
  const [current, setCurrent] = useState("");
  const [choice, setChoice] = useState<string | null>(null);          // null = untouched: the stored class is shown
  const [configure, setConfigure] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try {
      const [cl, asg] = await Promise.all([auth.api.listTaxClasses(), kind === "item" ? auth.api.listItemTaxClasses() : auth.api.listPartyTaxClasses()]);
      setClasses(cl.classes.filter((c) => c.kind === (kind === "item" ? 1 : 2))); setConfigure(asg.can.configure);
      setCurrent(asg.assignments.find((a) => a.subjectId === subjectId)?.classId ?? ""); setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "tax.error.")); }
  }, [auth.api, auth.handleError, kind, subjectId]);
  useEffect(() => { void load(); }, [load]);
  const runner = useRunner("tax.error.", load);
  const value = choice ?? current;
  const dirty = choice !== null && choice !== current;
  useReportUnsaved(`tax-class-${kind}-${subjectId}`, dirty, t("tax.picker.title"));

  if (!classes) return loadError ? <Banner kind="error" testId="tax-picker-error">{loadError}</Banner> : <Loading />;
  const options = classes.filter((c) => c.isActive || c.id === current);

  async function save() {
    const classId = value === "" ? null : value;
    const done = await runner.run(() => (kind === "item" ? auth.api.assignItemTaxClass(subjectId, classId) : auth.api.assignPartyTaxClass(subjectId, classId)), t("tax.saved"));
    if (done) setChoice(null);
  }

  return (
    <div data-testid="tax-class-picker">
      <p className="hint">{t("tax.picker.hint")}</p>
      {runner.error && <Banner kind="error" testId="tax-picker-save-error">{runner.error}</Banner>}
      {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      <Field id={`tax-picker-${kind}`} label={t("tax.picker.title")} error={runner.fields.classId}>
        {(a) => (
          <select {...a} value={value} disabled={!configure || runner.busy} onChange={(e) => setChoice(e.target.value)}>
            <option value="">{t("tax.picker.none")}</option>
            {options.map((c) => <option key={c.id} value={c.id}>{c.code} — {localName(c, locale)}{c.isActive ? "" : ` (${t("tax.inactive")})`}</option>)}
          </select>
        )}
      </Field>
      {configure && <button type="button" className="primary" disabled={!dirty || runner.busy} onClick={() => void save()}>{t("tax.save")}</button>}
    </div>
  );
}
