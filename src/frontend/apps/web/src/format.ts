/**
 * Display formatting driven by the company settings (date format, numeral system, business time zone).
 * Display only: stored values and audit data stay UTC / exact and are never altered. No monetary arithmetic happens here.
 */
export interface DisplayFormat {
  dateFormat: string;
  numeralSystem: string;
  /** IANA id, or null while setup is incomplete (then UTC is shown, never the browser's zone). */
  timeZone: string | null;
}

/** Defaults of the configuration model (docs/PHASE-3.1A-2-PROPOSAL.md); used when the settings are unknown or not viewable. */
export const DEFAULT_FORMAT: DisplayFormat = { dateFormat: "yyyy-MM-dd", numeralSystem: "latin", timeZone: null };

const ARABIC_INDIC = "٠١٢٣٤٥٦٧٨٩";

/** Applies the company's numeral preference to already formatted text (digits only; separators are left alone). */
export function applyNumerals(text: string, f: Pick<DisplayFormat, "numeralSystem">): string {
  return f.numeralSystem === "arabic-indic" ? text.replace(/\d/g, (c) => ARABIC_INDIC[Number(c)]) : text;
}

function zonedParts(d: Date, timeZone: string | null): Record<string, string> {
  const make = (tz: string) => new Intl.DateTimeFormat("en-US", {
    timeZone: tz, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
  }).formatToParts(d);
  let parts: Intl.DateTimeFormatPart[];
  try { parts = make(timeZone ?? "UTC"); } catch { parts = make("UTC"); } // an id this runtime does not know: fall back to UTC
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  return { year: get("year"), month: get("month"), day: get("day"), hour: get("hour") === "24" ? "00" : get("hour"), minute: get("minute") };
}

function datePart(p: Record<string, string>, dateFormat: string): string {
  return dateFormat === "dd/MM/yyyy" ? `${p.day}/${p.month}/${p.year}`
    : dateFormat === "MM/dd/yyyy" ? `${p.month}/${p.day}/${p.year}`
    : `${p.year}-${p.month}-${p.day}`;
}

/** Date and time (24h) in the company time zone using the company date format. */
export function formatDateTime(iso: string, f: DisplayFormat): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  const p = zonedParts(d, f.timeZone);
  return applyNumerals(`${datePart(p, f.dateFormat)} ${p.hour}:${p.minute}`, f);
}

/** Date only, in the company time zone and date format. */
export function formatDate(iso: string, f: DisplayFormat): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return applyNumerals(datePart(zonedParts(d, f.timeZone), f.dateFormat), f);
}

/** Grouped number with a fixed number of decimals (Latin grouping, then the company numeral preference). */
export function formatNumber(value: number, f: Pick<DisplayFormat, "numeralSystem">, decimals = 0): string {
  if (!Number.isFinite(value)) return "";
  const text = new Intl.NumberFormat("en-US", { minimumFractionDigits: decimals, maximumFractionDigits: decimals }).format(value);
  return applyNumerals(text, f);
}

/** "12,450.00 SYP": amount in major units with the currency's minor-unit precision, then the ISO code. Display only. */
export function formatMoney(amount: number, currencyCode: string, f: Pick<DisplayFormat, "numeralSystem">, minorUnits = 2): string {
  return `${formatNumber(amount, f, minorUnits)} ${currencyCode}`;
}
