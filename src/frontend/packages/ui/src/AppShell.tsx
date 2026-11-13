import type { ReactNode } from "react";

interface Props {
  /** Brand block at the top of the sidebar. */
  brand: ReactNode;
  /** Sidebar navigation (a vertical CascadingMenu). */
  nav: ReactNode;
  /** Compact header content (company, user, language, sign out). */
  header: ReactNode;
  children: ReactNode;
}

/**
 * Direction-aware application shell: a two-column grid whose first column is the sidebar. Because the grid follows the document
 * direction, the sidebar sits at the inline-start edge: RIGHT in Arabic (RTL), LEFT in English (LTR). No per-language markup or
 * absolute positioning, so switching language mirrors the whole shell instantly while React state (route, company, session) is untouched.
 */
export function AppShell({ brand, nav, header, children }: Props) {
  return (
    <div className="app-shell">
      <aside className="app-sidebar" data-testid="sidebar">
        <div className="sidebar-brand">{brand}</div>
        {nav}
      </aside>
      <div className="app-main">
        <header className="app-topbar">{header}</header>
        {children}
      </div>
    </div>
  );
}
