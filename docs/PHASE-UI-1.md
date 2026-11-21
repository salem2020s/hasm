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

## Session expiry with unsaved input (owner decision D-236)
Owner text: "Preserve unsubmitted form input in memory only while the session is expired. Hide it from unauthenticated users, restore only after the same user reauthenticates, and clear it on logout or an identity change. Do not store it persistently in the Web application." One implementation in the shared frontend; Web and Windows desktop behave the same (the only difference stays the session transport: cookie or in-memory bearer).
- **When it applies.** The server answers an authenticated call with 401 (not the step-up refusal) while the shell is ready AND at least one section reports unsaved edits to the unsaved-changes safeguard. With nothing unsaved the behaviour is unchanged: the normal sign-in page with the expiry notice.
- **What is preserved, and where.** Nothing is copied. The application shell stays MOUNTED; the preserved input is the React state of the mounted screens, in the memory of that tab or window only. It is never serialized and never written to `localStorage`, `sessionStorage`, a cookie, IndexedDB or disk, on Web or desktop. A reload or closing the tab or window ends it (the existing `beforeunload` / window-close confirmation still asks first).
- **Hidden.** The shell is wrapped in `.shell-root`, which gets `hidden` (not rendered and not printed; `display: none !important`, also under `@media print`), `inert` (no focus, pointer, keyboard, clipboard or drag events) and `aria-hidden`. The skip link is removed, an open Stay / Discard question and an open step-up dialog are withdrawn, navigation (menu, Back/Forward, a typed address) is ignored and the address is put back. The sign-in-again dialog (`reauth.tsx`) is the only focusable and printable content; it shows no user name, no company, no section name and no value. The document title never carries screen content. The application has no polling and no toasts; the only timers in screens are one-shot search debounces, whose calls are refused locally while locked.
- **No request from the hidden shell.** The screens only hold a GATED api client (`createSessionGate`, `gateClient` in `packages/api-client`). The gate is created CLOSED and is opened only after the server has confirmed a session with a company; it closes the moment the session ends. While it is closed every call is refused locally (401 `session_locked`): nothing is sent, and above all nothing can be sent under the session of a different user who signs in at that moment. The session provider signs in through the ungated client. This is a client-side guard, never an authorization: the server decides everything.
- **Sign in again.** The dialog is the ordinary sign-in form: password, then the second factor, an enrolment or the recovery notice exactly as at any sign-in. The password and the code belong to that form, are never part of anything preserved, are cleared after a refusal and disappear with the dialog.
- **Restore: only the same user and the same company.** The identity is the user id the SERVER returns for the new session (never the typed name). For the same id the company the input belongs to is selected on the new session and the access answer is read; only when the server confirms that very company are the permissions replaced, the lock released and, last, the gate opened. A screen whose permission is gone is replaced by the normal no-access rendering, which unmounts it with its input.
- **Drop.** A different user id, a company that can no longer be entered or an access answer for another company, "Discard unsaved input and sign out", or a sign-out in another tab: the shell is unmounted synchronously while the gate is closed, the unsaved registry is emptied, and only then does the application continue (to the other user's session, to company selection or to the sign-in page). Nothing of the old shell renders for anyone else.
- **Other tabs (Web).** Tabs of one browser share the session cookie, and a 401 cannot tell an expiry from a sign-out elsewhere. Sign-out and sign-in are therefore announced on a `BroadcastChannel` named `hasm-session`; the message is the bare event name, nothing is stored. "signed-out": every other tab ends its session at once, locked or not. "signed-in": every other tab asks the server who is signed in and ends its session if it is someone else; the same user does NOT unlock another tab (the input comes back only through that tab's own sign-in). Residual: a browser without `BroadcastChannel` announces nothing; preserved input then lasts until the sign-in in that tab, where the user-id check still applies. **Windows desktop:** one window with its own in-memory bearer token; there is no other tab, so no event ever arrives, and sign-out happens in that window.
- **After restoring.** Nothing is re-submitted automatically; the user saves again. The form still carries the revision it was loaded with, so the server's optimistic concurrency decides: a newer revision is answered 412 and the screen's existing conflict handling applies. A privileged save on the new session may ask for a fresh second-factor proof through the existing step-up dialog.
- **Covered screens.** Every screen that reports unsaved sections: Approval Policies, Branches, Chart of Accounts, Commercial Documents, Company Details & Settings, Cost Centers, Fiscal Years, Inventory Groups, Items and Units, Journal, Parties, Party Roles, Pricing, Stock Documents, Stocktakes, Taxes, Warehouses. Because the whole shell stays mounted, every other state of the current screen survives a same-user sign-in as well; but a session that ends while NO section reports unsaved edits is handled as before, so input in a screen that does not report to the safeguard is not preserved.
- **Tabs.** The shared `Tabs` component is unchanged; switching tabs still never saves, discards or resets input.
- **Team readings on the strict side (for the owner, none blocks):** (1) "hide" is implemented as not rendered, not focusable and not reachable; the values still exist in the memory of the page (and its hidden DOM), as any in-memory option implies; (2) the dialog shows no user name; (3) no time limit is added (the owner gave none); an optional maximum lock duration is a possible hardening; (4) preservation applies only when something is reported unsaved; (5) the desktop app follows the same no-persistence rule as the Web application.

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
