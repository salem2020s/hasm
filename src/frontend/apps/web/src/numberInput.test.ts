import { describe, expect, it } from "vitest";
import { parseDecimalInput as p, parseIntegerInRange as pi, parseSignedDecimalInput as ps, parseWholeNumber as pw } from "./numberInput";

describe("parseDecimalInput", () => {
  it("accepts plain decimals in Latin, Arabic-Indic and Persian digits with one separator", () => {
    expect([p("15"), p(" 15.5 "), p("1,5"), p("١٥"), p("١٥٫٥"), p("۱۲,۵"), p("0.25")]).toEqual([15, 15.5, 1.5, 15, 15.5, 12.5, 0.25]);
  });
  it("refuses everything ambiguous or not a plain decimal instead of guessing", () => {
    for (const bad of ["", "abc", "5,000", "٥,٠٠٠", "1e3", "0x10", "Infinity", "-1", "+1", "1 000", "1..2", "1,2,3", ".5", "5."]) expect(p(bad), bad).toBeNaN();
  });
});

describe("parseSignedDecimalInput", () => {
  it("accepts one leading minus on top of the plain decimal rules and keeps refusing ambiguity", () => {
    expect([ps("-5"), ps("\u22125.5"), ps("-٢٫٥"), ps("10"), ps("-0"), ps("0")]).toEqual([-5, -5.5, -2.5, 10, 0, 0]);
    for (const bad of ["-", "--5", "+5", "-5,000", "- 5", "5-", "-.5", "-1e3", ""]) expect(ps(bad), bad).toBeNaN();
    expect(Object.is(ps("-0"), -0)).toBe(false);
  });
  it("leaves the unsigned parser strict", () => { expect(p("-1")).toBeNaN(); });
});

describe("whole-number fields share the digit rules (Arabic-Indic, Persian, mixed) and the range rules", () => {
  it("parseWholeNumber accepts a fixed-width year in every digit set, also mixed", () => {
    expect([pw("2027", 4, 1900, 2200), pw("\u0662\u0660\u0662\u0667", 4, 1900, 2200), pw("\u06f2\u06f0\u06f2\u06f7", 4, 1900, 2200), pw("\u06620\u0662\u06f7", 4, 1900, 2200)]).toEqual([2027, 2027, 2027, 2027]);
  });
  it("parseWholeNumber refuses a wrong width, a range violation, a fraction and non-digits", () => {
    for (const bad of ["", "27", "20277", "1899", "2201", "20.7", "202x", "\u0662\u0660\u0662x", "\u0662\u0660 \u0662\u0667"]) expect(pw(bad, 4, 1900, 2200), bad).toBeNaN();
  });
  it("parseIntegerInRange accepts any width within the range in every digit set, also mixed", () => {
    expect([pi("7", 1, 1200), pi("\u0668\u0664", 1, 1200), pi("\u06f1\u06f2\u06f0\u06f0", 1, 1200), pi("1\u0662", 1, 1200)]).toEqual([7, 84, 1200, 12]);
  });
  it("parseIntegerInRange refuses blank, zero below the minimum, too large, fractions, separators and non-digits", () => {
    for (const bad of ["", "0", "1201", "\u0661\u0662\u0660\u0661", "1.5", "\u0661\u066b\u0665", "1,000", "\u0661\u066c\u0660\u0660\u0660", "8x", "-5", "60.0", "6,0", "\u0666\u066b\u0660", "\u06f6\u066b\u06f0", " 6 0"]) expect(pi(bad, 1, 1200), bad).toBeNaN();
  });
});
