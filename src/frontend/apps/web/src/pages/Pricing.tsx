import type { CustomerGroup, CustomerGroupAssignment, DiscountRule, DiscountRulesResponse, InventoryGroup, InventoryItem, InventoryUnit, PartyRole, PriceEntry, PriceList, PriceListsResponse, PricingPolicyResponse, PricingQuote } from "@hasm/api-client";
import { SearchPicker, Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { localName, useRunner } from "../ledgerCommon";
import { parseDecimalInput, parseSignedDecimalInput } from "../numberInput";
import { useReportUnsaved } from "../unsaved";

/**
 * Pricing administration (Phase 3.11 slice 2c): ONE shared screen for Web and Desktop. Tabs by business meaning: the company POLICY (scales, margin control and the configurable precedence of price lists and
 * discounts), the PRICE LISTS with their entries, the DISCOUNT rules, and a QUOTE preview that shows exactly what the engine would price and seal. Everything is enforced by the server and PostgreSQL
 * (revision fences, the policy version fence, append-only history, no deletion, ambiguity and customer-role checks); this screen only shows and submits. Nothing here ever deletes: lists, entries and rules are deactivated.
 * Customer GROUPS (migration 0027): a tab manages the group master and the one-group-per-customer assignment; a price list or discount rule is scoped to a customer OR a group (the two selects exclude each other).
 */
interface Pickers { units: InventoryUnit[]; groups: InventoryGroup[]; items: InventoryItem[]; customers: PartyRole[]; customerGroups: CustomerGroup[]; assignments: CustomerGroupAssignment[]; baseCurrency: string; currencyFailed: boolean; failed: boolean }
const PICKER_LIMIT = 500;
interface Data { policy: PricingPolicyResponse; lists: PriceListsResponse; discounts: DiscountRulesResponse; pickers: Pickers }
const pad = (n: number) => String(n).padStart(2, "0");
const today = () => { const d = new Date(); return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`; };   // the local calendar day, not UTC
const num = parseDecimalInput;
const snum = parseSignedDecimalInput;
const sbad = (s: string) => s.trim() !== "" && Number.isNaN(snum(s));
const bad = (s: string) => s.trim() !== "" && Number.isNaN(num(s));
const blank = (v: string) => (v.trim() === "" ? null : v.trim());

export function PricingPage() {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [data, setData] = useState<Data | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [tab, setTab] = useState("policy");
  const [dirty, setDirty] = useState<Record<string, boolean>>({});
  const setTabDirty = useCallback((id: string, v: boolean) => setDirty((d) => (d[id] === v ? d : { ...d, [id]: v })), []);
  const load = useCallback(async () => {
    try {
      const [policy, lists, discounts] = await Promise.all([auth.api.getPricingPolicy(), auth.api.listPriceLists(), auth.api.listDiscountRules()]);
      // pickers need the inventory and party-role view permissions: a user without them still sees the configuration (the pickers are then empty)
      let failed = false;
      let currencyFailed = false;
      const soft = <T,>(p: Promise<T[]>) => p.catch((e) => { if (!auth.handleError(e)) failed = true; return [] as T[]; });
      const [units, groups, items, customers, customerGroups, assignments, baseCurrency] = await Promise.all([
        soft(auth.api.listInventoryUnits().then((r) => r.units)), soft(auth.api.listInventoryGroups().then((r) => r.groups)),
        soft(auth.api.listInventoryItems({ limit: PICKER_LIMIT }).then((r) => r.items)), soft(auth.api.listPartyRoles(1).then((r) => r.items)),
        soft(auth.api.listCustomerGroups().then((r) => r.customerGroups)), soft(auth.api.listCustomerGroupAssignments().then((r) => r.assignments)),
        // the default currency of a new list/quote is the company base currency when the user may read it; otherwise it stays empty (never guessed). Not counted as a failed helper list: it needs its own permission.
        auth.api.getCurrencies().then((r) => r.baseCurrency ?? "").catch((e) => { if (!auth.handleError(e)) currencyFailed = true; return ""; }),
      ]);
      setData({ policy, lists, discounts, pickers: { units, groups, items, customers, customerGroups, assignments, baseCurrency, currencyFailed, failed } }); setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "pricing.error.")); }
  }, [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);

  if (!data) return loadError ? <Banner kind="error" testId="pricing-load-error">{loadError}</Banner> : <Loading />;
  const configure = data.policy.can.configure;
  const tabs: TabItem[] = [
    { id: "policy", label: t("pricing.tab.policy"), dirty: dirty.policy, content: <PolicyTab data={data} configure={configure} reload={load} onDirty={(v) => setTabDirty("policy", v)} /> },
    { id: "lists", label: t("pricing.tab.lists"), dirty: dirty.lists, content: <ListsTab data={data} configure={configure} reload={load} onDirty={(v) => setTabDirty("lists", v)} /> },
    { id: "discounts", label: t("pricing.tab.discounts"), dirty: dirty.discounts, content: <DiscountsTab data={data} configure={configure} reload={load} onDirty={(v) => setTabDirty("discounts", v)} /> },
    { id: "groups", label: t("pricing.tab.groups"), dirty: dirty.groups, content: <GroupsTab data={data} configure={configure} reload={load} onDirty={(v) => setTabDirty("groups", v)} /> },
    { id: "quote", label: t("pricing.tab.quote"), dirty: dirty.quote, content: <QuoteTab data={data} onDirty={(v) => setTabDirty("quote", v)} /> },
  ];
  return (
    <section aria-labelledby="page-title" data-testid="pricing">
      <PageHeader title={t("pricing.title")} description={t("pricing.subtitle")} />
      {loadError && <Banner kind="error" testId="pricing-load-error">{loadError}</Banner>}
      <Banner kind="info" testId="pricing-note">{t("pricing.note")}</Banner>
      {data.pickers.failed && <Banner kind="warning" testId="pricing-pickers-failed">{t("pricing.pickers_unavailable")}</Banner>}
      {data.pickers.currencyFailed && <Banner kind="warning" testId="pricing-currency-unavailable">{t("pricing.currency_unavailable")}</Banner>}
      {data.pickers.items.length >= PICKER_LIMIT && <Banner kind="warning" testId="pricing-pickers-truncated">{t("pricing.pickers_truncated", { n: String(PICKER_LIMIT) })}</Banner>}
      <Card><Tabs label={t("pricing.tabs_label")} tabs={tabs} value={tab} onChange={setTab} testId="pricing-tabs" /></Card>
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

const customerName = (p: Pickers, id: string | null, none: string) => (id ? p.customers.find((c) => c.partyId === id)?.partyCode ?? id : none);

function CustomerSelect({ id, value, onChange, pickers, error, disabled }: { id: string; value: string; onChange: (v: string) => void; pickers: Pickers; error?: string; disabled?: boolean }) {
  const { t } = useI18n();
  return (
    <Field id={id} label={t("pricing.customer")} hint={t("pricing.customer_hint")} error={error}>
      {(a) => <select {...a} disabled={disabled} value={value} onChange={(e) => onChange(e.target.value)}><option value="">{t("pricing.all_customers")}</option>{pickers.customers.map((c) => <option key={c.partyId} value={c.partyId}>{codeName(c.partyCode, c.partyName)}</option>)}</select>}
    </Field>
  );
}

/** "code — name" with each part isolated (FSI...PDI), so a Latin code next to an Arabic name (or the reverse) keeps its own direction in option labels and tables. */
const codeName = (code: string, name: string) => `\u2068${code}\u2069 — \u2068${name}\u2069`;
const itemLabel = (i: InventoryItem, locale: "ar" | "en") => codeName(i.code, localName(i, locale));
const shortId = (id: string) => id.slice(0, 8);

/** Names of the items an entry/discount table mentions: the preloaded page, items picked on this screen, and (for the rest) ONE `getInventoryItem` call each; a short id is shown while unknown or when the item cannot be read. */
function useItemNames(preloaded: InventoryItem[], ids: (string | null | undefined)[]) {
  const auth = useAuth();
  const [extra, setExtra] = useState<Record<string, InventoryItem>>({});
  const tried = useRef(new Set<string>());
  const known = useMemo(() => new Map(preloaded.map((i) => [i.id, i])), [preloaded]);
  const key = ids.filter(Boolean).join(",");
  useEffect(() => {
    const missing = [...new Set(key === "" ? [] : key.split(","))].filter((id) => !known.has(id) && !tried.current.has(id)).slice(0, 50);
    for (const id of missing) {
      tried.current.add(id);
      auth.api.getInventoryItem(id).then((i) => setExtra((e) => ({ ...e, [i.id]: i })), (e) => { auth.handleError(e); });
    }
  }, [key, known]); // eslint-disable-line react-hooks/exhaustive-deps
  const find = (id: string) => known.get(id) ?? extra[id];
  return {
    /** Remember an item the user picked here, so its name survives a reload that returns more than the preloaded page. */
    remember: (i: InventoryItem | null) => { if (i) { tried.current.add(i.id); setExtra((e) => ({ ...e, [i.id]: i })); } },
    label: (id: string, locale: "ar" | "en") => { const i = find(id); return i ? itemLabel(i, locale) : shortId(id); },
    code: (id: string) => find(id)?.code ?? shortId(id),
  };
}

/** Item picker: a bounded server-side SEARCH (shared SearchPicker) instead of a silently truncated list; it says when more items match than are shown. */
function ItemPicker({ id, label, value, onChange, error }: { id: string; label: string; value: string; onChange: (id: string, item: InventoryItem | null) => void; error?: string }) {
  const { locale } = useI18n();
  const auth = useAuth();
  const load = useCallback((search: string) => auth.api.listInventoryItems({ search: search === "" ? undefined : search, limit: PICKER_LIMIT }).then((r) => ({ items: r.items, total: r.total })).catch((e) => { auth.handleError(e); throw e; }), [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <div className={`field${error ? " field-invalid" : ""}`} data-testid={`${id}-picker`}>
      <label htmlFor={`${id}-list`}>{label}<span aria-hidden="true" className="required"> *</span></label>
      <SearchPicker id={`${id}-list`} label={label} value={value} onChange={onChange} load={load} getId={(i: InventoryItem) => i.id} getLabel={(i: InventoryItem) => itemLabel(i, locale)}
        invalid={!!error} describedBy={error ? `${id}-error` : undefined} required />
      {error && <p id={`${id}-error`} className="field-error">{error}</p>}
    </div>
  );
}

function PolicyTab({ data, configure, reload, onDirty }: TabProps) {
  const { t } = useI18n();
  const auth = useAuth();
  const runner = useRunner("pricing.error.", reload);
  const p = data.policy.policy;
  const initial = () => ({ priceDecimals: String(p.priceDecimals), moneyDecimals: String(p.moneyDecimals), margin: p.minMarginPercent === null ? "" : String(p.minMarginPercent), action: String(p.marginAction), list: String(p.listPrecedence), discount: String(p.discountPrecedence) });
  const [form, setForm] = useState(initial);
  const base = initial();
  const isDirty = (Object.keys(base) as (keyof typeof base)[]).some((k) => base[k] !== form[k]);
  useReportUnsaved("pricing-policy", isDirty, t("pricing.tab.policy"));
  useEffect(() => { onDirty(isDirty); }, [isDirty]); // eslint-disable-line react-hooks/exhaustive-deps
  const valid = Number.isInteger(num(form.priceDecimals)) && Number.isInteger(num(form.moneyDecimals)) && !sbad(form.margin);

  async function save() {
    await runner.run(() => auth.api.savePricingPolicy({ priceDecimals: num(form.priceDecimals), moneyDecimals: num(form.moneyDecimals), minMarginPercent: form.margin.trim() === "" ? null : snum(form.margin),
      marginAction: Number(form.action), listPrecedence: Number(form.list), discountPrecedence: Number(form.discount) }, p.version), t("pricing.saved"));
  }
  return (
    <div data-testid="pricing-tab-policy">
      <Notices runner={runner} id="pricing-policy" />
      <p className="hint">{t("pricing.policy.hint")}</p>
      <h3>{p.isDefault ? t("pricing.policy.default") : t("pricing.policy.version", { n: String(p.version) })}</h3>
      <p data-testid="pricing-policy-current">
        {t("pricing.policy.price_decimals")}: <bdi dir="ltr">{p.priceDecimals}</bdi> · {t("pricing.policy.money_decimals")}: <bdi dir="ltr">{p.moneyDecimals}</bdi> · {t("pricing.policy.min_margin")}: <bdi dir="ltr">{p.minMarginPercent ?? t("pricing.none")}</bdi> ·
        {" "}{t("pricing.policy.margin_action")}: {t(`pricing.margin_action.${p.marginAction}`)} · {t("pricing.policy.list_precedence")}: {t(`pricing.list_precedence.${p.listPrecedence}`)} · {t("pricing.policy.discount_precedence")}: {t(`pricing.discount_precedence.${p.discountPrecedence}`)}
      </p>
      {configure && (
        <fieldset disabled={runner.busy} data-testid="pricing-policy-form">
          <legend>{t("pricing.policy.save")}</legend>
          <Field id="pp-price-decimals" label={t("pricing.policy.price_decimals")} error={bad(form.priceDecimals) ? t("field.invalid") : runner.fields.priceDecimals}>{(a) => <input {...a} dir="ltr" inputMode="numeric" value={form.priceDecimals} onChange={(e) => setForm({ ...form, priceDecimals: e.target.value })} />}</Field>
          <Field id="pp-money-decimals" label={t("pricing.policy.money_decimals")} error={bad(form.moneyDecimals) ? t("field.invalid") : runner.fields.moneyDecimals}>{(a) => <input {...a} dir="ltr" inputMode="numeric" value={form.moneyDecimals} onChange={(e) => setForm({ ...form, moneyDecimals: e.target.value })} />}</Field>
          <Field id="pp-margin" label={t("pricing.policy.min_margin")} hint={t("pricing.policy.min_margin_hint")} error={sbad(form.margin) ? t("field.invalid") : runner.fields.minMarginPercent}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={form.margin} onChange={(e) => setForm({ ...form, margin: e.target.value })} />}</Field>
          <Field id="pp-action" label={t("pricing.policy.margin_action")} error={runner.fields.marginAction}>
            {(a) => <select {...a} value={form.action} onChange={(e) => setForm({ ...form, action: e.target.value })}>{[1, 2, 3].map((n) => <option key={n} value={n}>{t(`pricing.margin_action.${n}`)}</option>)}</select>}
          </Field>
          <Field id="pp-list" label={t("pricing.policy.list_precedence")} hint={t("pricing.policy.precedence_hint")} error={runner.fields.listPrecedence}>
            {(a) => <select {...a} value={form.list} onChange={(e) => setForm({ ...form, list: e.target.value })}>{[1, 2].map((n) => <option key={n} value={n}>{t(`pricing.list_precedence.${n}`)}</option>)}</select>}
          </Field>
          <Field id="pp-discount" label={t("pricing.policy.discount_precedence")} hint={t("pricing.policy.no_stacking")} error={runner.fields.discountPrecedence}>
            {(a) => <select {...a} value={form.discount} onChange={(e) => setForm({ ...form, discount: e.target.value })}>{[1, 2].map((n) => <option key={n} value={n}>{t(`pricing.discount_precedence.${n}`)}</option>)}</select>}
          </Field>
          <button type="button" className="primary" disabled={!valid} onClick={() => void save()}>{t("pricing.policy.save")}</button>
        </fieldset>
      )}
    </div>
  );
}

const asInput = (l: PriceList, isActive: boolean) => ({ nameAr: l.nameAr, nameEn: l.nameEn, priority: l.priority, validFrom: l.validFrom, validTo: l.validTo, isActive });

/** The assignment list carries ids only (names are customer data of the party-role permission): the name is joined from the party-role list when the user may read it, otherwise the id is shown. */
const partyLabel = (p: Pickers, id: string) => { const c = p.customers.find((x) => x.partyId === id); return c ? c.partyCode : id.slice(0, 8); };
/** Code plus name when the party-role list provides it; ids stay short codes otherwise. */
/** Rows with a known customer code first (numeric-aware code order), then rows shown by short id, ordered by the full id: deterministic whatever the server order. */
const byCodeThenId = (p: Pickers) => (x: CustomerGroupAssignment, y: CustomerGroupAssignment) => {
  const cx = p.customers.find((c) => c.partyId === x.partyId)?.partyCode, cy = p.customers.find((c) => c.partyId === y.partyId)?.partyCode;
  if (cx !== undefined && cy === undefined) return -1;
  if (cx === undefined && cy !== undefined) return 1;
  return (cx !== undefined && cy !== undefined ? cx.localeCompare(cy, "en", { numeric: true }) : 0) || (x.partyId < y.partyId ? -1 : x.partyId > y.partyId ? 1 : 0);
};
const partyText = (p: Pickers, id: string) => { const c = p.customers.find((x) => x.partyId === id); return c ? codeName(c.partyCode, c.partyName) : id.slice(0, 8); };
const groupName = (p: Pickers, id: string | null | undefined, none: string) => (id ? p.customerGroups.find((g) => g.id === id)?.code ?? id : none);

/** A customer OR a customer group (never both): choosing one disables the other; the server and PostgreSQL enforce it too. */
function CustomerGroupSelect({ id, value, onChange, pickers, error, disabled }: { id: string; value: string; onChange: (v: string) => void; pickers: Pickers; error?: string; disabled?: boolean }) {
  const { t, locale } = useI18n();
  return (
    <Field id={id} label={t("pricing.customer_group")} hint={t("pricing.customer_group_hint")} error={error}>
      {(a) => <select {...a} disabled={disabled} value={value} onChange={(e) => onChange(e.target.value)}><option value="">{t("pricing.all_groups")}</option>{pickers.customerGroups.filter((g) => g.isActive).map((g) => <option key={g.id} value={g.id}>{codeName(g.code, localName(g, locale))}</option>)}</select>}
    </Field>
  );
}

function GroupsTab({ data, configure, reload, onDirty }: TabProps) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("pricing.error.", reload);
  const [form, setForm] = useState({ code: "", nameAr: "", nameEn: "" });
  const [editing, setEditing] = useState<{ id: string; nameAr: string; nameEn: string } | null>(null);
  const [picked, setPicked] = useState<Record<string, string>>({});
  const isDirty = form.code !== "" || form.nameAr !== "" || form.nameEn !== "" || editing !== null || Object.keys(picked).length > 0;
  useReportUnsaved("pricing-groups", isDirty, t("pricing.tab.groups"));
  useEffect(() => { onDirty(isDirty); }, [isDirty]); // eslint-disable-line react-hooks/exhaustive-deps
  async function create() {
    const done = await runner.run(() => auth.api.createCustomerGroup({ code: form.code, nameAr: form.nameAr, nameEn: blank(form.nameEn) }), t("pricing.saved"));
    if (done) setForm({ code: "", nameAr: "", nameEn: "" });
  }
  async function saveNames() {
    if (!editing) return;
    const g = data.pickers.customerGroups.find((x) => x.id === editing.id);
    if (!g) return;
    const done = await runner.run(() => auth.api.updateCustomerGroup(g.id, { nameAr: editing.nameAr, nameEn: blank(editing.nameEn), isActive: g.isActive }, g.revision), t("pricing.saved"));
    if (done) setEditing(null);
  }
  const toggle = (g: CustomerGroup) => void runner.run(() => auth.api.updateCustomerGroup(g.id, { nameAr: g.nameAr, nameEn: g.nameEn, isActive: !g.isActive }, g.revision), t("pricing.saved"));
  async function assign(a: CustomerGroupAssignment) {
    const value = picked[a.partyId] ?? (a.customerGroupId ?? "");
    const done = await runner.run(() => auth.api.setCustomerGroupAssignment(a.partyId, value === "" ? null : value, a.revision), t("pricing.saved"));
    if (done) setPicked(({ [a.partyId]: _drop, ...rest }) => rest);
  }
  return (
    <div data-testid="pricing-tab-groups">
      <Notices runner={runner} id="pricing-groups" />
      <p className="hint">{t("pricing.groups.hint")}</p>
      <Banner kind="info" testId="pricing-groups-deactivate-note">{t("pricing.groups.deactivate_note")}</Banner>
      {data.pickers.customerGroups.length === 0 ? <p data-testid="pricing-groups-empty">{t("pricing.groups.empty")}</p> : (
        <table className="table" data-testid="pricing-groups-table" aria-label={t("pricing.groups.list_label")}>
          <thead><tr><th>{t("pricing.code")}</th><th>{t("pricing.name_ar")}</th><th>{t("pricing.name_en")}</th><th>{t("pricing.status")}</th><th>{t("pricing.actions")}</th></tr></thead>
          <tbody>
            {data.pickers.customerGroups.map((g) => (
              <tr key={g.id} data-testid={`pricing-group-${g.code}`}>
                <td><bdi dir="ltr">{g.code}</bdi></td>
                <td>{editing?.id === g.id ? <input aria-label={t("pricing.name_ar")} value={editing.nameAr} onChange={(e) => setEditing({ ...editing, nameAr: e.target.value })} /> : <bdi dir="auto">{g.nameAr}</bdi>}</td>
                <td>{editing?.id === g.id ? <input aria-label={t("pricing.name_en")} dir="ltr" value={editing.nameEn} onChange={(e) => setEditing({ ...editing, nameEn: e.target.value })} /> : <bdi dir="ltr">{g.nameEn ?? ""}</bdi>}</td>
                <td>{g.isActive ? t("pricing.active") : t("pricing.inactive")}</td>
                <td>{configure && (editing?.id === g.id
                  ? <><button type="button" className="primary" disabled={runner.busy || !editing.nameAr.trim()} onClick={() => void saveNames()}>{t("pricing.save")}</button><button type="button" className="ghost" onClick={() => setEditing(null)}>{t("pricing.cancel")}</button></>
                  : <><button type="button" className="ghost" onClick={() => setEditing({ id: g.id, nameAr: g.nameAr, nameEn: g.nameEn ?? "" })}>{t("pricing.groups.edit")}</button><button type="button" className="ghost" disabled={runner.busy} onClick={() => toggle(g)}>{g.isActive ? t("pricing.deactivate") : t("pricing.activate")}</button></>)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {configure && (
        <fieldset disabled={runner.busy} data-testid="pricing-group-create">
          <legend>{t("pricing.groups.create")}</legend>
          <Field id="cg-code" label={t("pricing.code")} required error={runner.fields.code}>{(a) => <input {...a} dir="ltr" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} />}</Field>
          <Field id="cg-name-ar" label={t("pricing.name_ar")} required error={runner.fields.nameAr}>{(a) => <input {...a} value={form.nameAr} onChange={(e) => setForm({ ...form, nameAr: e.target.value })} />}</Field>
          <Field id="cg-name-en" label={t("pricing.name_en")} error={runner.fields.nameEn}>{(a) => <input {...a} dir="ltr" value={form.nameEn} onChange={(e) => setForm({ ...form, nameEn: e.target.value })} />}</Field>
          <button type="button" className="primary" disabled={!form.code.trim() || !form.nameAr.trim()} onClick={() => void create()}>{t("pricing.groups.create")}</button>
        </fieldset>
      )}
      <h3>{t("pricing.groups.assignments")}</h3>
      <p className="hint">{t("pricing.groups.assignments_hint")}</p>
      {data.pickers.assignments.length === 0 ? <p data-testid="pricing-assignments-empty">{t("pricing.groups.assignments_empty")}</p> : (
        <table className="table" data-testid="pricing-assignments-table">
          <thead><tr><th>{t("pricing.groups.party")}</th><th>{t("pricing.groups.group")}</th><th>{t("pricing.actions")}</th></tr></thead>
          <tbody>
            {[...data.pickers.assignments].sort(byCodeThenId(data.pickers)).map((a) => {
              const value = picked[a.partyId] ?? (a.customerGroupId ?? "");
              return (
                <tr key={a.partyId} data-testid={`pricing-assignment-${partyLabel(data.pickers, a.partyId)}`}>
                  <td><bdi dir="auto">{partyText(data.pickers, a.partyId)}</bdi></td>
                  <td>{configure
                    ? <select aria-label={`${t("pricing.groups.group")} ${partyLabel(data.pickers, a.partyId)}`} value={value} disabled={runner.busy} onChange={(e) => setPicked({ ...picked, [a.partyId]: e.target.value })}>
                        <option value="">{t("pricing.groups.none")}</option>
                        {data.pickers.customerGroups.filter((g) => g.isActive || g.id === a.customerGroupId).map((g) => <option key={g.id} value={g.id}>{codeName(g.code, localName(g, locale))}</option>)}
                      </select>
                    : <bdi dir="ltr">{a.customerGroupCode ?? t("pricing.groups.none")}</bdi>}</td>
                  <td>{configure && <button type="button" className="ghost" disabled={runner.busy || picked[a.partyId] === undefined || picked[a.partyId] === (a.customerGroupId ?? "")} onClick={() => void assign(a)}>{t("pricing.groups.assign")}</button>}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}

function ListsTab({ data, configure, reload, onDirty }: TabProps) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("pricing.error.", reload);
  const [form, setForm] = useState({ code: "", nameAr: "", nameEn: "", priority: "10", currency: data.pickers.baseCurrency, validFrom: today(), validTo: "", customerId: "", customerGroupId: "" });
  const [selected, setSelected] = useState("");
  const [entries, setEntries] = useState<PriceEntry[]>([]);
  const [entry, setEntry] = useState({ kind: "item", targetId: "", unitId: "", minQuantity: "0", unitPrice: "" });
  const [repricing, setRepricing] = useState<{ id: string; price: string } | null>(null);
  const names = useItemNames(data.pickers.items, entries.map((e) => e.itemId));
  const list = data.lists.priceLists.find((x) => x.id === selected) ?? null;
  const isDirty = form.code !== "" || form.nameAr !== "" || form.nameEn !== "" || entry.unitPrice !== "" || repricing !== null;
  useReportUnsaved("pricing-lists", isDirty, t("pricing.tab.lists"));
  useEffect(() => { onDirty(isDirty); }, [isDirty]); // eslint-disable-line react-hooks/exhaustive-deps

  const loadEntries = useCallback(async (id: string) => {
    if (!id) { setEntries([]); return; }
    try { setEntries((await auth.api.listPriceEntries(id)).entries); } catch (e) { if (!auth.handleError(e)) setEntries([]); }
  }, [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void loadEntries(selected); setRepricing(null); }, [selected, loadEntries]);   // a half-edited price belongs to the list it was opened on
  const refreshEntries = async () => { await reload(); await loadEntries(selected); };
  const entryRunner = useRunner("pricing.error.", refreshEntries);

  const targetName = (e: PriceEntry) => {
    if (e.itemId) return names.label(e.itemId, locale);
    const g = data.pickers.groups.find((x) => x.id === e.itemGroupId); return g ? codeName(g.code, localName(g, locale)) : (e.itemGroupId ? shortId(e.itemGroupId) : "");
  };
  const unitName = (id: string) => data.pickers.units.find((u) => u.id === id)?.code ?? id;

  async function create() {
    const done = await runner.run(() => auth.api.createPriceList({ code: form.code, nameAr: form.nameAr, nameEn: blank(form.nameEn), priority: num(form.priority), currency: form.currency, validFrom: form.validFrom, validTo: blank(form.validTo), customerId: blank(form.customerId), customerGroupId: blank(form.customerGroupId) }), t("pricing.saved"));
    if (done) { setForm({ ...form, code: "", nameAr: "", nameEn: "", validTo: "" }); setSelected(done.id); }
  }
  const toggle = (l: PriceList) => void runner.run(() => auth.api.updatePriceList(l.id, asInput(l, !l.isActive), l.revision), t("pricing.saved"));
  async function addEntry() {
    if (!list) return;
    const done = await entryRunner.run(() => auth.api.createPriceEntry(list.id, { itemId: entry.kind === "item" ? entry.targetId : null, itemGroupId: entry.kind === "group" ? entry.targetId : null, unitId: entry.unitId, minQuantity: num(entry.minQuantity), unitPrice: num(entry.unitPrice) }), t("pricing.saved"));
    if (done) setEntry({ ...entry, unitPrice: "" });
  }
  async function reprice() {
    if (!repricing) return;
    const e = entries.find((x) => x.id === repricing.id);
    if (!e) return;
    const done = await entryRunner.run(() => auth.api.updatePriceEntry(e.id, { unitPrice: num(repricing.price), isActive: e.isActive }, e.revision), t("pricing.saved"));
    if (done) setRepricing(null);
  }
  const toggleEntry = (e: PriceEntry) => void entryRunner.run(() => auth.api.updatePriceEntry(e.id, { unitPrice: e.unitPrice, isActive: !e.isActive }, e.revision), t("pricing.saved"));
  const targets = data.pickers.groups.map((g) => ({ id: g.id, label: codeName(g.code, localName(g, locale)) }));

  return (
    <div data-testid="pricing-tab-lists">
      <Notices runner={runner} id="pricing-lists" />
      {data.lists.priceLists.length === 0 ? <p data-testid="pricing-lists-empty">{t("pricing.lists_empty")}</p> : (
        <table className="table" data-testid="pricing-lists-table">
          <thead><tr><th>{t("pricing.code")}</th><th>{t("pricing.name_ar")}</th><th>{t("pricing.priority")}</th><th>{t("pricing.currency")}</th><th>{t("pricing.customer")}</th><th>{t("pricing.customer_group")}</th><th>{t("pricing.valid")}</th><th>{t("pricing.status")}</th><th>{t("pricing.actions")}</th></tr></thead>
          <tbody>
            {data.lists.priceLists.map((l) => (
              <tr key={l.id} data-testid={`pricing-list-${l.code}`}>
                <td><bdi dir="ltr">{l.code}</bdi></td><td>{localName(l, locale)}</td><td><bdi dir="ltr">{l.priority}</bdi></td><td><bdi dir="ltr">{l.currency}</bdi></td>
                <td><bdi dir="ltr">{customerName(data.pickers, l.customerId, t("pricing.all_customers"))}</bdi></td>
                <td><bdi dir="ltr">{groupName(data.pickers, l.customerGroupId, t("pricing.all_groups"))}</bdi></td>
                <td><bdi dir="ltr">{l.validFrom} → {l.validTo ?? t("pricing.open_ended")}</bdi></td>
                <td>{l.isActive ? t("pricing.active") : t("pricing.inactive")}</td>
                <td>
                  <button type="button" className="ghost" onClick={() => setSelected(l.id)}>{t("pricing.entries")}</button>
                  {configure && <button type="button" className="ghost" disabled={runner.busy} onClick={() => toggle(l)}>{l.isActive ? t("pricing.deactivate") : t("pricing.activate")}</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {configure && (
        <fieldset disabled={runner.busy} data-testid="pricing-list-create">
          <legend>{t("pricing.create_list")}</legend>
          <Field id="pl-code" label={t("pricing.code")} required error={runner.fields.code}>{(a) => <input {...a} dir="ltr" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} />}</Field>
          <Field id="pl-name-ar" label={t("pricing.name_ar")} required error={runner.fields.nameAr}>{(a) => <input {...a} value={form.nameAr} onChange={(e) => setForm({ ...form, nameAr: e.target.value })} />}</Field>
          <Field id="pl-name-en" label={t("pricing.name_en")} error={runner.fields.nameEn}>{(a) => <input {...a} dir="ltr" value={form.nameEn} onChange={(e) => setForm({ ...form, nameEn: e.target.value })} />}</Field>
          <Field id="pl-priority" label={t("pricing.priority")} hint={t("pricing.priority_hint")} error={bad(form.priority) ? t("field.invalid") : runner.fields.priority}>{(a) => <input {...a} dir="ltr" inputMode="numeric" value={form.priority} onChange={(e) => setForm({ ...form, priority: e.target.value })} />}</Field>
          <Field id="pl-currency" label={t("pricing.currency")} required error={runner.fields.currency}>{(a) => <input {...a} dir="ltr" maxLength={3} value={form.currency} onChange={(e) => setForm({ ...form, currency: e.target.value.toUpperCase() })} />}</Field>
          <Field id="pl-from" label={t("pricing.valid_from")} required error={runner.fields.validFrom}>{(a) => <input {...a} type="date" value={form.validFrom} onChange={(e) => setForm({ ...form, validFrom: e.target.value })} />}</Field>
          <Field id="pl-to" label={t("pricing.valid_to")} error={runner.fields.validTo}>{(a) => <input {...a} type="date" value={form.validTo} onChange={(e) => setForm({ ...form, validTo: e.target.value })} />}</Field>
          <CustomerSelect id="pl-customer" value={form.customerId} onChange={(v) => setForm({ ...form, customerId: v })} pickers={data.pickers} error={runner.fields.customerId} disabled={form.customerGroupId !== ""} />
          <CustomerGroupSelect id="pl-group" value={form.customerGroupId} onChange={(v) => setForm({ ...form, customerGroupId: v })} pickers={data.pickers} error={runner.fields.customerGroupId} disabled={form.customerId !== ""} />
          <button type="button" className="primary" disabled={!form.code.trim() || !form.nameAr.trim() || !form.validFrom || form.currency.trim().length !== 3 || Number.isNaN(num(form.priority))} onClick={() => void create()}>{t("pricing.create_list")}</button>
        </fieldset>
      )}
      <h3>{t("pricing.entries")}</h3>
      {!list ? <p>{t("pricing.select_list")}</p> : (
        <div data-testid="pricing-entries">
          <Notices runner={entryRunner} id="pricing-entries" />
          <p className="hint">{t("pricing.entries_hint", { code: list.code })}</p>
          {entries.length === 0 ? <p data-testid="pricing-entries-empty">{t("pricing.entries_empty")}</p> : (
            <table className="table" data-testid="pricing-entries-table">
              <thead><tr><th>{t("pricing.target")}</th><th>{t("pricing.unit")}</th><th>{t("pricing.min_quantity")}</th><th>{t("pricing.unit_price")}</th><th>{t("pricing.status")}</th><th>{t("pricing.actions")}</th></tr></thead>
              <tbody>
                {entries.map((e) => (
                  <tr key={e.id} data-testid={`pricing-entry-${e.id}`}>
                    <td><bdi dir="auto">{targetName(e)}</bdi></td><td><bdi dir="ltr">{unitName(e.unitId)}</bdi></td><td><bdi dir="ltr">{e.minQuantity}</bdi></td>
                    <td>{repricing?.id === e.id
                      ? <input aria-label={t("pricing.unit_price")} dir="ltr" inputMode="decimal" value={repricing.price} onChange={(ev) => setRepricing({ id: e.id, price: ev.target.value })} />
                      : <bdi dir="ltr">{e.unitPrice}</bdi>}</td>
                    <td>{e.isActive ? t("pricing.active") : t("pricing.inactive")}</td>
                    <td>{configure && (repricing?.id === e.id
                      ? <><button type="button" className="primary" disabled={entryRunner.busy || Number.isNaN(num(repricing.price))} onClick={() => void reprice()}>{t("pricing.save")}</button><button type="button" className="ghost" onClick={() => setRepricing(null)}>{t("pricing.cancel")}</button></>
                      : <><button type="button" className="ghost" onClick={() => setRepricing({ id: e.id, price: String(e.unitPrice) })}>{t("pricing.edit_price")}</button><button type="button" className="ghost" disabled={entryRunner.busy} onClick={() => toggleEntry(e)}>{e.isActive ? t("pricing.deactivate") : t("pricing.activate")}</button></>)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {configure && (
            <fieldset disabled={entryRunner.busy} data-testid="pricing-entry-create">
              <legend>{t("pricing.add_entry")}</legend>
              <Field id="pe-kind" label={t("pricing.target_kind")}>
                {(a) => <select {...a} value={entry.kind} onChange={(e) => setEntry({ ...entry, kind: e.target.value, targetId: "" })}><option value="item">{t("pricing.kind_item")}</option><option value="group">{t("pricing.kind_group")}</option></select>}
              </Field>
              {entry.kind === "item"
                ? <ItemPicker id="pe-target" label={t("pricing.target")} value={entry.targetId} onChange={(v, it) => { names.remember(it); setEntry({ ...entry, targetId: v }); }} error={entryRunner.fields.itemId} />
                : <Field id="pe-target" label={t("pricing.target")} required error={entryRunner.fields.itemId}>
                    {(a) => <select {...a} value={entry.targetId} onChange={(e) => setEntry({ ...entry, targetId: e.target.value })}><option value="">{t("pricing.choose")}</option>{targets.map((x) => <option key={x.id} value={x.id}>{x.label}</option>)}</select>}
                  </Field>}
              <Field id="pe-unit" label={t("pricing.unit")} required error={entryRunner.fields.unitId}>
                {(a) => <select {...a} value={entry.unitId} onChange={(e) => setEntry({ ...entry, unitId: e.target.value })}><option value="">{t("pricing.choose")}</option>{data.pickers.units.map((u) => <option key={u.id} value={u.id}>{u.code}</option>)}</select>}
              </Field>
              <Field id="pe-min" label={t("pricing.min_quantity")} hint={t("pricing.tier_hint")} error={bad(entry.minQuantity) ? t("field.invalid") : entryRunner.fields.minQuantity}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={entry.minQuantity} onChange={(e) => setEntry({ ...entry, minQuantity: e.target.value })} />}</Field>
              <Field id="pe-price" label={t("pricing.unit_price")} required error={bad(entry.unitPrice) ? t("field.invalid") : entryRunner.fields.unitPrice}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={entry.unitPrice} onChange={(e) => setEntry({ ...entry, unitPrice: e.target.value })} />}</Field>
              <button type="button" className="primary" disabled={!entry.targetId || !entry.unitId || Number.isNaN(num(entry.unitPrice)) || Number.isNaN(num(entry.minQuantity))} onClick={() => void addEntry()}>{t("pricing.add_entry")}</button>
            </fieldset>
          )}
        </div>
      )}
    </div>
  );
}

function DiscountsTab({ data, configure, reload, onDirty }: TabProps) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("pricing.error.", reload);
  const [form, setForm] = useState({ code: "", nameAr: "", nameEn: "", priority: "10", validFrom: today(), validTo: "", percent: "", minQuantity: "0", customerId: "", customerGroupId: "", scope: "all", targetId: "" });
  const isDirty = form.code !== "" || form.nameAr !== "" || form.nameEn !== "" || form.percent !== "";
  useReportUnsaved("pricing-discounts", isDirty, t("pricing.tab.discounts"));
  useEffect(() => { onDirty(isDirty); }, [isDirty]); // eslint-disable-line react-hooks/exhaustive-deps
  const names = useItemNames(data.pickers.items, data.discounts.discounts.map((d) => d.itemId));
  const scopeText = (d: DiscountRule) => {
    const parts = [d.customerId ? customerName(data.pickers, d.customerId, "") : "", d.customerGroupId ? `${t("pricing.scope_customer_group")} ${groupName(data.pickers, d.customerGroupId, "")}` : "", d.itemId ? names.code(d.itemId) : "", d.itemGroupId ? (data.pickers.groups.find((g) => g.id === d.itemGroupId)?.code ?? d.itemGroupId) : ""].filter(Boolean);
    return parts.length === 0 ? t("pricing.scope_all") : parts.join(" · ");
  };
  async function create() {
    const done = await runner.run(() => auth.api.createDiscountRule({ code: form.code, nameAr: form.nameAr, nameEn: blank(form.nameEn), priority: num(form.priority), validFrom: form.validFrom, validTo: blank(form.validTo), percent: num(form.percent), minQuantity: num(form.minQuantity),
      customerId: blank(form.customerId), customerGroupId: blank(form.customerGroupId), itemId: form.scope === "item" ? form.targetId : null, itemGroupId: form.scope === "group" ? form.targetId : null }), t("pricing.saved"));
    if (done) setForm({ ...form, code: "", nameAr: "", nameEn: "", percent: "" });
  }
  const toggle = (d: DiscountRule) => void runner.run(() => auth.api.updateDiscountRule(d.id, { nameAr: d.nameAr, nameEn: d.nameEn, priority: d.priority, validFrom: d.validFrom, validTo: d.validTo, isActive: !d.isActive, percent: d.percent, minQuantity: d.minQuantity }, d.revision), t("pricing.saved"));
  const targets = data.pickers.groups.map((g) => ({ id: g.id, label: codeName(g.code, localName(g, locale)) }));
  return (
    <div data-testid="pricing-tab-discounts">
      <Notices runner={runner} id="pricing-discounts" />
      <p className="hint">{t("pricing.discounts_hint")}</p>
      {data.discounts.discounts.length === 0 ? <p data-testid="pricing-discounts-empty">{t("pricing.discounts_empty")}</p> : (
        <table className="table" data-testid="pricing-discounts-table">
          <thead><tr><th>{t("pricing.code")}</th><th>{t("pricing.name_ar")}</th><th>{t("pricing.percent")}</th><th>{t("pricing.min_quantity")}</th><th>{t("pricing.priority")}</th><th>{t("pricing.scope")}</th><th>{t("pricing.valid")}</th><th>{t("pricing.status")}</th><th>{t("pricing.actions")}</th></tr></thead>
          <tbody>
            {data.discounts.discounts.map((d) => (
              <tr key={d.id} data-testid={`pricing-discount-${d.code}`}>
                <td><bdi dir="ltr">{d.code}</bdi></td><td>{localName(d, locale)}</td><td><bdi dir="ltr">{d.percent}%</bdi></td><td><bdi dir="ltr">{d.minQuantity}</bdi></td><td><bdi dir="ltr">{d.priority}</bdi></td>
                <td><bdi dir="auto">{scopeText(d)}</bdi></td><td><bdi dir="ltr">{d.validFrom} → {d.validTo ?? t("pricing.open_ended")}</bdi></td><td>{d.isActive ? t("pricing.active") : t("pricing.inactive")}</td>
                <td>{configure && <button type="button" className="ghost" disabled={runner.busy} onClick={() => toggle(d)}>{d.isActive ? t("pricing.deactivate") : t("pricing.activate")}</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {configure && (
        <fieldset disabled={runner.busy} data-testid="pricing-discount-create">
          <legend>{t("pricing.create_discount")}</legend>
          <Field id="pd-code" label={t("pricing.code")} required error={runner.fields.code}>{(a) => <input {...a} dir="ltr" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} />}</Field>
          <Field id="pd-name-ar" label={t("pricing.name_ar")} required error={runner.fields.nameAr}>{(a) => <input {...a} value={form.nameAr} onChange={(e) => setForm({ ...form, nameAr: e.target.value })} />}</Field>
          <Field id="pd-name-en" label={t("pricing.name_en")} error={runner.fields.nameEn}>{(a) => <input {...a} dir="ltr" value={form.nameEn} onChange={(e) => setForm({ ...form, nameEn: e.target.value })} />}</Field>
          <Field id="pd-percent" label={t("pricing.percent")} required error={bad(form.percent) ? t("field.invalid") : runner.fields.percent}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={form.percent} onChange={(e) => setForm({ ...form, percent: e.target.value })} />}</Field>
          <Field id="pd-min" label={t("pricing.min_quantity")} error={bad(form.minQuantity) ? t("field.invalid") : runner.fields.minQuantity}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={form.minQuantity} onChange={(e) => setForm({ ...form, minQuantity: e.target.value })} />}</Field>
          <Field id="pd-priority" label={t("pricing.priority")} hint={t("pricing.priority_hint")} error={bad(form.priority) ? t("field.invalid") : runner.fields.priority}>{(a) => <input {...a} dir="ltr" inputMode="numeric" value={form.priority} onChange={(e) => setForm({ ...form, priority: e.target.value })} />}</Field>
          <Field id="pd-from" label={t("pricing.valid_from")} required error={runner.fields.validFrom}>{(a) => <input {...a} type="date" value={form.validFrom} onChange={(e) => setForm({ ...form, validFrom: e.target.value })} />}</Field>
          <Field id="pd-to" label={t("pricing.valid_to")} error={runner.fields.validTo}>{(a) => <input {...a} type="date" value={form.validTo} onChange={(e) => setForm({ ...form, validTo: e.target.value })} />}</Field>
          <CustomerSelect id="pd-customer" value={form.customerId} onChange={(v) => setForm({ ...form, customerId: v })} pickers={data.pickers} error={runner.fields.customerId} disabled={form.customerGroupId !== ""} />
          <CustomerGroupSelect id="pd-group" value={form.customerGroupId} onChange={(v) => setForm({ ...form, customerGroupId: v })} pickers={data.pickers} error={runner.fields.customerGroupId} disabled={form.customerId !== ""} />
          <Field id="pd-scope" label={t("pricing.scope")} hint={t("pricing.scope_combine_hint")} error={runner.fields.itemId}>
            {(a) => <select {...a} value={form.scope} onChange={(e) => setForm({ ...form, scope: e.target.value, targetId: "" })}><option value="all">{t("pricing.scope_all")}</option><option value="item">{t("pricing.kind_item")}</option><option value="group">{t("pricing.kind_group")}</option></select>}
          </Field>
          {form.scope !== "all" && (
            form.scope === "item"
              ? <ItemPicker id="pd-target" label={t("pricing.target")} value={form.targetId} onChange={(v, it) => { names.remember(it); setForm({ ...form, targetId: v }); }} />
              : <Field id="pd-target" label={t("pricing.target")} required>
                  {(a) => <select {...a} value={form.targetId} onChange={(e) => setForm({ ...form, targetId: e.target.value })}><option value="">{t("pricing.choose")}</option>{targets.map((x) => <option key={x.id} value={x.id}>{x.label}</option>)}</select>}
                </Field>
          )}
          <button type="button" className="primary" disabled={!form.code.trim() || !form.nameAr.trim() || !form.validFrom || Number.isNaN(num(form.percent)) || Number.isNaN(num(form.priority)) || bad(form.minQuantity) || (form.scope !== "all" && !form.targetId)} onClick={() => void create()}>{t("pricing.create_discount")}</button>
        </fieldset>
      )}
    </div>
  );
}

function QuoteTab({ data, onDirty }: { data: Data; onDirty: (v: boolean) => void }) {
  const { t } = useI18n();
  const auth = useAuth();
  const [form, setForm] = useState({ date: today(), currency: data.pickers.baseCurrency, itemId: "", unitId: "", quantity: "1", customerId: "", unitCost: "", overridePrice: "", overrideDiscount: "", overrideReason: "" });
  const [quote, setQuote] = useState<PricingQuote | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { onDirty(false); }, []); // eslint-disable-line react-hooks/exhaustive-deps    (a preview holds no business data to lose)
  const [pickedItem, setPickedItem] = useState<InventoryItem | null>(null);
  const item = pickedItem?.id === form.itemId ? pickedItem : data.pickers.items.find((i) => i.id === form.itemId);
  const wantsOverride = form.overridePrice.trim() !== "" || form.overrideDiscount.trim() !== "";
  const valid = form.itemId && form.unitId && form.date && form.currency.trim().length === 3 && !Number.isNaN(num(form.quantity)) && !bad(form.unitCost) && !bad(form.overridePrice) && !bad(form.overrideDiscount) && (!wantsOverride || form.overrideReason.trim() !== "");

  async function run() {
    setBusy(true); setError(null);
    try {
      setQuote(await auth.api.quotePrice({ date: form.date, currency: form.currency, itemId: form.itemId, itemGroupId: item?.groupId ?? null, unitId: form.unitId, quantity: num(form.quantity), customerId: blank(form.customerId),
        unitCost: form.unitCost.trim() === "" ? null : num(form.unitCost),
        override: wantsOverride ? { unitPrice: form.overridePrice.trim() === "" ? null : num(form.overridePrice), discountPercent: form.overrideDiscount.trim() === "" ? null : num(form.overrideDiscount), reason: form.overrideReason } : null }));
    } catch (e) {
      setQuote(null);
      if (!auth.handleError(e)) {
        const field = (e as { details?: { fields?: { field: string; code: string }[] } }).details?.fields?.[0];
        const key = field ? `pricing.quote_error.${field.code}` : "";
        setError(field && t(key) !== key ? t(key) : errorText(t, e, "pricing.error."));
      }
    } finally { setBusy(false); }
  }
  const r = quote?.result;
  return (
    <div data-testid="pricing-tab-quote">
      <p className="hint">{t("pricing.quote.hint")}</p>
      {error && <Banner kind="error" testId="pricing-quote-error">{error}</Banner>}
      <fieldset disabled={busy} data-testid="pricing-quote-form">
        <legend>{t("pricing.quote.form")}</legend>
        <Field id="pq-date" label={t("pricing.quote.date")} required>{(a) => <input {...a} type="date" value={form.date} onChange={(e) => setForm({ ...form, date: e.target.value })} />}</Field>
        <Field id="pq-currency" label={t("pricing.currency")} required>{(a) => <input {...a} dir="ltr" maxLength={3} value={form.currency} onChange={(e) => setForm({ ...form, currency: e.target.value.toUpperCase() })} />}</Field>
        <ItemPicker id="pq-item" label={t("pricing.kind_item")} value={form.itemId} onChange={(v, picked) => { setPickedItem(picked); setForm({ ...form, itemId: v }); }} />
        <Field id="pq-unit" label={t("pricing.unit")} required>
          {(a) => <select {...a} value={form.unitId} onChange={(e) => setForm({ ...form, unitId: e.target.value })}><option value="">{t("pricing.choose")}</option>{data.pickers.units.map((u) => <option key={u.id} value={u.id}>{u.code}</option>)}</select>}
        </Field>
        <Field id="pq-qty" label={t("pricing.quote.quantity")} required error={bad(form.quantity) ? t("field.invalid") : undefined}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={form.quantity} onChange={(e) => setForm({ ...form, quantity: e.target.value })} />}</Field>
        <CustomerSelect id="pq-customer" value={form.customerId} onChange={(v) => setForm({ ...form, customerId: v })} pickers={data.pickers} />
        <Field id="pq-cost" label={t("pricing.quote.unit_cost")} hint={t("pricing.quote.unit_cost_hint")} error={bad(form.unitCost) ? t("field.invalid") : undefined}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={form.unitCost} onChange={(e) => setForm({ ...form, unitCost: e.target.value })} />}</Field>
        <Field id="pq-ov-price" label={t("pricing.quote.override_price")} hint={t("pricing.quote.override_hint")} error={bad(form.overridePrice) ? t("field.invalid") : undefined}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={form.overridePrice} onChange={(e) => setForm({ ...form, overridePrice: e.target.value })} />}</Field>
        <Field id="pq-ov-discount" label={t("pricing.quote.override_discount")} error={bad(form.overrideDiscount) ? t("field.invalid") : undefined}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={form.overrideDiscount} onChange={(e) => setForm({ ...form, overrideDiscount: e.target.value })} />}</Field>
        <Field id="pq-ov-reason" label={t("pricing.quote.override_reason")} required={wantsOverride}>{(a) => <input {...a} value={form.overrideReason} onChange={(e) => setForm({ ...form, overrideReason: e.target.value })} />}</Field>
        <button type="button" className="primary" disabled={!valid} onClick={() => void run()}>{t("pricing.quote.run")}</button>
      </fieldset>
      {quote && r && (
        <div data-testid="pricing-quote-result">
          <h3>{t("pricing.quote.result")}</h3>
          <table className="table">
            <tbody>
              <tr><th>{t("pricing.quote.list_price")}</th><td><bdi dir="ltr">{r.listUnitPrice}</bdi></td></tr>
              <tr><th>{t("pricing.quote.discount")}</th><td><bdi dir="ltr">{r.discountPercent}%</bdi></td></tr>
              <tr><th>{t("pricing.quote.net_unit")}</th><td data-testid="pricing-quote-net"><bdi dir="ltr">{r.netUnitPrice}</bdi></td></tr>
              <tr><th>{t("pricing.quote.line_net")}</th><td data-testid="pricing-quote-line"><bdi dir="ltr">{r.lineNet}</bdi></td></tr>
              <tr><th>{t("pricing.quote.margin")}</th><td><bdi dir="ltr">{r.marginPercent === null ? t("pricing.none") : `${r.marginPercent}%`}</bdi> · {t(`pricing.margin_status.${r.margin}`)}{r.requiresApproval ? ` · ${t("pricing.quote.requires_approval")}` : ""}</td></tr>
              {r.overridden && <tr><th>{t("pricing.quote.overridden")}</th><td><bdi dir="auto">{r.overrideReason}</bdi></td></tr>}
              <tr><th>{t("pricing.quote.rule")}</th><td><bdi dir="ltr">{r.priceRuleRef}</bdi>{r.discountRuleRef ? <> · <bdi dir="ltr">{r.discountRuleRef}</bdi></> : null}</td></tr>
              <tr><th>{t("pricing.quote.snapshot")}</th><td><bdi dir="ltr">{quote.version}</bdi> · <bdi dir="ltr" data-testid="pricing-quote-hash">{quote.contentHash.slice(0, 16)}…</bdi></td></tr>
            </tbody>
          </table>
          <h4>{t("pricing.quote.trace")}</h4>
          <ul data-testid="pricing-quote-trace">{r.trace.map((s, i) => <li key={i}><bdi dir="ltr">{s.kind}: {s.ruleRef} — {s.outcome}</bdi></li>)}</ul>
        </div>
      )}
    </div>
  );
}
