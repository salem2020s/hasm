import { describe, expect, it } from "vitest";
import { parseDecimalInput as p } from "./numberInput";

describe("parseDecimalInput", () => {
  it("accepts plain decimals in Latin, Arabic-Indic and Persian digits with one separator", () => {
    expect([p("15"), p(" 15.5 "), p("1,5"), p("١٥"), p("١٥٫٥"), p("۱۲,۵"), p("0.25")]).toEqual([15, 15.5, 1.5, 15, 15.5, 12.5, 0.25]);
  });
  it("refuses everything ambiguous or not a plain decimal instead of guessing", () => {
    for (const bad of ["", "abc", "5,000", "٥,٠٠٠", "1e3", "0x10", "Infinity", "-1", "+1", "1 000", "1..2", "1,2,3", ".5", "5."]) expect(p(bad), bad).toBeNaN();
  });
});
