import { ApiError, type AccessRole, type MfaPolicy, type MfaPolicyKind } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Loading, PageHeader } from "../components";
import { useReportUnsaved } from "../unsaved";

/**
 * The company's two-step verification policy (Phase 4.1 S3b-2b): `off`, `roles` (users holding one of the chosen roles) or `all` members. One implementation for the browser and the desktop app. The server decides everything:
 * who may read (`access.mfapolicy.view`) or change (`access.mfapolicy.update`) the policy, the fresh second-factor proof (the shared step-up dialog asks for it and repeats the save once), the lock-out guard (409
 * `mfa_policy_requires_own_enrolment`: the person saving must have an authenticator before the policy would apply to them) and the role check. This screen shows the policy and sends the change; it enforces nothing.
 * Saving `roles` or `all` first asks for confirmation, because every affected user without an authenticator is asked to enrol before reaching the company.
 */
const KINDS: MfaPolicyKind[] = ["off", "roles", "all"];

export function MfaPolicyPage() {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const canEdit = auth.can("access.mfapolicy.update");
  const [saved, setSaved] = useState<MfaPolicy | null | undefined>(undefined);        // undefined: loading
  const [roles, setRoles] = useState<AccessRole[] | null>(null);                      // null: not available (the role list is refused or failed)
  const [kind, setKind] = useState<MfaPolicyKind>("off");
  const [chosen, setChosen] = useState<string[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [needOwnEnrolment, setNeedOwnEnrolment] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const seq = useRef(0);

  const load = useCallback(async () => {
    const n = ++seq.current;
    try {
      const [policy, roleList] = await Promise.all([
        auth.api.getMfaPolicy(),
        auth.api.listAccessRoles().then((r) => r.roles, (e: unknown) => { if (e instanceof ApiError && (e.status === 401 || (e.status === 403 && e.code === "mfa_enrolment_required"))) throw e; return null; }),
      ]);
      if (n !== seq.current) return;
      setSaved(policy); setKind(policy.policy); setChosen(policy.roleIds); setRoles(roleList); setLoadError(null);
    } catch (e) {
      if (n !== seq.current || auth.handleError(e)) return;
      setLoadError(errorText(t, e, "mfapolicy.error."));
      setSaved((s) => s ?? null);
    }
  }, [auth, t]);
  useEffect(() => { void load(); }, [load]);

  const dirty = saved != null && (kind !== saved.policy || (kind === "roles" && [...chosen].sort().join() !== [...saved.roleIds].sort().join()));
  useReportUnsaved("mfa-policy", dirty, t("mfapolicy.section"));

  function toggle(id: string) { setChosen((c) => (c.includes(id) ? c.filter((x) => x !== id) : [...c, id])); setNotice(null); }

  async function save() {
    setConfirming(false);
    if (busy) return;
    setBusy(true); setError(null); setNotice(null); setNeedOwnEnrolment(false);
    try {
      const result = await auth.api.putMfaPolicy({ policy: kind, roleIds: kind === "roles" ? chosen : [] });
      setSaved(result); setKind(result.policy); setChosen(result.roleIds);
      setNotice(t("mfapolicy.saved"));
    } catch (e) {
      if (auth.handleError(e)) return;
      if (e instanceof ApiError && e.status === 409 && e.code === "mfa_policy_requires_own_enrolment") setNeedOwnEnrolment(true);
      else {
        setError(errorText(t, e, "mfapolicy.error."));
        if (e instanceof ApiError && e.code === "unknown_role") void load();                 // the role list is stale: show the roles as they are now (the edit is kept by the user's own choice, not silently changed)
      }
    } finally { setBusy(false); }
  }

  function submit() {
    if (kind === "off") void save(); else setConfirming(true);
  }

  if (saved === undefined) return <Loading />;
  const rolesMissing = kind === "roles" && chosen.length === 0;
  const known = new Set((roles ?? []).map((r) => r.id));
  const roleLabel = (r: AccessRole) => r.name;
  void locale;

  return (
    <div>
      <PageHeader title={t("mfapolicy.title")} description={t("mfapolicy.subtitle")} />
      {loadError && <Banner kind="error" testId="mfapolicy-load-error">{loadError} <button type="button" className="link" onClick={() => void load()}>{t("picker.retry")}</button></Banner>}
      {notice && <Banner kind="success" testId="mfapolicy-notice">{notice}</Banner>}
      {error && <Banner kind="error" testId="mfapolicy-error">{error}</Banner>}
      {needOwnEnrolment && (
        <Banner kind="warning" testId="mfapolicy-own-enrolment">{t("mfapolicy.error.mfa_policy_requires_own_enrolment")} <a href="#/account/security" data-testid="mfapolicy-security-link">{t("mfapolicy.open_security")}</a></Banner>
      )}
      {saved && (
        <form onSubmit={(e) => { e.preventDefault(); if (canEdit && !rolesMissing) submit(); }} noValidate>
          <Card title={t("mfapolicy.card")} hint={t("mfapolicy.hint")}>
            {!canEdit && <p className="hint" data-testid="mfapolicy-readonly">{t("mfapolicy.readonly")}</p>}
            <fieldset className="field" disabled={!canEdit || busy}>
              <legend>{t("mfapolicy.kind")}</legend>
              {KINDS.map((k) => (
                <label key={k} className="radio" data-testid={`mfapolicy-kind-${k}`}>
                  <input type="radio" name="mfa-policy" value={k} checked={kind === k} onChange={() => { setKind(k); setNotice(null); }} />
                  <span>{t(`mfapolicy.kind.${k}`)}</span>
                  <span className="hint">{t(`mfapolicy.kind.${k}.hint`)}</span>
                </label>
              ))}
            </fieldset>
            {kind === "roles" && (
              <fieldset className="field" disabled={!canEdit || busy} data-testid="mfapolicy-roles">
                <legend>{t("mfapolicy.roles")}</legend>
                {roles === null && <p className="hint" data-testid="mfapolicy-roles-unavailable">{t("mfapolicy.roles_unavailable")}</p>}
                {(roles ?? []).filter((r) => r.active).map((r) => (
                  <label key={r.id} className="checkbox" data-testid={`mfapolicy-role-${r.id}`}>
                    <input type="checkbox" checked={chosen.includes(r.id)} onChange={() => toggle(r.id)} />
                    <bdi>{roleLabel(r)}</bdi>
                  </label>
                ))}
                {roles !== null && chosen.filter((id) => !known.has(id) || !roles.find((r) => r.id === id)?.active).map((id) => (
                  <label key={id} className="checkbox" data-testid={`mfapolicy-role-unavailable-${id}`}>
                    <input type="checkbox" checked onChange={() => toggle(id)} />
                    <span>{t("mfapolicy.role_unavailable")}</span>
                  </label>
                ))}
                {rolesMissing && <p className="field-error" role="alert" data-testid="mfapolicy-roles-required">{t("mfapolicy.roles_required")}</p>}
              </fieldset>
            )}
            {kind !== "off" && <Banner kind="warning" testId="mfapolicy-warning">{t(kind === "all" ? "mfapolicy.warn.all" : "mfapolicy.warn.roles")}</Banner>}
            {saved.updatedAtUtc && <p className="hint">{t("mfapolicy.updated")} <bdi dir="ltr">{new Date(saved.updatedAtUtc).toLocaleString(locale === "ar" ? "ar" : "en", { dateStyle: "medium", timeStyle: "short" })}</bdi></p>}
            {canEdit && (
              <div className="actions">
                <button type="submit" className="primary" disabled={busy || !dirty || rolesMissing} data-testid="mfapolicy-save">{busy ? t("loading") : t("mfapolicy.save")}</button>
                <button type="button" className="ghost" disabled={busy || !dirty} onClick={() => { setKind(saved.policy); setChosen(saved.roleIds); setError(null); setNeedOwnEnrolment(false); }} data-testid="mfapolicy-cancel">{t("mfapolicy.cancel")}</button>
              </div>
            )}
          </Card>
        </form>
      )}
      {confirming && (
        <ConfirmDialog title={t("mfapolicy.confirm_title")} message={t(kind === "all" ? "mfapolicy.confirm.all" : "mfapolicy.confirm.roles")} confirmLabel={t("mfapolicy.confirm")} onCancel={() => setConfirming(false)} onConfirm={() => void save()} />
      )}
    </div>
  );
}
