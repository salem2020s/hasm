# HASM — Phase UI-1: Visual Design & UX Foundation

Status: **implemented; owner-approved (Checkpoints A, B, C and the later refinements).** Delivered on `feature/ui-1-design-foundation` as a frontend-only change
(plus one Tauri capability line, see "Windows desktop window close"). No backend code, database schema, API contract or CI expectation changed.
Reference mockups: `docs/ui-mockups/` (visual reference only; they do not show implemented modules). The official logo assets in `assets/branding/` remain the branding authority.

## What was delivered
- **A. Foundation:** design tokens, direction-aware application shell with the vertical cascading sidebar, inline SVG icon set.
- **B. Existing screens:** Login, Company Selection, Company Details & Settings (former Profile and Settings), Currencies, Configuration History in the HASM style; base-currency presentation (current base vs explicit change action); date formatting from the company settings.
- **C. Dashboard and module visuals:** dashboard, shared visual components, module illustrations, development-only design previews.
- **Refinements:** compact enterprise density, light-mint authentication background, company/building icon on Company Selection, unified Company Details & Settings page, unsaved-changes safeguard (Web and Windows desktop close).

## Visual direction
Dark teal sidebar, light neutral working surfaces, teal/green primary, restrained gold accents, soft shadows. Authentication screens (Login, Company Selection) use the approved **light mint** composition: mint field, turquoise glows and waves, a thin gold curve, fine white/light-gray arcs (one small static inline SVG plus CSS gradients, no bitmaps, no animation).

## Design tokens
`src/frontend/packages/ui/src/tokens.css` is the single source (colour, typography, spacing, radius, shadow, focus, control heights, layout sizes).
Primary `#0F766E`, secondary `#14B8A6`, sidebar `#0B474A`, gold `#F59E0B`, mint `#E6F4F1`, surface `#F3F6F8`, text `#1F2937`, wordmark navy `#1B2A4E` (estimated from the wordmark, not pixel-sampled).
**Compact scale** (individual token steps, never a global zoom or `transform: scale`): text 12/13/14/15/16/17/20/24 px; controls 38 px (login and company selection 40 px); sidebar 240 px; page headings 24 px. Arabic keeps a taller line height (1.65 vs 1.5).

## Typography
Local system fonts only; no font download. English: Segoe UI. Arabic (`:lang(ar)`): Segoe UI Variable Text, then Tahoma.

## RTL / LTR rules
- `<html lang dir>` follows the user's interface language (existing `I18nProvider`), separate from the company's default language.
- The shell is a CSS grid whose first column is the sidebar, so it sits at the inline-start edge: **right in Arabic, left in English**. No per-language markup. Switching language re-renders in place: route, company, session **and unsaved form edits** are preserved.
- Logical properties throughout; chevrons and decorative graphics mirror under `:root[dir="rtl"]` (the logo, text and cards never mirror).
- Submenus fly out toward the content. Keyboard: Up/Down move; the arrow facing the content opens a submenu; Escape closes.
- Values that must not be reordered (codes, emails, URLs, numbers, currency codes, dates) use `.ltr-value` or `<bdi>`.

## Navigation (SRS line 118 / D-027, unchanged)
Hover expands, click or Enter navigates, hover never navigates. Permissions filter destinations and routes remain permission-checked server-side and client-side.
Unimplemented modules (Accounting, Warehouses, Sales, Purchases, Reports, system Settings) are disabled "Coming soon" items with no route and no production page; the status is in the accessible name and a hover tooltip, not a badge on every row.
Company Management contains **Company Details & Settings**, Currencies and Change history.

## Company Details & Settings (unified page)
`/company/details` (Company Details tab) and `/company/details/general` (General Settings tab); the old `/company/profile` and `/company/settings` redirect to them.
The two sections are the former pages, unchanged in behaviour: each keeps its own permission (`company.profile.view` / `company.settings.view`), revision and optimistic concurrency, validation and Save. A user who may see only one section sees only it (no tabs). Both sections stay mounted (the inactive one is hidden and `inert`), so switching tabs or language never discards edits; a tab shows a marker when it has unsaved edits.

## Permanent UI standard: tabs and screen organization (owner-approved)
* Every screen with several meaningful groups of related information is evaluated for tabs; simple screens get none. Group by business meaning, not field counts. Concise, accurately translated titles (Arabic and English). No nested tabs unless genuinely necessary. Menus separate modules, tabs divide sections of ONE screen, step navigation is for sequential workflows. No placeholder tabs for deferred features.
* One shared component, `Tabs` (`packages/ui/src/Tabs.tsx`, styles `.tabs`/`.tab` in `app.css` on the UI-1 tokens), used by Web and Tauri alike: tablist/tab/tabpanel semantics, roving tabindex, Left/Right (mirrored in RTL), Home/End, disabled tabs skipped; all panels stay mounted (inactive ones `hidden` + `inert`) so switching never saves, discards or resets input; per-tab `invalid` (error mark + text) and `dirty` (unsaved marker) indicators; the strip scrolls horizontally on narrow windows. Company Details & Settings predates it (same markup and CSS) and is migrated when next revised.
* Editor screens keep the record identity (which item/entity is edited) and Save/Cancel in a fixed place outside the tab panels, reachable from every tab. Tests: `Tabs.test.tsx` (switching, preserved data, indicators, keyboard in LTR/RTL, disabled, controlled use, Tauri shell) plus each screen's own tests. Existing complex screens are reviewed and adopt the standard incrementally when next developed; no application-wide redesign.

## Unsaved-changes safeguard
Sections report unsaved edits independently (`unsaved.tsx`, `router.ts`). Leaving the page by menu navigation, Back/Forward or a typed address, **Change company** or **Sign out** asks "Stay on this page" / "Discard changes" (Stay is focused; Escape stays). The dialog names the sections only, never field values. Nothing is saved automatically. Nothing is asked when nothing is unsaved.
- **Web:** browser reload/close uses the native `beforeunload` confirmation, only while something is unsaved.
- **Windows desktop window close (X button, Alt+F4, taskbar close):** the app registers for the native Tauri 2 close request through the official `@tauri-apps/api` window API (loaded lazily, only inside Tauri; separate 15 KB chunk, never loaded on the Web). With unsaved edits the close is prevented and the same dialog is shown; **Discard changes** calls `destroy()` (which does not raise another close request, so there is no loop); a second X while the dialog is open is ignored (no duplicate dialogs). Without unsaved edits the app closes normally. The window capability gained one permission, `core:window:allow-destroy`; nothing else about the Tauri security configuration (CSP, capabilities, transport) changed.

## Company selection and sign-in
- After login (and on session restore with no company selected), **exactly one accessible company is entered automatically** through the normal `select-company` API (the server re-checks membership); more than one, or none, shows Company Selection; a failed automatic entry falls back to Company Selection (never treated as "no company"). **Change company** always opens Company Selection and never auto-selects.
- **Add New Company** is visible but **not operational** (company creation and license enforcement are not implemented): `aria-disabled`, "Not available yet", with the Trial one-company rule stated as text. See `docs/COMPANY-CREATION-REQUIREMENTS.md`.
- **Saved Default Company is NOT implemented.** It needs a backend extension (new per-user table and API) awaiting owner approval; see `docs/PROPOSAL-DEFAULT-COMPANY.md`. No migration was applied.

## Dashboard
Real company information only: company, base currency (from the Currencies API), edition (from the entitlements API), quick access to permitted screens. The setup checklist and its KPI appear **only while setup is incomplete**; when complete they disappear and a **Recent activity** card (last five Configuration History entries: item, changed field names, date; never values) appears for users with `company.config.view`. Future sales/inventory widgets are identified empty states with no figures.

## Shared components and assets
`AppShell`, vertical `CascadingMenu`, inline SVG icons, `ModuleHero`, `ModuleIllustration` (six lightweight inline SVG illustrations), `DashboardCard`, `MetricCard`, `QuickActionCard`, `EmptyState`, `ResponsiveTable`, `PageHeader`, `Card`, `ConfirmDialog`. Display formatting (`format.ts`): dates, date-time, numbers, money display, numeral preference, company time zone (display only; stored values stay UTC/exact).

## Development-only design previews
Warehouses and Sales Invoices previews and a module-header gallery under `/dev/preview*`, with a "Design previews (DEV)" menu group. They are behind the compile-time `import.meta.env.DEV` constant, so **they are absent from production builds** (verified by searching the production bundle and the bundle used by the release desktop executable). They make no API calls, hold no state and every value is labelled as sample design content.

## Performance (production build)
| | JS (gzip) | CSS (gzip) |
|---|---|---|
| Baseline before UI-1 | 273.6 KB | 4.7 KB |
| Final | 312.5 KB (94.0) + 15 KB lazy Tauri window chunk (desktop only) | 24.6 KB (5.1) |

No fonts, no raster graphics beyond the existing logo, no animation, no blur filters. Reduced-motion respected via `--transition`.

## Verification summary
Frontend: typecheck clean; 97 web, 10 API-client and 11 UI tests pass (RBAC, authentication/session, navigation, unsaved-changes and window-close tests included). Web and Windows desktop were exercised against the real running API in Arabic and English (login, company entry, unified page, saves, unsaved dialogs, Windows X close, layouts at typical and minimum sizes). Tauri debug and release builds compile. PostgreSQL integration tests were **not** run against the permanent development server.

## Known limitations
- Narrow windows (<48rem) stack the sidebar above the content; Tauri minimum width is 960 px.
- Header search/notifications are intentionally absent until those features exist.
- Saved Default Company and company creation are pending approved backend work (see the two proposal documents).
- Browser Back after a blocked navigation can leave one extra history entry (the address is restored with a replace).
