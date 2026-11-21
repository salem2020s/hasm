import { createApiClient, type ApiClient, type SessionGate } from "@hasm/api-client";
import { AccountingIcon, AppShell, CascadingMenu, CompanyIcon, HomeIcon, LanguageSwitcher, PurchasesIcon, ReportsIcon, SalesIcon, SettingsIcon, WarehouseIcon, useI18n, type MenuItem } from "@hasm/ui";
import "@hasm/ui/src/tokens.css";
import { lazy, Suspense, useEffect, useMemo, type ReactNode } from "react";
// Official branding assets, used unmodified (see assets/branding/BRANDING.md).
import markUrl from "../../../../../assets/branding/hasm-mark.png";
import { AuthProvider, createSessionBridge, useAuth, type SessionBridge } from "./auth";
import { Banner, Loading } from "./components";
import { apiBaseUrl } from "./config";
import { isTauri } from "./platform";
import { ReauthDialog } from "./reauth";
import { StepUpHost, createStepUpBridge, type StepUpBridge } from "./stepUp";
import { SecurityPage } from "./pages/Security";
import { BranchesPage } from "./pages/Branches";
import { WarehousesPage } from "./pages/Warehouses";
import { ImportsPage } from "./pages/Imports";
import { FinancialStatementsPage } from "./pages/FinancialStatements";
import { StatementMappingPage } from "./pages/StatementMapping";
import { ReportCenterPage } from "./pages/ReportCenter";
import { InventoryItemsPage } from "./pages/InventoryItems";
import { InventoryGroupsPage } from "./pages/InventoryGroups";
import { InventoryUnitsPage } from "./pages/InventoryUnits";
import { CommercialDocumentsPage } from "./pages/CommercialDocuments";
import { StockDocumentsPage } from "./pages/StockDocuments";
import { InventoryAccountingPage } from "./pages/InventoryAccounting";
import { StockBalancesPage } from "./pages/StockBalances";
import { StocktakesPage } from "./pages/Stocktakes";
import { NegativeStockPage } from "./pages/NegativeStock";
import { PartiesPage } from "./pages/Parties";
import { PartyApprovalsPage } from "./pages/PartyApprovals";
import { FiscalYearsPage } from "./pages/FiscalYears";
import { ChartOfAccountsPage } from "./pages/ChartOfAccounts";
import { ApprovalPoliciesPage } from "./pages/ApprovalPolicies";
import { CostCentersPage } from "./pages/CostCenters";
import { CostCenterPoliciesPage } from "./pages/CostCenterPolicies";
import { PricingPage } from "./pages/Pricing";
import { AttachmentRetentionPage } from "./pages/AttachmentRetention";
import { TaxesPage } from "./pages/Taxes";
import { PartyRolesPage } from "./pages/PartyRoles";
import { FiscalClosingPage } from "./pages/FiscalClosing";
import { ExchangeRatesPage } from "./pages/ExchangeRates";
import { JournalPage } from "./pages/Journal";
import { LedgerReportsPage } from "./pages/LedgerReports";
import { NumberingPage } from "./pages/Numbering";
import { CompanySelectPage } from "./pages/CompanySelect";
import { CurrenciesPage } from "./pages/Currencies";
import { HistoryPage } from "./pages/History";
import { HomePage } from "./pages/Home";
import { LoginPage } from "./pages/Login";
import { CompanyDetailsPage } from "./pages/CompanyDetails";
import { navigate, useRoute } from "./router";
import { UnsavedProvider, useUnsaved } from "./unsaved";
import "./app.css";
import "./visual/visual.css";

/** The Tauri window and the shared web app run THIS build; only the session transport differs (bearer in memory vs HttpOnly cookie). */
export { isTauri };

interface RouteDef { path: string; /** Central (cross-company) screen: identity administrators only; the server enforces it again. */ centralAdmin?: boolean; permission?: string; /** Allowed when ANY of these is held (each section inside still enforces its own permission). */ anyOf?: string[]; /** Allowed when ANY grant (even for a single branch) holds this permission; the server still decides per branch. */ anyScope?: string; element: ReactNode }

/** Former pages that now live inside the unified Company Details & Settings page (old bookmarks keep working). */
const REDIRECTS: Record<string, string> = { "/company/profile": "/company/details", "/company/settings": "/company/details/general" };

/**
 * DEVELOPMENT-ONLY design previews (Warehouses, Sales Invoices, gallery). Everything here sits behind the compile-time constant
 * import.meta.env.DEV, which is false in production builds, so the preview code, routes and menu entries are removed from the bundle entirely.
 */
const DevPreviews = import.meta.env.DEV ? {
  gallery: lazy(() => import("./previews/Gallery")),
  warehouses: lazy(() => import("./previews/Warehouses")),
  sales: lazy(() => import("./previews/SalesInvoices")),
} : null;

const ROUTES: RouteDef[] = [
  { path: "/", element: <HomePage /> },
  { path: "/account/security", element: <SecurityPage /> },                      // the user's OWN security settings: every signed-in user, no company permission
  // Both paths render the same element type in the same place, so switching tabs keeps the page (and unsaved edits) mounted.
  { path: "/company/details", anyOf: ["company.profile.view", "company.settings.view"], element: <CompanyDetailsPage /> },
  { path: "/company/details/general", anyOf: ["company.profile.view", "company.settings.view"], element: <CompanyDetailsPage /> },
  { path: "/company/currencies", permission: "company.currencies.view", element: <CurrenciesPage /> },
  { path: "/company/branches", anyScope: "company.branches.view", element: <BranchesPage /> },
  { path: "/company/warehouses", anyScope: "company.warehouses.view", element: <WarehousesPage /> },
  { path: "/company/fiscal-years", permission: "company.fiscalyears.view", element: <FiscalYearsPage /> },
  { path: "/company/accounts", permission: "company.accounts.view", element: <ChartOfAccountsPage /> },
  { path: "/company/numbering", permission: "company.numbering.view", element: <NumberingPage /> },
  { path: "/company/imports", permission: "company.imports.view", element: <ImportsPage /> },
  { path: "/company/exchange-rates", permission: "company.exchangerates.view", element: <ExchangeRatesPage /> },
  { path: "/company/cost-centers", permission: "company.costcenters.view", element: <CostCentersPage /> },
  { path: "/company/cost-center-policies", permission: "company.costcenterpolicies.view", element: <CostCenterPoliciesPage /> },
  { path: "/company/party-roles", anyOf: ["company.partyroles.view", "company.salesreps.view"], element: <PartyRolesPage /> },
  { path: "/company/taxes", permission: "company.tax.view", element: <TaxesPage /> },
  { path: "/company/pricing", permission: "company.pricing.view", element: <PricingPage /> },
  { path: "/company/attachment-retention", permission: "company.attachmentretention.view", element: <AttachmentRetentionPage /> },
  { path: "/company/fiscal-closing", permission: "company.fiscalclosing.view", element: <FiscalClosingPage /> },
  { path: "/company/approval-policies", permission: "company.approvalpolicies.view", element: <ApprovalPoliciesPage /> },
  { path: "/accounting/journal", anyScope: "company.journal.view", element: <JournalPage /> },
  { path: "/accounting/reports", anyScope: "company.ledgerreports.view", element: <LedgerReportsPage /> },
  { path: "/accounting/report-center", anyScope: "company.ledgerreports.view", element: <ReportCenterPage /> },
  { path: "/inventory/accounting", permission: "company.inventoryaccounting.view", element: <InventoryAccountingPage /> },
  { path: "/inventory/stock-balances", anyScope: "company.stockbalance.view", element: <StockBalancesPage /> },
  { path: "/inventory/stock-documents", anyScope: "company.stockdocs.view", element: <StockDocumentsPage /> },
  { path: "/inventory/stocktakes", anyScope: "company.stocktake.view", element: <StocktakesPage /> },
  { path: "/inventory/negative-stock", anyScope: "company.stockdocs.view", element: <NegativeStockPage /> },
  { path: "/inventory/items", permission: "company.items.view", element: <InventoryItemsPage /> },
  { path: "/inventory/groups", anyOf: ["company.itemgroups.view", "company.items.view"], element: <InventoryGroupsPage /> },
  { path: "/inventory/units", anyOf: ["company.units.view", "company.items.view"], element: <InventoryUnitsPage /> },
  { path: "/commercial/documents", anyScope: "company.commercialdocs.view", element: <CommercialDocumentsPage /> },
  { path: "/accounting/statements", anyScope: "company.financialstatements.view", element: <FinancialStatementsPage /> },
  { path: "/accounting/statement-mapping", permission: "company.statementmapping.view", element: <StatementMappingPage /> },
  { path: "/company/history", permission: "company.config.view", element: <HistoryPage /> },
  { path: "/company/parties", permission: "company.parties.view", element: <PartiesPage /> },
  { path: "/admin/party-proposals", centralAdmin: true, element: <PartyApprovalsPage /> },
  ...(DevPreviews ? [
    { path: "/dev/preview", element: <Suspense fallback={<Loading />}><DevPreviews.gallery /></Suspense> },
    { path: "/dev/preview/warehouses", element: <Suspense fallback={<Loading />}><DevPreviews.warehouses /></Suspense> },
    { path: "/dev/preview/sales-invoices", element: <Suspense fallback={<Loading />}><DevPreviews.sales /></Suspense> },
  ] : []),
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
  const { t, locale } = useI18n();
  const auth = useAuth();
  const route = useRoute();
  const { guard } = useUnsaved();

  const menu: MenuItem[] = useMemo(() => {
    const company: MenuItem[] = [
      { id: "/company/details", label: t("nav.details"), permission: "company.profile.view|company.settings.view" },
      { id: "/company/currencies", label: t("nav.currencies"), permission: "company.currencies.view" },
      { id: "/company/branches", label: t("nav.branches"), permission: "company.branches.view", anyScope: true },
      { id: "/company/warehouses", label: t("nav.warehouse_mgmt"), permission: "company.warehouses.view", anyScope: true },
      { id: "/company/fiscal-years", label: t("nav.fiscal"), permission: "company.fiscalyears.view" },
      { id: "/company/accounts", label: t("nav.accounts"), permission: "company.accounts.view" },
      { id: "/company/numbering", label: t("nav.numbering"), permission: "company.numbering.view" },
      { id: "/company/imports", label: t("nav.imports"), permission: "company.imports.view" },
      { id: "/company/exchange-rates", label: t("nav.exchange_rates"), permission: "company.exchangerates.view" },
      { id: "/company/cost-centers", label: t("nav.cost_centers"), permission: "company.costcenters.view" },
      { id: "/company/cost-center-policies", label: t("nav.cost_center_policies"), permission: "company.costcenterpolicies.view" },
      { id: "/company/party-roles", label: t("nav.party_roles"), permission: "company.partyroles.view|company.salesreps.view" },
      { id: "/company/taxes", label: t("nav.taxes"), permission: "company.tax.view" },
      { id: "/company/pricing", label: t("nav.pricing"), permission: "company.pricing.view" },
      { id: "/company/attachment-retention", label: t("nav.attachment_retention"), permission: "company.attachmentretention.view" },
      { id: "/company/fiscal-closing", label: t("nav.fiscal_closing"), permission: "company.fiscalclosing.view" },
      { id: "/company/approval-policies", label: t("nav.approval_policies"), permission: "company.approvalpolicies.view" },
      { id: "/company/parties", label: t("nav.parties"), permission: "company.parties.view" },
      { id: "/company/history", label: t("nav.history"), permission: "company.config.view" },
    ].filter((i) => i.permission.split("|").some((p) => auth.can(p) || (i.anyScope === true && auth.canAny(p)))).map(({ id, label }) => ({ id, label }));
    if (auth.user?.isIdentityAdministrator) company.push({ id: "/admin/party-proposals", label: t("nav.party_approvals") });
    const accounting: MenuItem[] = [
      { id: "/accounting/journal", label: t("nav.journal"), permission: "company.journal.view" },
      { id: "/accounting/reports", label: t("nav.gl_reports"), permission: "company.ledgerreports.view" },
      { id: "/accounting/report-center", label: t("nav.report_center"), permission: "company.ledgerreports.view", anyScope: true },
      { id: "/accounting/statements", label: t("nav.statements"), permission: "company.financialstatements.view", anyScope: true },
      { id: "/accounting/statement-mapping", label: t("nav.statement_mapping"), permission: "company.statementmapping.view" },
    ].filter((i) => auth.can(i.permission) || auth.canAny(i.permission)).map(({ id, label }) => ({ id, label }));
    const inventory: MenuItem[] = [
      { id: "/inventory/items", label: t("nav.inventory_items"), permission: "company.items.view" },
      { id: "/inventory/groups", label: t("nav.inventory_groups"), permission: "company.itemgroups.view|company.items.view" },
      { id: "/inventory/units", label: t("nav.inventory_units"), permission: "company.units.view|company.items.view" },
    ].filter((i) => i.permission.split("|").some((p) => auth.can(p))).map(({ id, label }) => ({ id, label }));
    if (auth.can("company.inventoryaccounting.view")) inventory.push({ id: "/inventory/accounting", label: t("nav.inventory_accounting") });
    if (auth.canAny("company.stockdocs.view")) inventory.push({ id: "/inventory/stock-documents", label: t("nav.stock_documents") });
    if (auth.canAny("company.stockbalance.view")) inventory.push({ id: "/inventory/stock-balances", label: t("nav.stock_balances") });
    if (auth.canAny("company.stocktake.view")) inventory.push({ id: "/inventory/stocktakes", label: t("nav.stocktakes") });
    if (auth.canAny("company.stockdocs.view")) inventory.push({ id: "/inventory/negative-stock", label: t("nav.negative_stock") });
    // Commercial documents (3.13 S6, D-079): shown to anyone who may view them in any scope; the screen itself reports "no document types activated" until the concrete types are activated (3.15/3.16).
    const commercial: MenuItem[] = auth.canAny("company.commercialdocs.view") || auth.can("company.commercialdocs.view") ? [{ id: "/commercial/documents", label: t("nav.commercial_documents") }] : [];
    // Modules without an implemented business feature are shown disabled with a "Coming soon" badge: no route, no page, never navigable.
    const soon = (id: string, label: string, icon: MenuItem["icon"]): MenuItem => ({ id: `soon:${id}`, label, icon, disabled: true, badge: t("nav.coming_soon") });
    return [
      { id: "/", label: t("nav.home"), icon: <HomeIcon /> },
      ...(accounting.length ? [{ id: "accounting", label: t("nav.accounting"), icon: <AccountingIcon />, children: accounting } as MenuItem] : [soon("accounting", t("nav.accounting"), <AccountingIcon />)]),
      ...(inventory.length ? [{ id: "inventory", label: t("nav.inventory"), icon: <WarehouseIcon />, children: inventory } as MenuItem] : [soon("warehouses", t("nav.warehouses"), <WarehouseIcon />)]),
      ...(commercial.length ? [{ id: "commercial", label: t("nav.commercial"), icon: <SalesIcon />, children: commercial } as MenuItem] : []),
      soon("sales", t("nav.sales"), <SalesIcon />),
      soon("purchases", t("nav.purchases"), <PurchasesIcon />),
      soon("reports", t("nav.reports"), <ReportsIcon />),
      ...(company.length ? [{ id: "company", label: t("nav.company"), icon: <CompanyIcon />, children: company }] : []),
      soon("settings", t("nav.system_settings"), <SettingsIcon />),
      // Development builds only: design previews of future modules (not part of the product; absent from production builds).
      ...(import.meta.env.DEV ? [{
        id: "dev", label: locale === "ar" ? "معاينات التصميم (تطوير)" : "Design previews (DEV)", icon: <SettingsIcon />,
        children: [
          { id: "/dev/preview", label: locale === "ar" ? "معرض التصميم" : "Design gallery" },
          { id: "/dev/preview/warehouses", label: locale === "ar" ? "معاينة المستودعات" : "Warehouses preview" },
          { id: "/dev/preview/sales-invoices", label: locale === "ar" ? "معاينة فواتير المبيعات" : "Sales invoices preview" },
        ],
      } as MenuItem] : []),
    ];
  }, [t, auth, locale]);

  // Move focus to the new page content on navigation (keyboard and screen-reader users).
  useEffect(() => { document.getElementById("content")?.focus({ preventScroll: true }); }, [route]);

  // Old Profile / Settings routes redirect (replace, so Back does not bounce) to the unified page.
  useEffect(() => { const to = REDIRECTS[route]; if (to) window.location.replace(`#${to}`); }, [route]);

  const def = ROUTES.find((r) => r.path === route);
  // Protected navigation: a route is rendered only when the permission is held; the server enforces the same rule independently.
  const content = !def ? <Banner kind="error" testId="not-found">{t("error.not_found")}</Banner>
    : (def.permission && !auth.can(def.permission)) || (def.anyOf && !def.anyOf.some((p) => auth.can(p))) || (def.anyScope && !auth.canAny(def.anyScope)) || (def.centralAdmin && !auth.user?.isIdentityAdministrator) ? <Banner kind="error" testId="forbidden">{t("error.forbidden")}</Banner>
    : def.element;

  return (
    <AppShell
      brand={<Brand />}
      nav={<CascadingMenu orientation="vertical" items={menu} label={t("nav.main")} currentId={route.startsWith("/company/details") ? "/company/details" : route} onSelect={(i) => navigate(i.id)} />}
      header={
        <div className="header-tools">
          {/* <bdi> isolates the company name so its own text direction is never reordered by the page direction. */}
          {auth.companyName && <span className="company-name" data-testid="company-name"><bdi>{auth.companyName}</bdi></span>}
          <button type="button" className="ghost" onClick={() => guard(() => void auth.changeCompany())}>{t("nav.change_company")}</button>
          <span className="user-name">{auth.user?.displayName}</span>
          <button type="button" className="ghost" data-testid="nav-security" aria-current={route === "/account/security" ? "page" : undefined} onClick={() => navigate("/account/security")}>{t("nav.security")}</button>
          <LanguageSwitcher />
          <button type="button" className="ghost" onClick={() => guard(() => void auth.logout())}>{t("nav.logout")}</button>
        </div>
      }
    >
      <main id="content" className="content" tabIndex={-1}>{content}</main>
    </AppShell>
  );
}

/** Decorative background (aria-hidden, static inline SVG, no images/animation): soft turquoise waves, a thin gold curve and fine white / light-gray arcs on a mint field. Mirrored in RTL by CSS. */
function AuthDecor() {
  return (
    <svg className="auth-deco" viewBox="0 0 1200 800" preserveAspectRatio="xMidYMid slice" aria-hidden="true" focusable="false">
      <defs>
        <linearGradient id="auth-wave-a" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stopColor="#14b8a6" stopOpacity="0.38" /><stop offset="0.55" stopColor="#14b8a6" stopOpacity="0.14" /><stop offset="1" stopColor="#14b8a6" stopOpacity="0.4" /></linearGradient>
        <linearGradient id="auth-wave-b" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stopColor="#14b8a6" stopOpacity="0.5" /><stop offset="0.5" stopColor="#14b8a6" stopOpacity="0.22" /><stop offset="1" stopColor="#0f766e" stopOpacity="0.75" /></linearGradient>
      </defs>
      <g fill="none" strokeWidth="1.2">
        <circle cx="60" cy="40" r="250" stroke="#fff" strokeOpacity="0.85" />
        <circle cx="60" cy="40" r="320" stroke="#fff" strokeOpacity="0.6" />
        <circle cx="60" cy="40" r="390" stroke="#fff" strokeOpacity="0.4" />
        <circle cx="1170" cy="30" r="230" stroke="#b6c0c9" strokeOpacity="0.45" />
        <circle cx="1170" cy="30" r="300" stroke="#b6c0c9" strokeOpacity="0.3" />
      </g>
      <path d="M-60 520 C 180 470, 330 640, 600 690 S 1020 560, 1300 560 L1300 900 L-60 900 Z" fill="url(#auth-wave-a)" />
      <path d="M-60 650 C 220 600, 420 780, 720 760 S 1100 650, 1300 700 L1300 900 L-60 900 Z" fill="url(#auth-wave-b)" />
      <path d="M760 900 C 900 820, 1080 800, 1300 770 L1300 900 Z" fill="#0f766e" fillOpacity="0.85" />
      <g fill="none" strokeLinecap="round">
        <path d="M-60 600 C 200 590, 420 790, 700 790 S 1060 560, 1300 560" stroke="#f59e0b" strokeWidth="2.4" />
        <path d="M-60 690 C 180 660, 300 700, 480 720" stroke="#fff" strokeOpacity="0.8" strokeWidth="1.4" />
        <path d="M820 640 C 980 600, 1120 560, 1300 540" stroke="#fff" strokeOpacity="0.8" strokeWidth="1.4" />
        <path d="M-60 560 C 160 540, 340 600, 520 650" stroke="#b6c0c9" strokeOpacity="0.5" strokeWidth="1.2" />
      </g>
    </svg>
  );
}

/** Pre-sign-in layout (login, company selection): centred card on the brand backdrop, language switch always reachable. */
function AuthLayout({ children, language = true }: { children: ReactNode; language?: boolean }) {
  return (
    <div className="auth-page">
      <AuthDecor />
      {language && <div className="auth-lang"><LanguageSwitcher /></div>}
      <main className="auth-card">
        <div className="auth-brand"><Brand /></div>
        {children}
      </main>
    </div>
  );
}

function Gate() {
  const { t } = useI18n();
  const auth = useAuth();
  if (auth.phase === "loading") return <AuthLayout language={false}><Loading /></AuthLayout>;
  if (auth.phase === "unreachable") {
    return (
      <AuthLayout>
        <Banner kind="error" testId="unreachable">{t("error.unreachable")}</Banner>
        <button type="button" className="primary block" onClick={() => window.location.reload()}>{t("retry")}</button>
      </AuthLayout>
    );
  }
  if (auth.phase === "anonymous" || auth.phase === "mfa") return <AuthLayout><LoginPage /></AuthLayout>;
  if (auth.phase === "needCompany") return <AuthLayout><CompanySelectPage /></AuthLayout>;
  // D-236: when the session ends with unsaved input the shell stays MOUNTED (its state is the preserved input, in memory only) but is taken away from whoever sits at
  // the screen: `hidden` (not rendered, not printed), `inert` (no focus, no pointer, no keyboard, no clipboard or drag events) and `aria-hidden` (not in the
  // accessibility tree). The sign-in-again dialog is then the only content. `shellEpoch` makes a later shell a NEW instance, never this one.
  return (
    <>
      <div className="shell-root" data-testid="shell-root" hidden={auth.locked} inert={auth.locked} aria-hidden={auth.locked || undefined}><Shell key={auth.shellEpoch} /></div>
      {auth.locked && <ReauthDialog frame={(children) => <AuthLayout>{children}</AuthLayout>} />}
    </>
  );
}

/** The skip link targets the shell's content, so it does not exist while the shell is locked away (D-236). */
function SkipLink() {
  const { t } = useI18n();
  const auth = useAuth();
  if (auth.locked) return null;
  return <a className="skip-link" href="#content" onClick={(e) => { e.preventDefault(); document.getElementById("content")?.focus(); }}>{t("skip_to_content")}</a>;
}

export function App({ client, bridge, stepUpBridge, gate }: { client?: ApiClient; bridge?: SessionBridge; stepUpBridge?: StepUpBridge; /** Tests only: observe the session gate. */ gate?: SessionGate }) {
  const own = useMemo(() => {
    const step = stepUpBridge ?? createStepUpBridge();
    if (client) return { api: client, bridge, step };
    const b = createSessionBridge();
    return { api: createApiClient(apiBaseUrl, undefined, { client: isTauri() ? "desktop" : "web", onUnauthenticated: b.fire, stepUp: step.request }), bridge: b, step };
  }, [client, bridge, stepUpBridge]);
  return (
    <AuthProvider api={own.api} bridge={own.bridge} gate={gate}>
      <SkipLink />
      <UnsavedProvider><Gate /></UnsavedProvider>
      <StepUpHost bridge={own.step} />
    </AuthProvider>
  );
}
