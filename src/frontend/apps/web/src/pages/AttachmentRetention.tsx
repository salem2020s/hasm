import { ApiError, type LegalHold, type RetentionCapabilities, type RetentionPolicyVersion, type RetentionPreview, type RetentionRuleInfo } from "@hasm/api-client";
import { SearchPicker, Tabs, useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, Loading, PageHeader } from "../components";
import { useRunner } from "../ledgerCommon";
import { useReportUnsaved } from "../unsaved";

/**
 * Retention of removed attachments (Phase R, owner decision D-082): policy versions, legal holds and a READ-ONLY eligibility preview. ONE implementation for Web and Desktop.
 * Nothing here deletes anything: purge execution does not exist in this screen, and while the owner has not confirmed the retention basis (D-109) the server reports every item as pending. Actions are shown
 * from the server's capabilities; the server authorizes every call again (two-person rules included). Hidden is never the only protection.
 */
const KINDS = ["*", "local_party", "inventory_item"] as const;
const PAGE = 50;

interface RuleDraft { kind: string; sensitivity: string; mode: "months" | "indefinite"; months: string }
const blankRule = (): RuleDraft => ({ kind: "*", sensitivity: "0", mode: "months", months: "" });
const RECORD_PAGE_LIMIT = 50;
interface RecordChoice { id: string; label: string }
const isolate = (s: string) => `\u2068${s}\u2069`;

/**
 * Picks the record a record-level hold covers with the shared SearchPicker (bounded server-side search over the EXISTING party and item lists). A user who cannot read that list (company.parties.view or
 * company.items.view missing, or the server answers 403) gets the typed record id instead, with a hint saying so: never a broken picker. The server authorizes the hold itself either way.
 */
function HoldRecordPicker({ kind, value, onChange, error }: { kind: string; value: string; onChange: (id: string) => void; error?: string }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const [denied, setDenied] = useState<string | null>(null);   // the kind for which the server answered 403
  const permitted = kind === "local_party" ? auth.can("company.parties.view") : auth.can("company.items.view");
  const load = useCallback((search: string): Promise<{ items: RecordChoice[]; total: number }> => {
    const s = search === "" ? undefined : search;
    const run: Promise<{ items: RecordChoice[]; total: number }> = kind === "local_party"
      ? auth.api.listParties({ search: s, limit: RECORD_PAGE_LIMIT }).then((r) => ({ items: r.parties.map((p) => ({ id: p.id, label: `${isolate(p.code)} \u2014 ${isolate(p.displayName)}` })), total: r.total }))
      : auth.api.listInventoryItems({ search: s, limit: RECORD_PAGE_LIMIT }).then((r) => ({ items: r.items.map((i) => ({ id: i.id, label: `${isolate(i.code)} \u2014 ${isolate((locale === "ar" ? i.nameAr ?? i.nameEn : i.nameEn ?? i.nameAr) ?? i.code)}` })), total: r.total }));
    return run.catch((e) => { if (e instanceof ApiError && e.status === 403) setDenied(kind); else auth.handleError(e); throw e; });
  }, [auth.api, auth.handleError, kind, locale]);   // eslint-disable-line react-hooks/exhaustive-deps
  if (!permitted || denied === kind) {
    return (
      <Field id="ret-hsubject" label={t("ret.hold.subject_id")} required error={error} hint={t("ret.hold.record_unavailable")}>
        {(p) => <input {...p} dir="ltr" value={value} onChange={(e) => onChange(e.target.value)} />}
      </Field>
    );
  }
  return (
    <div className={`field${error ? " field-invalid" : ""}`} data-testid="ret-hold-record-picker">
      <label htmlFor="ret-hsubject-list">{t("ret.hold.record")}<span aria-hidden="true" className="required"> *</span></label>
      <SearchPicker id="ret-hsubject-list" label={t("ret.hold.record")} value={value} onChange={(id) => onChange(id)} load={load} getId={(r: RecordChoice) => r.id} getLabel={(r) => r.label}
        noneLabel={t("ret.hold.record_choose")} reloadKey={kind} invalid={!!error} required describedBy={error ? "ret-hsubject-error" : undefined} />
      {error && <p id="ret-hsubject-error" className="field-error">{error}</p>}
    </div>
  );
}

const day = (iso: string | null) => (iso ? iso.slice(0, 10) : "");

export function AttachmentRetentionPage() {
  const { t } = useI18n();
  const auth = useAuth();
  const me = auth.user?.id ?? null;
  const [caps, setCaps] = useState<RetentionCapabilities | null>(null);
  const [policies, setPolicies] = useState<RetentionPolicyVersion[]>([]);
  const [holds, setHolds] = useState<LegalHold[]>([]);
  const [showReleased, setShowReleased] = useState(false);
  const [preview, setPreview] = useState<RetentionPreview | null>(null);
  const [offset, setOffset] = useState(0);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [tab, setTab] = useState("policy");
  const seq = useRef(0);

  const load = useCallback(async () => {
    const n = ++seq.current;
    try {
      const c = await auth.api.getRetentionCapabilities();
      const [p, h, pv] = await Promise.all([
        auth.api.listRetentionPolicies(), auth.api.listLegalHolds(showReleased), auth.api.previewRetention(offset, PAGE),
      ]);
      if (n !== seq.current) return;
      setCaps(c); setPolicies(p.policies); setHolds(h.holds); setPreview(pv); setLoadError(null);
    } catch (e) {
      if (n !== seq.current || auth.handleError(e)) return;
      setLoadError(errorText(t, e, "ret.error."));
    }
  }, [auth, t, showReleased, offset]);
  useEffect(() => { void load(); }, [load]);

  const runner = useRunner("ret.error.", load, "ret");

  // Policy draft form.
  const [effective, setEffective] = useState("");
  const [note, setNote] = useState("");
  const [rules, setRules] = useState<RuleDraft[]>([blankRule()]);
  const draftDirty = effective !== "" || note !== "" || rules.length !== 1 || JSON.stringify(rules[0]) !== JSON.stringify(blankRule());
  const setRule = (i: number, patch: Partial<RuleDraft>) => setRules((r) => r.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const toRules = (): RetentionRuleInfo[] => rules.map((r) => ({
    subjectKind: r.kind, sensitivity: Number(r.sensitivity), indefinite: r.mode === "indefinite", months: r.mode === "indefinite" ? null : r.months === "" ? 0 : Number(r.months),
  }));
  const saveDraft = async () => {
    const ok = await runner.run(() => auth.api.draftRetentionPolicy({ effectiveFrom: effective, note: note.trim() || null, rules: toRules() }), t("ret.policy.saved"));
    if (ok) { setEffective(""); setNote(""); setRules([blankRule()]); }
  };

  // Hold form.
  const [scope, setScope] = useState<"1" | "2" | "3">("1");
  const [hKind, setHKind] = useState("local_party");
  const [hSubject, setHSubject] = useState("");
  const [hAttachment, setHAttachment] = useState("");
  const [hReason, setHReason] = useState("");
  const [hRef, setHRef] = useState("");
  const holdDirty = hReason !== "" || hRef !== "" || hSubject !== "" || hAttachment !== "";
  useReportUnsaved("ret-policy-draft", draftDirty, t("ret.tab.policy"));
  useReportUnsaved("ret-hold-form", holdDirty, t("ret.tab.holds"));
  const placeHold = async () => {
    const s = Number(scope) as 1 | 2 | 3;
    const ok = await runner.run(() => auth.api.placeLegalHold({
      scope: s, reason: hReason.trim(), reference: hRef.trim() || null,
      subjectKind: s === 2 ? hKind : null, subjectId: s === 2 ? hSubject.trim() : null, attachmentId: s === 3 ? hAttachment.trim() : null,
    }), t("ret.hold.placed"));
    if (ok) { setHReason(""); setHRef(""); setHSubject(""); setHAttachment(""); }
  };

  // Dialogs.
  const [approving, setApproving] = useState<RetentionPolicyVersion | null>(null);
  const [ack, setAck] = useState("");
  const [revoking, setRevoking] = useState<RetentionPolicyVersion | null>(null);
  const [releasing, setReleasing] = useState<LegalHold | null>(null);
  const [dialogReason, setDialogReason] = useState("");
  const closeDialogs = () => { setApproving(null); setRevoking(null); setReleasing(null); setAck(""); setDialogReason(""); };

  const f = runner.fields;
  const fieldMsgs = Object.values(f);
  const policyTabInvalid = Object.keys(f).some((k) => k.startsWith("rules") || k === "effectiveFrom" || k === "note");
  const holdTabInvalid = Object.keys(f).some((k) => ["scope", "subjectKind", "subjectId", "attachmentId", "reference"].includes(k) || k === "reason") && !policyTabInvalid;

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!caps || !preview) return <Loading />;

  const ruleSummary = (r: RetentionRuleInfo) => {
    const kind = r.subjectKind === "*" ? t("ret.rule.kind_all") : t(`ret.rule.kind.${r.subjectKind}`);
    const period = r.indefinite ? t("ret.rule.indefinite") : `${r.months} ${t("ret.rule.months")}`;
    return `${kind} / ${t(`ret.rule.sens.${r.sensitivity}`)}: ${period}`;
  };
  const kindLabel = (k: string) => (k === "*" ? t("ret.rule.kind_all") : t(`ret.rule.kind.${k}`));

  const policyTab = (
    <div>
      <Card title={t("ret.tab.policy")} hint={t("ret.policy.hint")}>
        {policies.length === 0 ? <p>{t("ret.policy.empty")}</p> : (
          <div className="table-wrap">
            <table className="data-table">
<caption className="visually-hidden">{t("ret.tab.policy")}</caption>
              <thead><tr>
                <th scope="col">{t("ret.version")}</th><th scope="col">{t("ret.status")}</th><th scope="col">{t("ret.effective_from")}</th>
                <th scope="col">{t("ret.rules")}</th><th scope="col">{t("ret.policy.shortens")}</th><th scope="col">{t("ret.actions")}</th>
              </tr></thead>
              <tbody>
                {policies.map((p) => (
                  <tr key={p.id} data-testid={`policy-${p.version}`}>
                    <td>{p.version}</td>
                    <td>{t(`ret.policy.status.${p.status}`)}</td>
                    <td>{day(p.effectiveFrom)}</td>
                    <td><ul className="change-list">{p.rules.map((r, i) => <li key={i}>{ruleSummary(r)}</li>)}</ul></td>
                    <td>{p.shortensAnyPeriod ? "✓" : ""}</td>
                    <td>
                      {caps.approve && p.status === 1 && p.draftedBy !== me && (
                        <button type="button" disabled={runner.busy} onClick={() => setApproving(p)}>{t("ret.policy.approve")}</button>
                      )}
                      {caps.approve && p.status === 1 && p.draftedBy === me && <span className="hint">{t("ret.policy.own_draft")}</span>}
                      {caps.approve && p.status === 2 && (
                        <button type="button" className="danger" disabled={runner.busy} onClick={() => setRevoking(p)}>{t("ret.policy.revoke")}</button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
      {caps.configure && (
        <Card title={t("ret.policy.draft")}>
          <form onSubmit={(e) => { e.preventDefault(); void saveDraft(); }} noValidate>
            <Field id="ret-effective" label={t("ret.effective_from")} required error={f.effectiveFrom}>
              {(p) => <input {...p} type="date" value={effective} onChange={(e) => setEffective(e.target.value)} />}
            </Field>
            <Field id="ret-note" label={t("ret.note")} error={f.note}>
              {(p) => <input {...p} value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} />}
            </Field>
            {rules.map((r, i) => (
              <fieldset key={i}>
                <legend>{t("ret.rules")} {i + 1}</legend>
                <div className="fields">
                <Field id={`ret-kind-${i}`} label={t("ret.rule.kind")} error={f[`rules[${i}].subjectKind`]}>
                  {(p) => <select {...p} value={r.kind} onChange={(e) => setRule(i, { kind: e.target.value })}>{KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>}
                </Field>
                <Field id={`ret-sens-${i}`} label={t("ret.rule.sensitivity")} error={f[`rules[${i}].sensitivity`]}>
                  {(p) => <select {...p} value={r.sensitivity} onChange={(e) => setRule(i, { sensitivity: e.target.value })}>{["0", "1", "2"].map((s) => <option key={s} value={s}>{t(`ret.rule.sens.${s}`)}</option>)}</select>}
                </Field>
                <Field id={`ret-mode-${i}`} label={t("ret.rule.period")} error={f[`rules[${i}]`]}>
                  {(p) => <select {...p} value={r.mode} onChange={(e) => setRule(i, { mode: e.target.value as RuleDraft["mode"] })}>
                    <option value="months">{t("ret.rule.mode_months")}</option><option value="indefinite">{t("ret.rule.mode_indefinite")}</option></select>}
                </Field>
                {r.mode === "months" && (
                  <Field id={`ret-months-${i}`} label={t("ret.rule.months")} error={f[`rules[${i}].months`]}>
                    {(p) => <input {...p} type="number" min={1} max={1200} value={r.months} onChange={(e) => setRule(i, { months: e.target.value })} />}
                  </Field>
                )}
                </div>
                {rules.length > 1 && <div className="actions"><button type="button" className="ghost" onClick={() => setRules((x) => x.filter((_, j) => j !== i))}>{t("ret.rule.remove")}</button></div>}
              </fieldset>
            ))}
            <div className="actions">
              <button type="button" onClick={() => setRules((x) => [...x, blankRule()])}>{t("ret.rule.add")}</button>
              <button type="submit" className="primary" disabled={runner.busy}>{t("ret.policy.save_draft")}</button>
            </div>
          </form>
        </Card>
      )}
    </div>
  );

  const holdsTab = (
    <div>
      <Card title={t("ret.holds.title")} hint={t("ret.holds.hint")}>
        <label><input type="checkbox" checked={showReleased} onChange={(e) => { setShowReleased(e.target.checked); }} /> {t("ret.hold.show_released")}</label>
        {holds.length === 0 ? <p>{t("ret.holds.empty")}</p> : (
          <div className="table-wrap">
            <table className="data-table">
<caption className="visually-hidden">{t("ret.holds.title")}</caption>
              <thead><tr>
                <th scope="col">{t("ret.hold.scope")}</th><th scope="col">{t("ret.hold.target")}</th><th scope="col">{t("ret.reason")}</th>
                <th scope="col">{t("ret.status")}</th><th scope="col">{t("ret.actions")}</th>
              </tr></thead>
              <tbody>
                {holds.map((h) => (
                  <tr key={h.id} data-testid={`hold-${h.id}`}>
                    <td>{t(`ret.hold.scope.${h.scope}`)}</td>
                    <td>{h.scope === 2 ? `${kindLabel(h.subjectKind ?? "")} ${h.subjectId}` : h.scope === 3 ? h.attachmentId : ""}</td>
                    <td>{h.reason}{h.reference ? ` (${h.reference})` : ""}</td>
                    <td>{h.isActive ? t("ret.hold.active") : t("ret.hold.released")}</td>
                    <td>
                      {caps.approve && h.isActive && h.placedBy !== me && <button type="button" disabled={runner.busy} onClick={() => setReleasing(h)}>{t("ret.hold.release")}</button>}
                      {caps.approve && h.isActive && h.placedBy === me && <span className="hint">{t("ret.hold.own")}</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
      {caps.hold && (
        <Card title={t("ret.hold.place")}>
          <form onSubmit={(e) => { e.preventDefault(); void placeHold(); }} noValidate>
            <Field id="ret-scope" label={t("ret.hold.scope")} error={f.scope}>
              {(p) => <select {...p} value={scope} onChange={(e) => setScope(e.target.value as "1" | "2" | "3")}>{["1", "2", "3"].map((s) => <option key={s} value={s}>{t(`ret.hold.scope.${s}`)}</option>)}</select>}
            </Field>
            {scope === "2" && (<>
              <Field id="ret-hkind" label={t("ret.rule.kind")} error={f.subjectKind}>
                {(p) => <select {...p} value={hKind} onChange={(e) => { setHKind(e.target.value); setHSubject(""); }}>{KINDS.filter((k) => k !== "*").map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>}
              </Field>
              <HoldRecordPicker kind={hKind} value={hSubject} onChange={setHSubject} error={f.subjectId} />
            </>)}
            {scope === "3" && (
              <Field id="ret-hatt" label={t("ret.hold.attachment_id")} required error={f.attachmentId} hint={t("ret.hold.attachment_hint")}>
                {(p) => <input {...p} dir="ltr" value={hAttachment} onChange={(e) => setHAttachment(e.target.value)} />}
              </Field>
            )}
            <Field id="ret-hreason" label={t("ret.reason")} required error={f.reason}>
              {(p) => <input {...p} value={hReason} maxLength={500} onChange={(e) => setHReason(e.target.value)} />}
            </Field>
            <Field id="ret-href" label={t("ret.hold.reference")} error={f.reference}>
              {(p) => <input {...p} value={hRef} maxLength={200} onChange={(e) => setHRef(e.target.value)} />}
            </Field>
            <div className="actions"><button type="submit" className="primary" disabled={runner.busy}>{t("ret.hold.place")}</button></div>
          </form>
        </Card>
      )}
    </div>
  );

  const pageEnd = offset + PAGE >= preview.total;
  const previewTab = (
    <Card title={t("ret.preview.title")} hint={t("ret.preview.hint")}>
      <p>{t("ret.preview.total", { n: preview.total })}</p>
      <ul className="change-list" data-testid="reason-counts">
        {Object.entries(preview.reasonCounts).map(([k, n]) => <li key={k}>{t(`ret.reason.${k}`)}: {n}</li>)}
      </ul>
      {preview.items.length === 0 ? <p>{t("ret.preview.empty")}</p> : (
        <div className="table-wrap">
          <table className="data-table">
<caption className="visually-hidden">{t("ret.preview.title")}</caption>
            <thead><tr>
              <th scope="col">{t("ret.preview.kind")}</th><th scope="col">{t("ret.hold.attachment_id")}</th><th scope="col">{t("ret.preview.removed")}</th>
              <th scope="col">{t("ret.preview.eligible_at")}</th><th scope="col">{t("ret.status")}</th>
            </tr></thead>
            <tbody>
              {preview.items.map((i) => (
                <tr key={i.attachmentId}>
                  <td>{kindLabel(i.subjectKind)}{i.isSensitive ? ` (${t("ret.preview.sensitive")})` : ""}</td>
                  <td dir="ltr">{i.attachmentId}</td>
                  <td>{day(i.removedAtUtc)}</td>
                  <td>{day(i.eligibleAtUtc)}</td>
                  <td>{t(`ret.reason.${i.reason}`)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <div className="actions">
        <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>{t("ret.preview.prev")}</button>
        <button type="button" disabled={pageEnd} onClick={() => setOffset(offset + PAGE)}>{t("ret.preview.next")}</button>
      </div>
    </Card>
  );

  return (
    <div>
      <PageHeader title={t("ret.title")} description={t("ret.subtitle")} />
      {!caps.basisConfirmed && <Banner kind="warning" testId="basis-pending">{t("ret.basis_pending")}</Banner>}
      {runner.error && <Banner kind="error">{runner.error}{fieldMsgs.length > 0 && <ul className="change-list">{[...new Set(fieldMsgs)].map((m) => <li key={m}>{m}</li>)}</ul>}</Banner>}
      {runner.conflict && <Banner kind="warning">{t("common.conflict")}</Banner>}
      {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      <Tabs
        label={t("ret.tabs_label")} value={tab} onChange={setTab} testId="retention-tabs"
        tabs={[
          { id: "policy", label: t("ret.tab.policy"), content: policyTab, dirty: draftDirty, invalid: policyTabInvalid },
          { id: "holds", label: t("ret.tab.holds"), content: holdsTab, dirty: holdDirty, invalid: holdTabInvalid },
          { id: "preview", label: t("ret.tab.preview"), content: previewTab },
        ]}
      />
      {approving && (
        <ConfirmDialog title={t("ret.policy.approve_title")} message={t("ret.policy.approve_message", { version: approving.version })}
          confirmLabel={t("ret.policy.approve")} onCancel={closeDialogs} confirmDisabled={approving.shortensAnyPeriod && ack.trim() === ""}
          onConfirm={() => { const p = approving; const a = ack.trim(); closeDialogs(); void runner.run(() => auth.api.approveRetentionPolicy(p.id, p.revision, a || null), t("ret.policy.approved")); }}>
          {approving.shortensAnyPeriod && (
            <Field id="ret-ack" label={t("ret.policy.ack_label")} required>
              {(p) => <input {...p} value={ack} maxLength={500} onChange={(e) => setAck(e.target.value)} />}
            </Field>
          )}
        </ConfirmDialog>
      )}
      {revoking && (
        <ConfirmDialog title={t("ret.policy.revoke_title")} message={t("ret.policy.revoke_message")} confirmLabel={t("ret.policy.revoke")} onCancel={closeDialogs} confirmDisabled={dialogReason.trim() === ""}
          onConfirm={() => { const p = revoking; const r = dialogReason.trim(); closeDialogs(); void runner.run(() => auth.api.revokeRetentionPolicy(p.id, p.revision, r), t("ret.policy.revoked")); }}>
          <Field id="ret-revoke-reason" label={t("ret.reason")} required>
            {(p) => <input {...p} value={dialogReason} maxLength={500} onChange={(e) => setDialogReason(e.target.value)} />}
          </Field>
        </ConfirmDialog>
      )}
      {releasing && (
        <ConfirmDialog title={t("ret.hold.release_title")} message={t("ret.hold.release_message")} confirmLabel={t("ret.hold.release")} onCancel={closeDialogs} confirmDisabled={dialogReason.trim() === ""}
          onConfirm={() => { const h = releasing; const r = dialogReason.trim(); closeDialogs(); void runner.run(() => auth.api.releaseLegalHold(h.id, h.revision, r), t("ret.hold.released_ok")); }}>
          <Field id="ret-release-reason" label={t("ret.reason")} required>
            {(p) => <input {...p} value={dialogReason} maxLength={500} onChange={(e) => setDialogReason(e.target.value)} />}
          </Field>
        </ConfirmDialog>
      )}
    </div>
  );
}
