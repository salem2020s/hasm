import { AccountingIcon, PurchasesIcon, ReportsIcon, SalesIcon, WarehouseIcon } from "@hasm/ui";
import type { ReactElement } from "react";
import { navigate } from "../router";
import { EmptyState, MetricCard, ModuleHero, QuickActionCard, type IllustrationKind } from "../visual";
import { PreviewBanner, SampleTag, useSample } from "./shared";

/** DEVELOPMENT-ONLY gallery of the module headers (Accounting, Warehouses, Sales, Purchases, Reports) and the shared dashboard components. */
export default function Gallery() {
  const { l } = useSample();
  const heroes: { module: IllustrationKind; icon: ReactElement; ar: string; en: string; dar: string; den: string }[] = [
    { module: "accounting", icon: <AccountingIcon />, ar: "المحاسبة", en: "Accounting", dar: "الحسابات والقيود والتقارير المالية.", den: "Accounts, journal entries and financial reporting." },
    { module: "warehouse", icon: <WarehouseIcon />, ar: "المستودعات", en: "Warehouses", dar: "الأصناف والأرصدة وحركات المخزون.", den: "Items, balances and stock movements." },
    { module: "sales", icon: <SalesIcon />, ar: "المبيعات", en: "Sales", dar: "الفواتير والعملاء وعروض الأسعار.", den: "Invoices, customers and quotations." },
    { module: "purchases", icon: <PurchasesIcon />, ar: "المشتريات", en: "Purchases", dar: "أوامر وفواتير الشراء والموردون.", den: "Purchase orders, bills and suppliers." },
    { module: "reports", icon: <ReportsIcon />, ar: "التقارير", en: "Reports", dar: "تقارير مالية وتشغيلية.", den: "Financial and operational reports." },
  ];
  return (
    <section aria-labelledby="page-title" data-testid="preview-gallery">
      <PreviewBanner />
      <h1 id="page-title">{l("معرض التصميم (للتطوير فقط)", "Design gallery (development only)")}</h1>
      <p className="page-description">{l("رؤوس الوحدات والمكوّنات المشتركة. كل المحتوى عيّنات تصميمية.", "Module headers and shared components. All content is sample design content.")}</p>

      <h2 className="gallery-h">{l("رؤوس الوحدات", "Module headers")}</h2>
      {heroes.map((h) => <div key={h.module} className="gallery-item"><ModuleHeroStatic {...h} /></div>)}

      <h2 className="gallery-h">{l("مكوّنات مشتركة", "Shared components")}</h2>
      <div className="metric-row">
        <MetricCard tone="teal" icon={<AccountingIcon />} label={l("بطاقة مؤشر", "Metric card")} value="—" hint={<SampleTag />} />
        <MetricCard tone="gold" icon={<WarehouseIcon />} label={l("تنبيه", "Attention")} value="—" hint={<SampleTag />} />
      </div>
      <div className="quick-grid">
        <QuickActionCard icon={<SalesIcon />} title={l("وصول سريع", "Quick action")} description={l("وصف قصير", "Short description")} onClick={() => navigate("/dev/preview/sales-invoices")} />
      </div>
      <EmptyState tag={l("حالة فارغة", "Empty state")} title={l("لا توجد بيانات", "No data")} description={l("تُستخدم حين لا توجد بيانات حقيقية بعد.", "Used when no real data exists yet.")} />
    </section>
  );
}

function ModuleHeroStatic({ module, icon, ar, en, dar, den }: { module: IllustrationKind; icon: ReactElement; ar: string; en: string; dar: string; den: string }) {
  const { l } = useSample();
  // Only the gallery heading above is the page title; these are sub-headings.
  return <div className="gallery-hero"><ModuleHero as="h3" module={module} icon={icon} title={l(ar, en)} description={l(dar, den)} tag={<SampleTag />} /></div>;
}
