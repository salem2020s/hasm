import { describe, expect, it } from "vitest";
import { DEFAULT_FORMAT, formatDate, formatDateTime, formatMoney, formatNumber } from "./format";

const iso = "2026-03-09T21:05:00Z";

describe("formatDateTime", () => {
  it("honours the three supported date formats", () => {
    expect(formatDateTime(iso, { ...DEFAULT_FORMAT, dateFormat: "yyyy-MM-dd" })).toBe("2026-03-09 21:05");
    expect(formatDateTime(iso, { ...DEFAULT_FORMAT, dateFormat: "dd/MM/yyyy" })).toBe("09/03/2026 21:05");
    expect(formatDateTime(iso, { ...DEFAULT_FORMAT, dateFormat: "MM/dd/yyyy" })).toBe("03/09/2026 21:05");
  });

  it("shows UTC when no business time zone is set, never the browser zone", () => {
    expect(formatDateTime(iso, DEFAULT_FORMAT)).toBe("2026-03-09 21:05");
  });

  it("converts to the company time zone (date may roll over)", () => {
    expect(formatDateTime(iso, { ...DEFAULT_FORMAT, timeZone: "Asia/Damascus" })).toBe("2026-03-10 00:05");
    expect(formatDateTime("2026-01-01T00:00:00Z", DEFAULT_FORMAT)).toBe("2026-01-01 00:00"); // midnight is 00, never 24
  });

  it("applies the numeral system and falls back safely on bad input", () => {
    expect(formatDateTime(iso, { ...DEFAULT_FORMAT, numeralSystem: "arabic-indic" })).toBe("٢٠٢٦-٠٣-٠٩ ٢١:٠٥");
    expect(formatDateTime(iso, { ...DEFAULT_FORMAT, timeZone: "Not/AZone" })).toBe("2026-03-09 21:05");
    expect(formatDateTime("garbage", DEFAULT_FORMAT)).toBe("garbage");
  });
});

describe("formatDate / formatNumber / formatMoney", () => {
  it("formats the date part only, in the company zone", () => {
    expect(formatDate("2026-03-09T21:05:00Z", { ...DEFAULT_FORMAT, dateFormat: "dd/MM/yyyy", timeZone: "Asia/Damascus" })).toBe("10/03/2026");
  });
  it("groups numbers and applies numerals without touching separators", () => {
    expect(formatNumber(47350000, DEFAULT_FORMAT)).toBe("47,350,000");
    expect(formatNumber(1234.5, DEFAULT_FORMAT, 2)).toBe("1,234.50");
    expect(formatNumber(1234.5, { numeralSystem: "arabic-indic" }, 2)).toBe("١,٢٣٤.٥٠");
    expect(formatNumber(NaN, DEFAULT_FORMAT)).toBe("");
  });
  it("shows money with minor-unit precision and the ISO code", () => {
    expect(formatMoney(12450, "SYP", DEFAULT_FORMAT)).toBe("12,450.00 SYP");
    expect(formatMoney(12450, "JPY", DEFAULT_FORMAT, 0)).toBe("12,450 JPY");
  });
});
