import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import { directionOf, I18nProvider, LanguageSwitcher, useI18n } from "./index";

const catalog = { ar: { hello: "مرحبا" }, en: { hello: "Hello", onlyEn: "English only" } };

function Probe() {
  const { t } = useI18n();
  return <p>{t("hello")} / {t("onlyEn")}</p>;
}

describe("i18n", () => {
  beforeEach(() => localStorage.clear());

  it("maps Arabic to rtl and English to ltr", () => {
    expect(directionOf("ar")).toBe("rtl");
    expect(directionOf("en")).toBe("ltr");
  });

  it("applies lang/dir to the document and switches at runtime", () => {
    render(<I18nProvider catalog={catalog}><Probe /><LanguageSwitcher /></I18nProvider>);
    expect(document.documentElement.dir).toBe("rtl");
    expect(screen.getByText(/مرحبا/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button"));

    expect(document.documentElement.dir).toBe("ltr");
    expect(document.documentElement.lang).toBe("en");
    expect(screen.getByText("Hello / English only")).toBeInTheDocument();
  });

  it("falls back to English for missing translations", () => {
    render(<I18nProvider catalog={catalog}><Probe /></I18nProvider>);
    expect(screen.getByText("مرحبا / English only")).toBeInTheDocument();
  });
});
