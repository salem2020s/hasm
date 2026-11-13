import { SalesIcon } from "@hasm/ui";
import { formatDate, formatMoney } from "../format";
import { ModuleHero, ResponsiveTable } from "../visual";
import { ExportI, FilterI, Pager, PlusI, PreviewBanner, PrintI, PvButton, SampleTag, SearchI, useSample } from "./shared";

type Status = "paid" | "pending" | "cancelled";
const INVOICES: { no: string; date: string; ar: string; en: string; total: number; status: Status }[] = [
  { no: "INV-000128", date: "2026-11-11T09:00:00Z", ar: "شركة الأفق للتقنية", en: "Horizon Technology Co.", total: 12450000, status: "paid" },
  { no: "INV-000127", date: "2026-11-10T09:00:00Z", ar: "مؤسسة الرمال", en: "Al Rimal Est.", total: 8275000, status: "paid" },
  { no: "INV-000126", date: "2026-11-09T09:00:00Z", ar: "شركة النور التجارية", en: "Al Nour Trading", total: 15900000, status: "pending" },
  { no: "INV-000125", date: "2026-11-08T09:00:00Z", ar: "مؤسسة الياسمين", en: "Al Yasmin Est.", total: 4320000, status: "cancelled" },
];

/** DEVELOPMENT-ONLY design preview of the future Sales Invoices module. Not functional; no posting, no accounting entries, sample content only. */
export default function SalesInvoicesPreview() {
  const { l, format, locale } = useSample();
  const text: Record<Status, string> = { paid: l("مدفوعة", "Paid"), pending: l("معلّقة", "Pending"), cancelled: l("ملغاة", "Cancelled") };
  const cls: Record<Status, string> = { paid: "st-ok", pending: "st-near", cancelled: "st-bad" };
  return (
    <section aria-labelledby="page-title" data-testid="preview-sales">
      <PreviewBanner />
      <ModuleHero module="sales" icon={<SalesIcon />} title={l("فواتير المبيعات", "Sales invoices")}
        description={l("إدارة فواتير المبيعات والمرتجعات.", "Manage sales invoices and returns.")} tag={<SampleTag />} />

      <div className="pv-tabs" role="group" aria-label={l("تصفية الحالة (عرض فقط)", "Status filter (display only)")}>
        {[[l("الكل", "All"), true], [text.paid, false], [text.pending, false], [text.cancelled, false]].map(([label, on]) => (
          <button key={String(label)} type="button" className={`pv-chip${on ? " is-active" : ""}`} aria-pressed={on as boolean}>{label}</button>
        ))}
      </div>

      <div className="card">
        <div className="pv-toolbar">
          <div className="pv-search">
            <SearchI />
            <input type="search" readOnly aria-label={l("بحث في الفواتير (عرض فقط)", "Search invoices (display only)")} placeholder={l("رقم الفاتورة أو اسم العميل…", "Invoice number or customer…")} />
          </div>
          <div className="pv-actions">
            <PvButton icon={<FilterI />}>{l("تصفية", "Filter")}</PvButton>
            <PvButton icon={<ExportI />}>{l("تصدير", "Export")}</PvButton>
            <PvButton icon={<PrintI />}>{l("طباعة", "Print")}</PvButton>
            <PvButton primary icon={<PlusI />}>{l("فاتورة جديدة", "New invoice")}</PvButton>
          </div>
        </div>
        <ResponsiveTable label={l("قائمة الفواتير (عينة تصميم)", "Invoice list (sample design)")}>
          <thead><tr>
            <th scope="col">{l("رقم الفاتورة", "Invoice no.")}</th><th scope="col">{l("التاريخ", "Date")}</th><th scope="col">{l("العميل", "Customer")}</th>
            <th scope="col" className="num">{l("الإجمالي", "Total")}</th><th scope="col">{l("الحالة", "Status")}</th>
          </tr></thead>
          <tbody>
            {INVOICES.map((r) => (
              <tr key={r.no}>
                <td><bdi className="ltr-value">{r.no}</bdi></td>
                <td><span className="ltr-value">{formatDate(r.date, format)}</span></td>
                <td>{locale === "ar" ? r.ar : r.en}</td>
                <td className="num"><span className="ltr-value">{formatMoney(r.total / 1000, "SYP", format)}</span></td>
                <td><span className={`chip ${cls[r.status]}`}>{text[r.status]}</span></td>
              </tr>
            ))}
          </tbody>
        </ResponsiveTable>
        <Pager total={l("عرض 1–4 من 128 فاتورة (عينة)", "Showing 1–4 of 128 invoices (sample)")} />
      </div>
    </section>
  );
}
