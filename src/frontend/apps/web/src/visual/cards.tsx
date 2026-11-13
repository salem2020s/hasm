import type { ReactNode } from "react";

/** Titled dashboard surface (use inside `.dash-grid`). */
export function DashboardCard({ title, icon, children, className = "" }: { title: string; icon?: ReactNode; children: ReactNode; className?: string }) {
  const id = `dc-${title.replace(/\s+/g, "-")}`;
  return (
    <section className={`dash-card ${className}`.trim()} aria-labelledby={id}>
      <h2 id={id} className="dash-card-title">{icon && <span className="dash-card-icon" aria-hidden="true">{icon}</span>}{title}</h2>
      {children}
    </section>
  );
}

export type Tone = "teal" | "gold" | "navy" | "neutral";

/** A single labelled value. The caller supplies the already formatted value (so numerals, codes and dates follow company settings). */
export function MetricCard({ label, value, hint, icon, tone = "teal", testId }: { label: string; value: ReactNode; hint?: ReactNode; icon?: ReactNode; tone?: Tone; testId?: string }) {
  return (
    <div className={`metric-card tone-${tone}`} data-testid={testId}>
      {icon && <span className="metric-icon" aria-hidden="true">{icon}</span>}
      <div className="metric-body">
        <span className="metric-label">{label}</span>
        <span className="metric-value">{value}</span>
        {hint && <span className="metric-hint">{hint}</span>}
      </div>
    </div>
  );
}

/** A large, keyboard-accessible shortcut to an existing screen. */
export function QuickActionCard({ icon, title, description, onClick }: { icon: ReactNode; title: string; description?: string; onClick: () => void }) {
  return (
    <button type="button" className="quick-card" onClick={onClick}>
      <span className="quick-icon" aria-hidden="true">{icon}</span>
      <span className="quick-text"><span className="quick-title">{title}</span>{description && <span className="quick-desc">{description}</span>}</span>
    </button>
  );
}

/** Clearly identified "nothing here (yet)" state. Never shows data. */
export function EmptyState({ title, description, icon, tag }: { title: string; description?: string; icon?: ReactNode; tag?: string }) {
  return (
    <div className="empty-state">
      {icon && <span className="empty-icon" aria-hidden="true">{icon}</span>}
      {tag && <span className="chip chip-off">{tag}</span>}
      <strong>{title}</strong>
      {description && <p className="hint">{description}</p>}
    </div>
  );
}

/** Horizontally scrollable, keyboard-focusable table region: wide tables never overflow the page in narrow windows. */
export function ResponsiveTable({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="table-wrap" role="region" aria-label={label} tabIndex={0}>
      <table className="data-table">
        <caption className="visually-hidden">{label}</caption>
        {children}
      </table>
    </div>
  );
}
