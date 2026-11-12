import type { ReactNode } from "react";

/** Lightweight inline SVG icons (stroke = currentColor, decorative). No icon library is bundled. */
function Icon({ children }: { children: ReactNode }) {
  return (
    <svg className="icon" viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
      {children}
    </svg>
  );
}

export const HomeIcon = () => <Icon><path d="M3 11l9-8 9 8" /><path d="M5 10v10h5v-6h4v6h5V10" /></Icon>;
export const AccountingIcon = () => <Icon><rect x="5" y="3" width="14" height="18" rx="2" /><path d="M8 7h8M8 12h2M12 12h2M16 12h0M8 16h2M12 16h2M16 16h0" /></Icon>;
export const WarehouseIcon = () => <Icon><path d="M12 3l8 4.5v9L12 21l-8-4.5v-9z" /><path d="M4 7.5l8 4.5 8-4.5M12 12v9" /></Icon>;
export const SalesIcon = () => <Icon><path d="M3 4h2l2.4 11h10l2-8H6.2" /><circle cx="9" cy="19" r="1.2" /><circle cx="17" cy="19" r="1.2" /></Icon>;
export const PurchasesIcon = () => <Icon><path d="M5 8h14l-1 12H6z" /><path d="M9 8a3 3 0 016 0" /></Icon>;
export const ReportsIcon = () => <Icon><path d="M4 20V10M10 20V4M16 20v-7M21 20H3" /></Icon>;
export const CompanyIcon = () => <Icon><rect x="4" y="3" width="11" height="18" rx="1" /><path d="M15 9h5v12h-5M8 7h3M8 11h3M8 15h3" /></Icon>;
/** Building with a plus: the "add a company" action. */
export const AddCompanyIcon = () => <Icon><rect x="4" y="3" width="11" height="18" rx="1" /><path d="M8 7h3M8 11h3M8 15h3M18 4v6M15 7h6" /></Icon>;
export const SettingsIcon = () => <Icon><circle cx="12" cy="12" r="3" /><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1" /></Icon>;
/** Points toward the content area; mirrored in RTL by CSS. */
export const ChevronIcon = () => <Icon><path d="M9 6l6 6-6 6" /></Icon>;
