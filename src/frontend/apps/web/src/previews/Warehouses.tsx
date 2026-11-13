import { WarehouseIcon } from "@hasm/ui";
import { formatMoney, formatNumber } from "../format";
import { EmptyState, MetricCard, ModuleHero, ResponsiveTable } from "../visual";
import { Pager, PreviewBanner, PlusI, PvButton, ExportI, PrintI, SampleTag, SearchI, SwapI, useSample } from "./shared";

type Status = "ok" | "low" | "near";
const ITEMS: { sku: string; ar: string; en: string; unit: [string, string]; wh: [string, string]; qty: number; min: number; cost: number; status: Status }[] = [
  { sku: "ITM-0001", ar: "لابتوب أعمال", en: "Business laptop", unit: ["قطعة", "pcs"], wh: ["المستودع الرئيسي", "Main warehouse"], qty: 25, min: 10, cost: 5200, status: "ok" },
  { sku: "ITM-0002", ar: "شاشة 24 بوصة", en: "24-inch monitor", unit: ["قطعة", "pcs"], wh: ["المستودع الرئيسي", "Main warehouse"], qty: 48, min: 20, cost: 1150, status: "ok" },
  { sku: "ITM-0003", ar: "طابعة ليزر", en: "Laser printer", unit: ["قطعة", "pcs"], wh: ["مستودع الفرع", "Branch warehouse"], qty: 12, min: 15, cost: 2750, status: "low" },
  { sku: "ITM-0004", ar: "حبر طابعة (أسود)", en: "Printer ink (black)", unit: ["علبة", "box"], wh: ["المستودع الرئيسي", "Main warehouse"], qty: 120, min: 100, cost: 85, status: "near" },
];

/** DEVELOPMENT-ONLY design preview of the future Warehouses module. Not functional; sample content only. */
export default function WarehousesPreview() {
  const { l, format, locale } = useSample();
  const i = locale === "ar" ? 0 : 1;
  const statusText: Record<Status, string> = { ok: l("متوفر", "In stock"), low: l("منخفض", "Low"), near: l("قريب من الحد", "Near minimum") };
  return (
    <section aria-labelledby="page-title" data-testid="preview-warehouses">
      <PreviewBanner />
      <ModuleHero module="warehouse" icon={<WarehouseIcon />} title={l("المستودعات", "Warehouses")}
        description={l("متابعة الأرصدة وحركة الأصناف والتحويل بين المستودعات.", "Track stock levels, item movements and transfers between warehouses.")} tag={<SampleTag />} />

      <div className="pv-toolbar">
        <div className="pv-chips" role="group" aria-label={l("المستودع", "Warehouse")}>
          <button type="button" className="pv-chip is-active" aria-pressed="true">{l("كل المستودعات", "All warehouses")}</button>
          <button type="button" className="pv-chip" aria-pressed="false">{ITEMS[0].wh[i]}</button>
          <button type="button" className="pv-chip" aria-pressed="false">{ITEMS[2].wh[i]}</button>
        </div>
        <div className="pv-actions">
          <PvButton primary icon={<PlusI />}>{l("إضافة صنف", "Add item")}</PvButton>
          <PvButton icon={<SwapI />}>{l("تحويل بين المستودعات", "Transfer")}</PvButton>
          <PvButton icon={<ExportI />}>{l("تصدير", "Export")}</PvButton>
          <PvButton icon={<PrintI />}>{l("طباعة", "Print")}</PvButton>
        </div>
      </div>

      <div className="metric-row">
        <MetricCard tone="teal" icon={<WarehouseIcon />} label={l("إجمالي الأصناف", "Total items")} value={<span className="ltr-value">{formatNumber(2486, format)}</span>} hint={<SampleTag />} />
        <MetricCard tone="gold" icon={<WarehouseIcon />} label={l("أصناف منخفضة المخزون", "Low-stock items")} value={<span className="ltr-value">{formatNumber(17, format)}</span>} hint={<SampleTag />} />
        <MetricCard tone="navy" icon={<WarehouseIcon />} label={l("حركات وارد", "Inbound movements")} value={<span className="ltr-value">{formatNumber(538, format)}</span>} hint={<SampleTag />} />
      </div>

      <div className="card">
        <div className="toolbar">
          <div className="pv-search">
            <SearchI />
            <input type="search" readOnly aria-label={l("بحث عن صنف (عرض فقط)", "Search items (display only)")} placeholder={l("ابحث بالاسم أو الرمز أو الباركود…", "Search by name, code or barcode…")} />
          </div>
          <SampleTag />
        </div>
        <ResponsiveTable label={l("أرصدة الأصناف (عينة تصميم)", "Item balances (sample design)")}>
          <thead><tr>
            <th scope="col">{l("رمز الصنف", "SKU")}</th><th scope="col">{l("اسم الصنف", "Item name")}</th><th scope="col">{l("الوحدة", "Unit")}</th>
            <th scope="col">{l("المستودع", "Warehouse")}</th><th scope="col" className="num">{l("الرصيد", "Balance")}</th>
            <th scope="col" className="num">{l("الحد الأدنى", "Minimum")}</th><th scope="col" className="num">{l("تكلفة الوحدة", "Unit cost")}</th><th scope="col">{l("الحالة", "Status")}</th>
            <th scope="col"><span className="visually-hidden">{l("إجراءات", "Actions")}</span></th>
          </tr></thead>
          <tbody>
            {ITEMS.map((r) => (
              <tr key={r.sku}>
                <td><bdi className="ltr-value">{r.sku}</bdi></td><td><strong>{l(r.ar, r.en)}</strong></td><td>{r.unit[i]}</td><td>{r.wh[i]}</td>
                <td className="num"><span className="ltr-value">{formatNumber(r.qty, format)}</span></td>
                <td className="num"><span className="ltr-value">{formatNumber(r.min, format)}</span></td>
                <td className="num"><span className="ltr-value">{formatMoney(r.cost, "SYP", format)}</span></td>
                <td><span className={`chip st-${r.status}`}>{statusText[r.status]}</span></td>
                <td><PvButton>…</PvButton></td>
              </tr>
            ))}
          </tbody>
        </ResponsiveTable>
        <Pager total={l("عرض 1–4 من 2,486 صنف (عينة)", "Showing 1–4 of 2,486 items (sample)")} />
      </div>

      <div className="dash-grid">
        <div className="dash-card">
          <h2 className="dash-card-title">{l("حركة المخزون", "Stock movement")}</h2>
          <EmptyState tag={l("تصميم فقط", "Design only")} title={l("مكان مخطط الحركة", "Movement chart placeholder")}
            description={l("سيظهر مخطط حقيقي عند تنفيذ الوحدة. لا يوجد مخطط في هذه المعاينة.", "A real chart will appear when the module is implemented. This preview contains no chart.")} />
        </div>
      </div>
    </section>
  );
}
