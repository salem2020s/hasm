import { useI18n } from "@hasm/ui";
import { useCallback, useState, type KeyboardEvent } from "react";
import { useAuth } from "../auth";
import { PageHeader } from "../components";
import { useReportUnsaved } from "../unsaved";
import { navigate, useRoute } from "../router";
import { ProfilePage } from "./Profile";
import { SettingsPage } from "./Settings";

export const DETAILS_PATH = "/company/details";
export const GENERAL_PATH = "/company/details/general";
type Tab = "profile" | "settings";

/**
 * Company Details & Settings: the former Company Profile and Company Settings pages as two sections of one page. Nothing about them is
 * merged: each section keeps its own permission (profile.view / settings.view), its own revision and optimistic-concurrency handling, its own
 * validation and its own Save. Both sections stay mounted while the other is shown (the inactive one is only hidden), so switching tabs never
 * discards unsaved edits. A user who may see only one section sees only that section (no tabs).
 */
export function CompanyDetailsPage() {
  const { t } = useI18n();
  const auth = useAuth();
  const route = useRoute();
  const canProfile = auth.can("company.profile.view");
  const canSettings = auth.can("company.settings.view");
  const wanted: Tab = route === GENERAL_PATH ? "settings" : "profile";
  const active: Tab = wanted === "settings" ? (canSettings ? "settings" : "profile") : (canProfile ? "profile" : "settings");
  const [dirty, setDirty] = useState({ profile: false, settings: false });
  const onProfileDirty = useCallback((d: boolean) => setDirty((s) => (s.profile === d ? s : { ...s, profile: d })), []);
  const onSettingsDirty = useCallback((d: boolean) => setDirty((s) => (s.settings === d ? s : { ...s, settings: d })), []);

  // Unsaved edits of each section are reported independently to the safeguard (navigation, Change company, Sign out, reload).
  useReportUnsaved("profile", dirty.profile, t("details.tab_details"));
  useReportUnsaved("settings", dirty.settings, t("details.tab_settings"));

  const go = (tab: Tab) => navigate(tab === "settings" ? GENERAL_PATH : DETAILS_PATH);
  const onKey = (e: KeyboardEvent) => {
    if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(e.key)) {
      e.preventDefault();
      const next: Tab = e.key === "Home" ? "profile" : e.key === "End" ? "settings" : active === "profile" ? "settings" : "profile";
      go(next);
      requestAnimationFrame(() => document.getElementById(`tab-${next}`)?.focus());
    }
  };
  const tab = (id: Tab, label: string) => (
    <button type="button" role="tab" id={`tab-${id}`} aria-selected={active === id} aria-controls={`panel-${id}`} tabIndex={active === id ? 0 : -1}
      className={`tab${active === id ? " is-active" : ""}`} onClick={() => go(id)} onKeyDown={onKey}>
      {label}
      {dirty[id] && <><span className="tab-dot" aria-hidden="true" /><span className="visually-hidden"> ({t("details.unsaved")})</span></>}
    </button>
  );

  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("details.title")} description={t("details.subtitle")} />
      {canProfile && canSettings && (
        <div role="tablist" aria-label={t("details.title")} className="tabs">
          {tab("profile", t("details.tab_details"))}
          {tab("settings", t("details.tab_settings"))}
        </div>
      )}
      {canProfile && (
        <div role="tabpanel" id="panel-profile" aria-labelledby={canSettings ? "tab-profile" : "page-title"} hidden={active !== "profile"} inert={active !== "profile"} className="tab-panel">
          <ProfilePage embedded onDirty={onProfileDirty} />
        </div>
      )}
      {canSettings && (
        <div role="tabpanel" id="panel-settings" aria-labelledby={canProfile ? "tab-settings" : "page-title"} hidden={active !== "settings"} inert={active !== "settings"} className="tab-panel">
          <SettingsPage embedded onDirty={onSettingsDirty} />
        </div>
      )}
    </section>
  );
}
