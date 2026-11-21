import { describe, expect, it } from "vitest";
import { parseDecimalInput as p, parseSignedDecimalInput as ps } from "./numberInput";

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
