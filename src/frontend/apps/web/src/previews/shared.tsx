import { useI18n } from "@hasm/ui";
import type { ReactNode } from "react";
import { Banner } from "../components";
import { useDisplayFormat } from "../useDisplayFormat";
import "./preview.css";

/**
 * DEVELOPMENT-ONLY design previews. Nothing here is a business module: no API calls (except reading the display format),
 * no state, no calculations. All values are fixed sample design content and are always labelled as such.
 * This folder is imported only behind `import.meta.env.DEV`, so it is absent from production builds.
 */
export function useSample() {
  const { locale } = useI18n();
  const { format } = useDisplayFormat();
  const l = (ar: string, en: string) => (locale === "ar" ? ar : en);
  return { l, format, locale };
}

export function SampleTag() {
  const { l } = useSample();
  return <span className="chip chip-sample">{l("بيانات تصميم توضيحية", "SAMPLE DESIGN CONTENT")}</span>;
}

export function PreviewBanner() {
  const { l } = useSample();
  return (
    <Banner kind="warning" testId="design-preview-banner">
      <strong>{l("معاينة تصميم — للتطوير فقط.", "DESIGN PREVIEW — development only.")}</strong>{" "}
      {l("هذه ليست وحدة منفّذة، والقيم المعروضة عينات تصميمية وليست بيانات حقيقية.", "This is not an implemented module; every value shown is sample design content, not real data.")}
    </Banner>
  );
}

/** Looks like a real button but does nothing (the previews have no functionality). */
export function PvButton({ children, primary, icon }: { children: ReactNode; primary?: boolean; icon?: ReactNode }) {
  return (
    <button type="button" className={`pv-btn${primary ? " primary" : ""}`} aria-disabled="true" onClick={(e) => e.preventDefault()}>
      {icon}{children}
    </button>
  );
}

const I = ({ children }: { children: ReactNode }) => (
  <svg className="icon" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">{children}</svg>
);
export const SearchI = () => <I><circle cx="11" cy="11" r="6" /><path d="M20 20l-4-4" /></I>;
export const PlusI = () => <I><path d="M12 5v14M5 12h14" /></I>;
export const ExportI = () => <I><path d="M12 4v11M7 10l5 5 5-5M5 20h14" /></I>;
export const PrintI = () => <I><path d="M7 9V3h10v6M7 17H5a1 1 0 01-1-1v-6h16v6a1 1 0 01-1 1h-2M7 14h10v7H7z" /></I>;
export const FilterI = () => <I><path d="M4 5h16l-6 8v6l-4-2v-4z" /></I>;
export const SwapI = () => <I><path d="M4 8h14l-4-4M20 16H6l4 4" /></I>;

export function Pager({ total }: { total: string }) {
  const { l } = useSample();
  return (
    <div className="pv-pager">
      <span className="hint">{total}</span>
      <div className="pv-pages" aria-label={l("ترقيم الصفحات (للعرض فقط)", "Pagination (display only)")}>
        {["1", "2", "3", "…", "141"].map((p, i) => <span key={i} className={`pv-page${i === 0 ? " is-current" : ""}`} aria-current={i === 0 ? "page" : undefined}>{p}</span>)}
      </div>
    </div>
  );
}
