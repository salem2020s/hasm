import { ApiError, type AccessRole, type MfaPolicy, type MfaPolicyKind } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { isCompanyContextChanged, useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Loading, PageHeader } from "../components";
import { useReportUnsaved } from "../unsaved";

/**
 * The company's two-step verification policy (Phase 4.1 S3b-2b): `off`, `roles` (users holding one of the chosen roles) or `all` members. One implementation for the browser and the desktop app. The server decides everything:
 * who may read (`access.mfapolicy.view`) or change (`access.mfapolicy.update`) the policy, the fresh second-factor proof (the shared step-up dialog asks for it and repeats the save once), the lock-out guard (409
 * `mfa_policy_requires_own_enrolment`: the person saving must have an authenticator before the policy would apply to them) and the role check. This screen shows the policy and sends the change; it enforces nothing.
 * Saving `roles` or `all` first asks for confirmation, because every affected user without an authenticator is asked to enrol before reaching the company.
 *
 * An unsaved choice is never replaced behind the user's back: the policy is fetched once (the loader depends only on the stable api and error handler, not on the language or the rest of the auth context), and every later
 * fetch (retry, the role refresh after `unknown_role`, the reload after a conflict) keeps a dirty edit. The save carries the loaded `revision` as If-Match; 412 `revision_conflict` (someone else changed the policy) keeps the
 * edit, says so and offers to load the current policy; the step-up retry repeats the identical request, so the same If-Match; the next revision comes from the save answer.
 */
const KINDS: MfaPolicyKind[] = ["off", "roles", "all"];
const sameRoles = (a: string[], b: string[]) => [...a].sort().join() === [...b].sort().join();

export function MfaPolicyPage() {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const { api, handleError } = auth;
  const canEdit = auth.can("access.mfapolicy.update");
  const [saved, setSaved] = useState<MfaPolicy | null | undefined>(undefined);        // undefined: loading
  const [roles, setRoles] = useState<AccessRole[] | null>(null);                      // null: not available (the role list is refused or failed)
  const [kind, setKind] = useState<MfaPolicyKind>("off");
  const [chosen, setChosen] = useState<string[]>([]);
  // Failures are kept as the error itself and worded when shown, so a language switch re-words them without asking the server again.
  const [loadError, setLoadError] = useState<unknown>(null);
  const [error, setError] = useState<unknown>(null);
  const [needOwnEnrolment, setNeedOwnEnrolment] = useState(false);
  const [conflict, setConflict] = useState(false);
  const [notice, setNotice] = useState<"saved" | "reloaded" | "context" | null>(null);
  const [rolesRefreshed, setRolesRefreshed] = useState<number | null>(null);          // after `unknown_role`: how many chosen roles no longer exist and were removed from the choice
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const seq = useRef(0);
  const dirtyRef = useRef(false);

  const rolesOrNull = useCallback(() => api.listAccessRoles().then((r) => r.roles, (e: unknown) => { if (e instanceof ApiError && (e.status === 401 || (e.status === 403 && e.code === "mfa_enrolment_required"))) throw e; return null; }), [api]);

  /** Fetches the policy and the roles. A dirty edit is kept: only what the server has saved (and its revision) is replaced. Returns true when the policy was loaded. */
  const load = useCallback(async (): Promise<boolean> => {
    const n = ++seq.current;
    try {
      const [policy, roleList] = await Promise.all([api.getMfaPolicy(), rolesOrNull()]);
      if (n !== seq.current) return false;
      setSaved(policy); setRoles(roleList); setLoadError(null);
      if (!dirtyRef.current) { setKind(policy.policy); setChosen(policy.roleIds); }
      return true;
    } catch (e) {
      if (n !== seq.current || handleError(e)) return false;
      setLoadError(e ?? new Error("load"));
      setSaved((s) => s ?? null);
      return false;
    }
  }, [api, handleError, rolesOrNull]);
  useEffect(() => { void load(); }, [load]);

  const dirty = saved != null && (kind !== saved.policy || (kind === "roles" && !sameRoles(chosen, saved.roleIds)));
  dirtyRef.current = dirty;
  useReportUnsaved("mfa-policy", dirty, t("mfapolicy.section"));

  function toggle(id: string) { setChosen((c) => (c.includes(id) ? c.filter((x) => x !== id) : [...c, id])); setNotice(null); }

  /** After 400 `unknown_role`: the role list is stale. Only the LIST is refreshed; the choice stays, except role ids that no longer exist at all (they cannot be shown or saved), and that is said. An inactive role stays, marked as not available. */
  async function refreshRoles() {
    try {
      const list = await rolesOrNull();
      if (list === null) return;
      const exists = new Set(list.map((r) => r.id));
      setRoles(list);
      setRolesRefreshed(chosen.filter((id) => !exists.has(id)).length);
      setChosen((c) => c.filter((id) => exists.has(id)));
    } catch (e) { handleError(e); }
  }

  /** After a conflict: loads what is saved now (and its revision). The edit is kept; the notice names the policy now in force so the user can decide to save over it or discard. */
  async function reload() {
    if (await load()) { setConflict(false); setError(null); setNotice("reloaded"); }
  }

  async function save() {
    setConfirming(false);
    if (busy || !saved) return;
    setBusy(true); setError(null); setNotice(null); setNeedOwnEnrolment(false); setConflict(false); setRolesRefreshed(null);
    try {
      const result = await api.putMfaPolicy({ policy: kind, roleIds: kind === "roles" ? chosen : [] }, saved.revision);
      setSaved(result); setKind(result.policy); setChosen(result.roleIds);
      setNotice("saved");
    } catch (e) {
      if (isCompanyContextChanged(e)) setNotice("context");                                   // not served: the session context is reloaded by handleError (no error shown)
      if (handleError(e)) return;
      if (e instanceof ApiError && e.status === 409 && e.code === "mfa_policy_requires_own_enrolment") setNeedOwnEnrolment(true);
      else if (e instanceof ApiError && e.status === 412) setConflict(true);                  // someone else changed the policy: the edit stays, nothing is fetched until the user asks
      else {
        setError(e ?? new Error("save"));
        if (e instanceof ApiError && e.code === "unknown_role") await refreshRoles();
      }
    } finally { setBusy(false); }
  }

  function submit() {
    if (kind === "off") void save(); else setConfirming(true);
  }

  if (saved === undefined) return <Loading />;
  const rolesMissing = kind === "roles" && chosen.length === 0;
  const available = new Set((roles ?? []).filter((r) => r.active).map((r) => r.id));

  return (
    <div>
      <PageHeader title={t("mfapolicy.title")} description={t("mfapolicy.subtitle")} />
      {loadError != null && <Banner kind="error" testId="mfapolicy-load-error">{errorText(t, loadError, "mfapolicy.error.")} <button type="button" className="link" onClick={() => void load()}>{t("picker.retry")}</button></Banner>}
      {notice === "saved" && <Banner kind="success" testId="mfapolicy-notice">{t("mfapolicy.saved")}</Banner>}
      {notice === "context" && <Banner kind="info" testId="mfapolicy-context">{t("mfapolicy.context_reloaded")}</Banner>}
      {notice === "reloaded" && saved && <Banner kind="info" testId="mfapolicy-reloaded">{t("mfapolicy.reloaded", { policy: t(`mfapolicy.kind.${saved.policy}`) })}{dirty ? ` ${t("mfapolicy.reloaded.kept")}` : ""}</Banner>}
      {conflict && (
        <Banner kind="warning" testId="mfapolicy-conflict">{t("mfapolicy.conflict")} <button type="button" className="link" disabled={busy} onClick={() => void reload()} data-testid="mfapolicy-reload">{t("mfapolicy.conflict.reload")}</button></Banner>
      )}
      {error != null && <Banner kind="error" testId="mfapolicy-error">{errorText(t, error, "mfapolicy.error.")}</Banner>}
      {rolesRefreshed !== null && <Banner kind="info" testId="mfapolicy-roles-refreshed">{t("mfapolicy.roles_refreshed")}{rolesRefreshed > 0 ? ` ${t("mfapolicy.roles_dropped", { count: rolesRefreshed })}` : ""}</Banner>}
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
                    <bdi>{r.name}</bdi>
                  </label>
                ))}
                {roles !== null && chosen.filter((id) => !available.has(id)).map((id) => (
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
                <button type="button" className="ghost" disabled={busy || !dirty} onClick={() => { setKind(saved.policy); setChosen(saved.roleIds); setError(null); setNeedOwnEnrolment(false); setRolesRefreshed(null); setNotice(null); }} data-testid="mfapolicy-cancel">{t("mfapolicy.cancel")}</button>
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
