import { createApiClient, type ApiClient } from "@hasm/api-client";
import { CascadingMenu, LanguageSwitcher, useI18n, type MenuItem } from "@hasm/ui";
import { useEffect, useMemo, type ReactNode } from "react";
// Official branding assets, used unmodified (see assets/branding/BRANDING.md).
import markUrl from "../../../../../assets/branding/hasm-mark.png";
import { AuthProvider, createSessionBridge, useAuth, type SessionBridge } from "./auth";
import { Banner, Loading } from "./components";
import { apiBaseUrl } from "./config";
import { CompanySelectPage } from "./pages/CompanySelect";
import { CurrenciesPage } from "./pages/Currencies";
import { HistoryPage } from "./pages/History";
import { HomePage } from "./pages/Home";
import { LoginPage } from "./pages/Login";
import { ProfilePage } from "./pages/Profile";
import { SettingsPage } from "./pages/Settings";
import { navigate, useRoute } from "./router";
import "./app.css";

/** The Tauri window and the shared web app run THIS build; only the session transport differs (bearer in memory vs HttpOnly cookie). */
export const isTauri = () => typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

interface RouteDef { path: string; permission?: string; element: ReactNode }

const ROUTES: RouteDef[] = [
  { path: "/", element: <HomePage /> },
  { path: "/company/profile", permission: "company.profile.view", element: <ProfilePage /> },
  { path: "/company/settings", permission: "company.settings.view", element: <SettingsPage /> },
  { path: "/company/currencies", permission: "company.currencies.view", element: <CurrenciesPage /> },
  { path: "/company/history", permission: "company.config.view", element: <HistoryPage /> },
];

function Brand() {
  const { t } = useI18n();
  return (
    <div className="brand">
      {/* Decorative: the adjacent text carries the accessible name. Native 120x136 ratio is preserved. */}
      <img className="brand-mark" src={markUrl} alt="" width={120} height={136} />
      <span className="brand-name">{t("appName")}</span>
    </div>
  );
}

function Shell() {
  const { t } = useI18n();
  const auth = useAuth();
  const route = useRoute();

  const menu: MenuItem[] = useMemo(() => {
    const company: MenuItem[] = [
      { id: "/company/profile", label: t("nav.profile"), permission: "company.profile.view" },
      { id: "/company/settings", label: t("nav.settings"), permission: "company.settings.view" },
      { id: "/company/currencies", label: t("nav.currencies"), permission: "company.currencies.view" },
      { id: "/company/history", label: t("nav.history"), permission: "company.config.view" },
    ].filter((i) => auth.can(i.permission)).map(({ id, label }) => ({ id, label }));
    return [{ id: "/", label: t("nav.home") }, ...(company.length ? [{ id: "company", label: t("nav.company"), children: company }] : [])];
  }, [t, auth]);

  // Move focus to the new page content on navigation (keyboard and screen-reader users).
  useEffect(() => { document.getElementById("content")?.focus(); }, [route]);

  const def = ROUTES.find((r) => r.path === route);
  // Protected navigation: a route is rendered only when the permission is held; the server enforces the same rule independently.
  const content = !def ? <Banner kind="error" testId="not-found">{t("error.not_found")}</Banner>
    : def.permission && !auth.can(def.permission) ? <Banner kind="error" testId="forbidden">{t("error.forbidden")}</Banner>
    : def.element;

  return (
    <>
      <header className="app-header">
        <Brand />
        <CascadingMenu items={menu} label={t("nav.main")} currentId={route} onSelect={(i) => navigate(i.id)} />
        <div className="header-tools">
          {auth.companyName && <span className="company-name" data-testid="company-name">{auth.companyName}</span>}
          <button type="button" onClick={() => void auth.changeCompany()}>{t("nav.change_company")}</button>
          <span className="user-name">{auth.user?.displayName}</span>
          <LanguageSwitcher />
          <button type="button" onClick={() => void auth.logout()}>{t("nav.logout")}</button>
        </div>
      </header>
      <main id="content" className="content" tabIndex={-1}>{content}</main>
    </>
  );
}

function Gate() {
  const { t } = useI18n();
  const auth = useAuth();
  if (auth.phase === "loading") return <main className="auth-card"><Loading /></main>;
  if (auth.phase === "unreachable") {
    return (
      <main className="auth-card">
        <Brand />
        <Banner kind="error" testId="unreachable">{t("error.unreachable")}</Banner>
        <button type="button" onClick={() => window.location.reload()}>{t("retry")}</button>
      </main>
    );
  }
  if (auth.phase === "anonymous") {
    return (
      <>
        <header className="app-header"><Brand /><LanguageSwitcher /></header>
        <LoginPage />
      </>
    );
  }
  if (auth.phase === "needCompany") {
    return (
      <>
        <header className="app-header"><Brand /><LanguageSwitcher /></header>
        <CompanySelectPage />
      </>
    );
  }
  return <Shell />;
}

export function App({ client, bridge }: { client?: ApiClient; bridge?: SessionBridge }) {
  const { t } = useI18n();
  const own = useMemo(() => {
    if (client) return { api: client, bridge };
    const b = createSessionBridge();
    return { api: createApiClient(apiBaseUrl, undefined, { client: isTauri() ? "desktop" : "web", onUnauthenticated: b.fire }), bridge: b };
  }, [client, bridge]);
  return (
    <AuthProvider api={own.api} bridge={own.bridge}>
      <a className="skip-link" href="#content" onClick={(e) => { e.preventDefault(); document.getElementById("content")?.focus(); }}>{t("skip_to_content")}</a>
      <Gate />
    </AuthProvider>
  );
}
