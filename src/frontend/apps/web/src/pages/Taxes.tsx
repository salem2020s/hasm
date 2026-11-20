import type { Tax, TaxClass, TaxClassesResponse, TaxPolicyResponse, TaxRule, TaxRulesResponse, TaxesResponse } from "@hasm/api-client";
import { Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { localName, useRunner } from "../ledgerCommon";
import { useReportUnsaved } from "../unsaved";

/**
 * Tax configuration (Phase 3.10 slice 3): ONE shared screen for Web and Desktop. Tabs by business meaning: the TAXES with their dated rate versions, the item and party CLASSES, the
 * application RULES, and the versioned ROUNDING policy. Everything is enforced by the server and PostgreSQL (revision fences, append-only versions, no deletion, overlap and ambiguity checks);
 * this screen only shows and submits. Nothing here ever deletes: taxes, classes and rules are deactivated.
 */
interface Data { taxes: TaxesResponse; classes: TaxClassesResponse; rules: TaxRulesResponse; policy: TaxPolicyResponse }
const TREATMENTS = [1, 2, 3, 4];
const today = () => new Date().toISOString().slice(0, 10);
const blank = (v: string) => (v.trim() === "" ? null : v.trim());

export function TaxesPage() {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [data, setData] = useState<Data | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [tab, setTab] = useState("taxes");
  const [dirty, setDirty] = useState<Record<string, boolean>>({});
  const setTabDirty = useCallback((id: string, v: boolean) => setDirty((d) => (d[id] === v ? d : { ...d, [id]: v })), []);
  const load = useCallback(async () => {
    try {
      const [taxes, classes, rules, policy] = await Promise.all([auth.api.listTaxes(), auth.api.listTaxClasses(), auth.api.listTaxRules(), auth.api.getTaxPolicy()]);
      setData({ taxes, classes, rules, policy }); setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "tax.error.")); }
  }, [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);

  if (loadError) return <Banner kind="error" testId="tax-load-error">{loadError}</Banner>;
  if (!data) return <Loading />;
  const configure = data.taxes.can.configure;
  const tabs: TabItem[] = [
    { id: "taxes", label: t("tax.tab.taxes"), dirty: dirty.taxes, content: <TaxesTab data={data} configure={configure} reload={load} onDirty={(v) => setTabDirty("taxes", v)} /> },
    { id: "classes", label: t("tax.tab.classes"), dirty: dirty.classes, content: <ClassesTab data={data} configure={configure} reload={load} onDirty={(v) => setTabDirty("classes", v)} /> },
    { id: "rules", label: t("tax.tab.rules"), dirty: dirty.rules, content: <RulesTab data={data} configure={configure} reload={load} onDirty={(v) => setTabDirty("rules", v)} /> },
    { id: "policy", label: t("tax.tab.policy"), dirty: dirty.policy, content: <PolicyTab data={data} configure={configure} reload={load} onDirty={(v) => setTabDirty("policy", v)} /> },
  ];
  return (
    <section aria-labelledby="page-title" data-testid="taxes">
      <PageHeader title={t("tax.title")} description={t("tax.subtitle")} />
      <Banner kind="info" testId="tax-note">{t("tax.note")}</Banner>
      <Card><Tabs label={t("tax.tabs_label")} tabs={tabs} value={tab} onChange={setTab} testId="tax-tabs" /></Card>
    </section>
  );
}

interface TabProps { data: Data; configure: boolean; reload: () => Promise<void>; onDirty: (v: boolean) => void }

function Notices({ runner, id }: { runner: ReturnType<typeof useRunner>; id: string }) {
  const { t } = useI18n();
  return (
    <>
      {runner.error && <Banner kind="error" testId={`${id}-error`}>{runner.error}</Banner>}
      {runner.conflict && <Banner kind="warning" testId={`${id}-conflict`}>{t("common.conflict")}</Banner>}
      {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
    </>
  );
}

function rateText(t: (k: string) => string, tax: Tax, today_: string) {
  const current = [...tax.rates].filter((r) => r.effectiveFrom <= today_).sort((a, b) => b.effectiveFrom.localeCompare(a.effectiveFrom))[0];
  if (!current) return t("tax.no_rate");
  return current.treatment === 1 ? `${current.ratePercent}%` : t(`tax.treatment.${current.treatment}`);
}

function TaxesTab({ data, configure, reload, onDirty }: TabProps) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("tax.error.", reload);
  const [form, setForm] = useState({ code: "", nameAr: "", nameEn: "" });
  const [rate, setRate] = useState({ effectiveFrom: today(), treatment: "1", ratePercent: "" });
  const [selected, setSelected] = useState("");
  const tax = data.taxes.taxes.find((x) => x.id === selected) ?? null;
  const isDirty = form.code !== "" || form.nameAr !== "" || form.nameEn !== "" || rate.ratePercent !== "";
  useReportUnsaved("tax-taxes", isDirty, t("tax.tab.taxes"));
  useEffect(() => { onDirty(isDirty); }, [isDirty]); // eslint-disable-line react-hooks/exhaustive-deps

  async function create() {
    const done = await runner.run(() => auth.api.createTax({ code: form.code, nameAr: form.nameAr, nameEn: blank(form.nameEn) }), t("tax.saved"));
    if (done) { setForm({ code: "", nameAr: "", nameEn: "" }); setSelected(done.id); }
  }
  async function addRate() {
    if (!tax) return;
    const done = await runner.run(() => auth.api.addTaxRate(tax.id, { effectiveFrom: rate.effectiveFrom, treatment: Number(rate.treatment), ratePercent: rate.treatment === "1" ? Number(rate.ratePercent) : 0 }), t("tax.saved"));
    if (done) setRate({ effectiveFrom: today(), treatment: "1", ratePercent: "" });
  }
  const toggle = (x: Tax) => void runner.run(() => auth.api.updateTax(x.id, { nameAr: x.nameAr, nameEn: x.nameEn, isActive: !x.isActive }, x.revision), t("tax.saved"));

  return (
    <div data-testid="tax-tab-taxes">
      <Notices runner={runner} id="tax" />
      {data.taxes.taxes.length === 0 ? <p data-testid="tax-empty">{t("tax.taxes_empty")}</p> : (
        <table className="table" data-testid="tax-table">
          <thead><tr><th>{t("tax.code")}</th><th>{t("tax.name_ar")}</th><th>{t("tax.current_rate")}</th><th>{t("tax.status")}</th><th>{t("tax.actions")}</th></tr></thead>
          <tbody>
            {data.taxes.taxes.map((x) => (
              <tr key={x.id} data-testid={`tax-row-${x.code}`}>
                <td><bdi dir="ltr">{x.code}</bdi></td>
                <td>{localName(x, locale)}</td>
                <td><bdi dir="ltr">{rateText(t, x, today())}</bdi></td>
                <td>{x.isActive ? t("tax.active") : t("tax.inactive")}</td>
                <td>
                  <button type="button" className="ghost" onClick={() => setSelected(x.id)}>{t("tax.rates")}</button>
                  {configure && <button type="button" className="ghost" disabled={runner.busy} onClick={() => toggle(x)}>{x.isActive ? t("tax.deactivate") : t("tax.activate")}</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {configure && (
        <fieldset disabled={runner.busy} data-testid="tax-create">
          <legend>{t("tax.create_tax")}</legend>
          <Field id="tax-code" label={t("tax.code")} required error={runner.fields.code}>{(a) => <input {...a} dir="ltr" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} />}</Field>
          <Field id="tax-name-ar" label={t("tax.name_ar")} required error={runner.fields.nameAr}>{(a) => <input {...a} value={form.nameAr} onChange={(e) => setForm({ ...form, nameAr: e.target.value })} />}</Field>
          <Field id="tax-name-en" label={t("tax.name_en")} error={runner.fields.nameEn}>{(a) => <input {...a} dir="ltr" value={form.nameEn} onChange={(e) => setForm({ ...form, nameEn: e.target.value })} />}</Field>
          <button type="button" className="primary" disabled={!form.code.trim() || !form.nameAr.trim()} onClick={() => void create()}>{t("tax.create_tax")}</button>
        </fieldset>
      )}
      <h3>{t("tax.rates")}</h3>
      {!tax ? <p>{t("tax.select_tax")}</p> : (
        <div data-testid="tax-rates">
          <p className="hint">{t("tax.rates_hint")}</p>
          {tax.rates.length === 0 ? <p>{t("tax.rates_empty")}</p> : (
            <table className="table" data-testid="tax-rates-table">
              <thead><tr><th>{t("tax.effective_from")}</th><th>{t("tax.treatment")}</th><th>{t("tax.rate_percent")}</th></tr></thead>
              <tbody>{tax.rates.map((r) => <tr key={r.effectiveFrom}><td><bdi dir="ltr">{r.effectiveFrom}</bdi></td><td>{t(`tax.treatment.${r.treatment}`)}</td><td><bdi dir="ltr">{r.ratePercent}</bdi></td></tr>)}</tbody>
            </table>
          )}
          {configure && (
            <fieldset disabled={runner.busy} data-testid="tax-add-rate">
              <legend>{t("tax.add_rate")}</legend>
              <Field id="tax-rate-from" label={t("tax.effective_from")} required error={runner.fields.effectiveFrom}>{(a) => <input {...a} type="date" value={rate.effectiveFrom} onChange={(e) => setRate({ ...rate, effectiveFrom: e.target.value })} />}</Field>
              <Field id="tax-rate-treatment" label={t("tax.treatment")} error={runner.fields.treatment}>
                {(a) => <select {...a} value={rate.treatment} onChange={(e) => setRate({ ...rate, treatment: e.target.value })}>{TREATMENTS.map((n) => <option key={n} value={n}>{t(`tax.treatment.${n}`)}</option>)}</select>}
              </Field>
              {rate.treatment === "1" && <Field id="tax-rate-percent" label={t("tax.rate_percent")} required error={runner.fields.ratePercent}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={rate.ratePercent} onChange={(e) => setRate({ ...rate, ratePercent: e.target.value })} />}</Field>}
              <button type="button" className="primary" disabled={!rate.effectiveFrom || (rate.treatment === "1" && rate.ratePercent.trim() === "")} onClick={() => void addRate()}>{t("tax.add_rate")}</button>
            </fieldset>
          )}
        </div>
      )}
    </div>
  );
}

function ClassesTab({ data, configure, reload, onDirty }: TabProps) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("tax.error.", reload);
  const [form, setForm] = useState({ kind: "1", code: "", nameAr: "", nameEn: "" });
  const isDirty = form.code !== "" || form.nameAr !== "" || form.nameEn !== "";
  useReportUnsaved("tax-classes", isDirty, t("tax.tab.classes"));
  useEffect(() => { onDirty(isDirty); }, [isDirty]); // eslint-disable-line react-hooks/exhaustive-deps

  async function create() {
    const done = await runner.run(() => auth.api.createTaxClass({ kind: Number(form.kind), code: form.code, nameAr: form.nameAr, nameEn: blank(form.nameEn) }), t("tax.saved"));
    if (done) setForm({ ...form, code: "", nameAr: "", nameEn: "" });
  }
  const toggle = (c: TaxClass) => void runner.run(() => auth.api.updateTaxClass(c.id, { kind: c.kind, nameAr: c.nameAr, nameEn: c.nameEn, isActive: !c.isActive }, c.revision), t("tax.saved"));

  return (
    <div data-testid="tax-tab-classes">
      <Notices runner={runner} id="tax-class" />
      <p className="hint">{t("tax.classes_hint")}</p>
      {data.classes.classes.length === 0 ? <p data-testid="tax-classes-empty">{t("tax.classes_empty")}</p> : (
        <table className="table" data-testid="tax-classes-table">
          <thead><tr><th>{t("tax.class_kind")}</th><th>{t("tax.code")}</th><th>{t("tax.name_ar")}</th><th>{t("tax.status")}</th><th>{t("tax.actions")}</th></tr></thead>
          <tbody>
            {data.classes.classes.map((c) => (
              <tr key={c.id} data-testid={`tax-class-${c.kind}-${c.code}`}>
                <td>{t(`tax.kind.${c.kind}`)}</td><td><bdi dir="ltr">{c.code}</bdi></td><td>{localName(c, locale)}</td><td>{c.isActive ? t("tax.active") : t("tax.inactive")}</td>
                <td>{configure && <button type="button" className="ghost" disabled={runner.busy} onClick={() => toggle(c)}>{c.isActive ? t("tax.deactivate") : t("tax.activate")}</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {configure && (
        <fieldset disabled={runner.busy} data-testid="tax-class-create">
          <legend>{t("tax.create_class")}</legend>
          <Field id="tax-class-kind" label={t("tax.class_kind")} error={runner.fields.kind}>
            {(a) => <select {...a} value={form.kind} onChange={(e) => setForm({ ...form, kind: e.target.value })}><option value="1">{t("tax.kind.1")}</option><option value="2">{t("tax.kind.2")}</option></select>}
          </Field>
          <Field id="tax-class-code" label={t("tax.code")} required error={runner.fields.code}>{(a) => <input {...a} dir="ltr" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} />}</Field>
          <Field id="tax-class-name-ar" label={t("tax.name_ar")} required error={runner.fields.nameAr}>{(a) => <input {...a} value={form.nameAr} onChange={(e) => setForm({ ...form, nameAr: e.target.value })} />}</Field>
          <Field id="tax-class-name-en" label={t("tax.name_en")} error={runner.fields.nameEn}>{(a) => <input {...a} dir="ltr" value={form.nameEn} onChange={(e) => setForm({ ...form, nameEn: e.target.value })} />}</Field>
          <button type="button" className="primary" disabled={!form.code.trim() || !form.nameAr.trim()} onClick={() => void create()}>{t("tax.create_class")}</button>
        </fieldset>
      )}
    </div>
  );
}

const ruleInput = (r: TaxRule, over: Partial<TaxRule> = {}) => ({
  documentType: r.documentType, operation: r.operation, itemClassId: r.itemClassId, partyClassId: r.partyClassId, taxId: r.taxId, priority: r.priority, includesTaxIds: r.includesTaxIds,
  validFrom: r.validFrom, validTo: r.validTo, isActive: r.isActive, treatmentOverride: r.treatmentOverride, ...over,
});

function RulesTab({ data, configure, reload, onDirty }: TabProps) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("tax.error.", reload);
  const blankForm = { documentType: "", operation: "", taxId: "", itemClassId: "", partyClassId: "", priority: "10", validFrom: today(), validTo: "", override: "", includes: [] as string[] };
  const [form, setForm] = useState(blankForm);
  const taxes = data.taxes.taxes, classes = data.classes.classes;
  const itemClasses = classes.filter((c) => c.kind === 1 && c.isActive), partyClasses = classes.filter((c) => c.kind === 2 && c.isActive);
  const taxName = (id: string) => { const x = taxes.find((y) => y.id === id); return x ? localName(x, locale) : id; };
  const className = (id: string | null) => { if (!id) return t("tax.any"); const c = classes.find((y) => y.id === id); return c ? localName(c, locale) : id; };
  const isDirty = form.documentType !== "" || form.operation !== "" || form.taxId !== "" || form.itemClassId !== "" || form.partyClassId !== "" || form.validTo !== "" || form.override !== "" || form.includes.length > 0;
  useReportUnsaved("tax-rules", isDirty, t("tax.tab.rules"));
  useEffect(() => { onDirty(isDirty); }, [isDirty]); // eslint-disable-line react-hooks/exhaustive-deps

  async function create() {
    const done = await runner.run(() => auth.api.createTaxRule({
      documentType: form.documentType.trim(), operation: form.operation.trim(), taxId: form.taxId, itemClassId: blank(form.itemClassId), partyClassId: blank(form.partyClassId),
      priority: Number(form.priority), includesTaxIds: form.includes, validFrom: form.validFrom, validTo: blank(form.validTo), isActive: true, treatmentOverride: form.override ? Number(form.override) : null,
    }), t("tax.saved"));
    if (done) setForm(blankForm);
  }
  const toggle = (r: TaxRule) => void runner.run(() => auth.api.updateTaxRule(r.id, ruleInput(r, { isActive: !r.isActive }), r.revision), t("tax.saved"));

  return (
    <div data-testid="tax-tab-rules">
      <Notices runner={runner} id="tax-rule" />
      <p className="hint">{t("tax.rules_hint")}</p>
      {data.rules.rules.length === 0 ? <p data-testid="tax-rules-empty">{t("tax.rules_empty")}</p> : (
        <table className="table" data-testid="tax-rules-table">
          <thead><tr><th>{t("tax.rule.document_type")}</th><th>{t("tax.rule.operation")}</th><th>{t("tax.rule.tax")}</th><th>{t("tax.rule.item_class")}</th><th>{t("tax.rule.party_class")}</th><th>{t("tax.rule.priority")}</th><th>{t("tax.rule.valid_from")}</th><th>{t("tax.rule.valid_to")}</th><th>{t("tax.status")}</th><th>{t("tax.actions")}</th></tr></thead>
          <tbody>
            {data.rules.rules.map((r) => (
              <tr key={r.id} data-testid={`tax-rule-${r.id}`}>
                <td><bdi dir="ltr">{r.documentType}</bdi></td><td><bdi dir="ltr">{r.operation}</bdi></td><td>{taxName(r.taxId)}{r.treatmentOverride ? ` (${t(`tax.treatment.${r.treatmentOverride}`)})` : ""}</td>
                <td>{className(r.itemClassId)}</td><td>{className(r.partyClassId)}</td><td>{r.priority}</td>
                <td><bdi dir="ltr">{r.validFrom}</bdi></td><td><bdi dir="ltr">{r.validTo ?? t("tax.rule.open_ended")}</bdi></td><td>{r.isActive ? t("tax.active") : t("tax.inactive")}</td>
                <td>{configure && <button type="button" className="ghost" disabled={runner.busy} onClick={() => toggle(r)}>{r.isActive ? t("tax.deactivate") : t("tax.activate")}</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {configure && (
        <fieldset disabled={runner.busy} data-testid="tax-rule-create">
          <legend>{t("tax.rule.create")}</legend>
          <Field id="tax-rule-doc" label={t("tax.rule.document_type")} required error={runner.fields.documentType}>{(a) => <input {...a} dir="ltr" value={form.documentType} onChange={(e) => setForm({ ...form, documentType: e.target.value })} />}</Field>
          <Field id="tax-rule-op" label={t("tax.rule.operation")} required error={runner.fields.operation}>{(a) => <input {...a} dir="ltr" value={form.operation} onChange={(e) => setForm({ ...form, operation: e.target.value })} />}</Field>
          <Field id="tax-rule-tax" label={t("tax.rule.tax")} required error={runner.fields.taxId}>
            {(a) => <select {...a} value={form.taxId} onChange={(e) => setForm({ ...form, taxId: e.target.value })}><option value="" />{taxes.filter((x) => x.isActive).map((x) => <option key={x.id} value={x.id}>{x.code} — {localName(x, locale)}</option>)}</select>}
          </Field>
          <Field id="tax-rule-item-class" label={t("tax.rule.item_class")} error={runner.fields.itemClassId}>
            {(a) => <select {...a} value={form.itemClassId} onChange={(e) => setForm({ ...form, itemClassId: e.target.value })}><option value="">{t("tax.any")}</option>{itemClasses.map((c) => <option key={c.id} value={c.id}>{localName(c, locale)}</option>)}</select>}
          </Field>
          <Field id="tax-rule-party-class" label={t("tax.rule.party_class")} error={runner.fields.partyClassId}>
            {(a) => <select {...a} value={form.partyClassId} onChange={(e) => setForm({ ...form, partyClassId: e.target.value })}><option value="">{t("tax.any")}</option>{partyClasses.map((c) => <option key={c.id} value={c.id}>{localName(c, locale)}</option>)}</select>}
          </Field>
          <Field id="tax-rule-priority" label={t("tax.rule.priority")} required error={runner.fields.priority}>{(a) => <input {...a} dir="ltr" inputMode="numeric" value={form.priority} onChange={(e) => setForm({ ...form, priority: e.target.value })} />}</Field>
          <Field id="tax-rule-from" label={t("tax.rule.valid_from")} required error={runner.fields.validFrom}>{(a) => <input {...a} type="date" value={form.validFrom} onChange={(e) => setForm({ ...form, validFrom: e.target.value })} />}</Field>
          <Field id="tax-rule-to" label={t("tax.rule.valid_to")} error={runner.fields.validTo}>{(a) => <input {...a} type="date" value={form.validTo} onChange={(e) => setForm({ ...form, validTo: e.target.value })} />}</Field>
          <Field id="tax-rule-override" label={t("tax.rule.override")} error={runner.fields.treatmentOverride}>
            {(a) => <select {...a} value={form.override} onChange={(e) => setForm({ ...form, override: e.target.value })}><option value="">{t("tax.none")}</option>{[2, 3, 4].map((n) => <option key={n} value={n}>{t(`tax.treatment.${n}`)}</option>)}</select>}
          </Field>
          <fieldset>
            <legend>{t("tax.rule.includes")}</legend>
            {taxes.filter((x) => x.id !== form.taxId).map((x) => (
              <div className="field" key={x.id}><label><input type="checkbox" checked={form.includes.includes(x.id)} onChange={(e) => setForm({ ...form, includes: e.target.checked ? [...form.includes, x.id] : form.includes.filter((i) => i !== x.id) })} /> {x.code} — {localName(x, locale)}</label></div>
            ))}
          </fieldset>
          <button type="button" className="primary" disabled={!form.documentType.trim() || !form.operation.trim() || !form.taxId || !form.validFrom || form.priority.trim() === ""} onClick={() => void create()}>{t("tax.rule.create")}</button>
        </fieldset>
      )}
    </div>
  );
}

function PolicyTab({ data, configure, reload, onDirty }: TabProps) {
  const { t } = useI18n();
  const auth = useAuth();
  const runner = useRunner("tax.error.", reload);
  const p = data.policy.policy;
  const [form, setForm] = useState({ decimals: String(p.moneyDecimals ?? 2), mode: String(p.mode ?? 1), level: String(p.level ?? 1) });
  const isDirty = p.configured ? form.decimals !== String(p.moneyDecimals) || form.mode !== String(p.mode) || form.level !== String(p.level) : false;
  useReportUnsaved("tax-policy", isDirty, t("tax.tab.policy"));
  useEffect(() => { onDirty(isDirty); }, [isDirty]); // eslint-disable-line react-hooks/exhaustive-deps

  async function add() {
    await runner.run(() => auth.api.addTaxPolicy({ moneyDecimals: Number(form.decimals), mode: Number(form.mode), level: Number(form.level) }), t("tax.saved"));
  }
  return (
    <div data-testid="tax-tab-policy">
      <Notices runner={runner} id="tax-policy" />
      <p className="hint">{t("tax.policy.hint")}</p>
      {!p.configured ? <p data-testid="tax-policy-none">{t("tax.policy.not_configured")}</p> : (
        <div data-testid="tax-policy-current">
          <h3>{t("tax.policy.current")} — {t("tax.policy.version", { n: String(p.version) })}</h3>
          <p>{t("tax.policy.decimals")}: <bdi dir="ltr">{p.moneyDecimals}</bdi> · {t("tax.policy.mode")}: {t(`tax.policy.mode.${p.mode}`)} · {t("tax.policy.level")}: {t(`tax.policy.level.${p.level}`)}</p>
        </div>
      )}
      {configure && (
        <fieldset disabled={runner.busy} data-testid="tax-policy-form">
          <legend>{t("tax.policy.add")}</legend>
          <Field id="tax-policy-decimals" label={t("tax.policy.decimals")} error={runner.fields.moneyDecimals}>{(a) => <input {...a} dir="ltr" inputMode="numeric" value={form.decimals} onChange={(e) => setForm({ ...form, decimals: e.target.value })} />}</Field>
          <Field id="tax-policy-mode" label={t("tax.policy.mode")} error={runner.fields.mode}>
            {(a) => <select {...a} value={form.mode} onChange={(e) => setForm({ ...form, mode: e.target.value })}><option value="1">{t("tax.policy.mode.1")}</option><option value="2">{t("tax.policy.mode.2")}</option></select>}
          </Field>
          <Field id="tax-policy-level" label={t("tax.policy.level")} error={runner.fields.level}>
            {(a) => <select {...a} value={form.level} onChange={(e) => setForm({ ...form, level: e.target.value })}><option value="1">{t("tax.policy.level.1")}</option><option value="2">{t("tax.policy.level.2")}</option></select>}
          </Field>
          <button type="button" className="primary" disabled={form.decimals.trim() === ""} onClick={() => void add()}>{t("tax.policy.add")}</button>
        </fieldset>
      )}
    </div>
  );
}
