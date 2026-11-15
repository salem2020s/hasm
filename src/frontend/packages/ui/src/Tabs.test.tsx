import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { I18nProvider, Tabs, type TabItem } from "./index";

const catalog = {
  ar: { "tabs.unsaved": "تغييرات غير محفوظة", "tabs.has_errors": "يحتوي على أخطاء" },
  en: { "tabs.unsaved": "unsaved changes", "tabs.has_errors": "contains errors" },
};

function Form({ extra }: { extra?: Partial<Record<string, Partial<TabItem>>> }) {
  const [name, setName] = useState("");
  return (
    <Tabs label="Item" tabs={[
      { id: "general", label: "General", content: <input aria-label="Name" value={name} onChange={(e) => setName(e.target.value)} />, ...extra?.general },
      { id: "units", label: "Units", content: <input aria-label="Factor" defaultValue="" />, ...extra?.units },
      { id: "barcodes", label: "Barcodes", content: <input aria-label="Barcode" />, ...extra?.barcodes },
      { id: "tracking", label: "Tracking", content: <input aria-label="Flag" />, ...extra?.tracking },
    ]} />
  );
}
const setup = (locale: "ar" | "en", extra?: Parameters<typeof Form>[0]["extra"]) => {
  localStorage.setItem("hasm.locale", locale);
  return render(<I18nProvider catalog={catalog}><Form extra={extra} /></I18nProvider>);
};
const tab = (n: string) => screen.getByRole("tab", { name: new RegExp(n) });

describe("Tabs", () => {
  beforeEach(() => localStorage.clear());

  it("uses accessible tab semantics and switches the visible panel on click", async () => {
    setup("en");
    expect(screen.getByRole("tablist", { name: "Item" })).toBeInTheDocument();
    expect(tab("General")).toHaveAttribute("aria-selected", "true");
    expect(tab("General")).toHaveAttribute("aria-controls", screen.getByRole("tabpanel", { name: /General/ }).id);
    await userEvent.click(tab("Units"));
    expect(tab("Units")).toHaveAttribute("aria-selected", "true");
    expect(tab("General")).toHaveAttribute("aria-selected", "false");
    expect(screen.getByRole("tabpanel", { name: /Units/ })).toBeVisible();
    expect(screen.queryByRole("tabpanel", { name: /General/ })).toBeNull(); // hidden panels are not exposed
  });

  it("keeps unsaved input in every panel when switching tabs (panels stay mounted)", async () => {
    setup("en");
    await userEvent.type(screen.getByLabelText("Name"), "Widget");
    await userEvent.click(tab("Units"));
    await userEvent.type(screen.getByLabelText("Factor"), "12");
    await userEvent.click(tab("Barcodes"));
    await userEvent.click(tab("General"));
    expect(screen.getByLabelText("Name")).toHaveValue("Widget");
    await userEvent.click(tab("Units"));
    expect(screen.getByLabelText("Factor")).toHaveValue("12");
  });

  it("makes fields of inactive panels unreachable by keyboard (inert)", async () => {
    setup("en");
    const hidden = document.querySelector<HTMLElement>('[role="tabpanel"][hidden]')!;
    for (let i = 0; i < 12; i++) { await userEvent.tab(); expect(hidden.contains(document.activeElement)).toBe(false); }
  });

  it("shows error and unsaved indicators with text for assistive technology, on the right tabs only", () => {
    setup("en", { units: { invalid: true }, barcodes: { dirty: true } });
    expect(tab("Units")).toHaveTextContent("contains errors");
    expect(tab("Units").classList.contains("has-error")).toBe(true);
    expect(tab("Barcodes")).toHaveTextContent("unsaved changes");
    expect(tab("General")).not.toHaveTextContent("contains errors");
    expect(tab("General")).not.toHaveTextContent("unsaved changes");
  });

  it("indicators are translated with the language", () => {
    setup("ar", { units: { invalid: true } });
    expect(tab("Units")).toHaveTextContent("يحتوي على أخطاء");
  });

  it("LTR keyboard: ArrowRight moves forward, ArrowLeft back, Home/End jump, with roving tabindex and wrap-around", async () => {
    setup("en");
    tab("General").focus();
    await userEvent.keyboard("{ArrowRight}");
    await waitFor(() => expect(tab("Units")).toHaveFocus());
    expect(tab("Units")).toHaveAttribute("aria-selected", "true");
    expect(tab("Units")).toHaveAttribute("tabindex", "0");
    expect(tab("General")).toHaveAttribute("tabindex", "-1");
    await userEvent.keyboard("{End}");
    await waitFor(() => expect(tab("Tracking")).toHaveFocus());
    await userEvent.keyboard("{ArrowRight}");
    await waitFor(() => expect(tab("General")).toHaveFocus());
    await userEvent.keyboard("{ArrowLeft}");
    await waitFor(() => expect(tab("Tracking")).toHaveFocus());
    await userEvent.keyboard("{Home}");
    await waitFor(() => expect(tab("General")).toHaveFocus());
  });

  it("RTL keyboard is mirrored: ArrowLeft moves forward, ArrowRight back", async () => {
    setup("ar");
    expect(document.documentElement.dir).toBe("rtl");
    tab("General").focus();
    await userEvent.keyboard("{ArrowLeft}");
    await waitFor(() => expect(tab("Units")).toHaveFocus());
    await userEvent.keyboard("{ArrowRight}");
    await waitFor(() => expect(tab("General")).toHaveFocus());
  });

  it("skips disabled tabs and never activates them", async () => {
    setup("en", { units: { disabled: true } });
    expect(tab("Units")).toBeDisabled();
    tab("General").focus();
    await userEvent.keyboard("{ArrowRight}");
    await waitFor(() => expect(tab("Barcodes")).toHaveFocus());
    await userEvent.click(tab("Units"));
    expect(tab("Units")).toHaveAttribute("aria-selected", "false");
  });

  it("supports controlled use: the owner decides the active tab", async () => {
    const onChange = vi.fn();
    render(<I18nProvider catalog={catalog}><Tabs label="X" value="b" onChange={onChange} tabs={[{ id: "a", label: "A", content: "a" }, { id: "b", label: "B", content: "b" }]} /></I18nProvider>);
    expect(tab("B")).toHaveAttribute("aria-selected", "true");
    await userEvent.click(tab("A"));
    expect(onChange).toHaveBeenCalledWith("a");
    expect(tab("B")).toHaveAttribute("aria-selected", "true"); // not changed until the owner changes `value`
  });

  it("behaves identically inside the Tauri desktop shell (no platform-specific code path)", async () => {
    (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__ = {};
    try {
      setup("en");
      await userEvent.type(screen.getByLabelText("Name"), "W");
      fireEvent.click(tab("Units"));
      fireEvent.click(tab("General"));
      expect(screen.getByLabelText("Name")).toHaveValue("W");
    } finally { delete (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__; }
  });
});
