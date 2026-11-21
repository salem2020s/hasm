/**
 * Parses a number typed by a user. Accepts Latin, Arabic-Indic and Persian digits and ONE decimal separator ("." "," or the Arabic decimal separator). Anything ambiguous or not a plain
 * decimal is NaN and is never sent to the server: thousands separators ("5,000", "٥٬٠٠٠"), exponents ("1e3"), hex ("0x10"), "Infinity", signs and spaces inside the number.
 */
export function parseDecimalInput(s: string): number {
  const v = s.trim().replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x0660)).replace(/[۰-۹]/g, (d) => String(d.charCodeAt(0) - 0x06f0)).replace(/٫/g, ".");
  if (!/^\d+([.,]\d+)?$/.test(v)) return Number.NaN;
  if (/^\d+,\d{3}$/.test(v)) return Number.NaN;               // "5,000" or "1234,567" could be a decimal or a grouped integer: refuse instead of guessing
  const n = Number(v.replace(",", "."));
  return Number.isFinite(n) ? n : Number.NaN;
}

/** Like parseDecimalInput, but accepts ONE leading minus sign (ASCII "-" or U+2212) for fields where a negative value is valid (for example a minimum margin). "-0" is zero; a bare "-" or "+5" is refused. */
export function parseSignedDecimalInput(s: string): number {
  const t = s.trim();
  if (t.startsWith("-") || t.startsWith("\u2212")) { const rest = t.slice(1); if (/^\s/.test(rest)) return Number.NaN; const n = parseDecimalInput(rest); return Number.isNaN(n) ? n : (n === 0 ? 0 : -n); }
  return parseDecimalInput(t);
}

/** A strictly positive amount with at most `places` decimals (default 10), built on parseDecimalInput; NaN when blank, zero, negative or too precise. */
export function parsePositiveDecimal(s: string, places = 10): number {
  const n = parseDecimalInput(s);
  return Number.isFinite(n) && n > 0 && Number(n.toFixed(places)) === n ? n : Number.NaN;
}

/** A whole number within [min, max] with ANY number of digits (Latin, Arabic-Indic or Persian, also mixed), for fields without a fixed width (for example months or counts); NaN when blank, fractional or out of range. */
export function parseIntegerInRange(s: string, min: number, max: number): number {
  const n = parseDecimalInput(s);
  return Number.isInteger(n) && n >= min && n <= max ? n : Number.NaN;
}

/** A whole number written with exactly `digits` digits (for example a four-digit year), within [min, max]; NaN otherwise. Leading zeros beyond the width are refused. */
export function parseWholeNumber(s: string, digits: number, min: number, max: number): number {
  const n = parseDecimalInput(s);
  return Number.isInteger(n) && s.trim().length === digits && n >= min && n <= max ? n : Number.NaN;
}
