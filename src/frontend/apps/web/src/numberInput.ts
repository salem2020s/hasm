/**
 * Parses a number typed by a user. Accepts Latin, Arabic-Indic and Persian digits and ONE decimal separator ("." "," or the Arabic decimal separator). Anything ambiguous or not a plain
 * decimal is NaN and is never sent to the server: thousands separators ("5,000", "٥٬٠٠٠"), exponents ("1e3"), hex ("0x10"), "Infinity", signs and spaces inside the number.
 */
export function parseDecimalInput(s: string): number {
  const v = s.trim().replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x0660)).replace(/[۰-۹]/g, (d) => String(d.charCodeAt(0) - 0x06f0)).replace(/٫/g, ".");
  if (!/^\d+([.,]\d+)?$/.test(v)) return Number.NaN;
  if (/^\d{1,3},\d{3}$/.test(v)) return Number.NaN;            // "5,000" could be five or five thousand: refuse instead of guessing
  const n = Number(v.replace(",", "."));
  return Number.isFinite(n) ? n : Number.NaN;
}
