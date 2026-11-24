import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
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

  describe("keyboard focus never races with the user (no deferred focus)", () => {
    afterEach(() => vi.unstubAllGlobals());
    const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
    /** Animation frames that arrive late (a busy or throttled window): 100 ms instead of ~16 ms. */
    const slowFrames = () => {
      const raf = vi.fn((cb: FrameRequestCallback) => window.setTimeout(() => cb(performance.now()), 100));
      vi.stubGlobal("requestAnimationFrame", raf);
      vi.stubGlobal("cancelAnimationFrame", (id: number) => window.clearTimeout(id));
      return raf;
    };

    it("moves focus to the chosen tab synchronously with the key press, even when animation frames are slow", () => {
      slowFrames();
      setup("en");
      tab("General").focus();
      fireEvent.keyDown(tab("General"), { key: "ArrowRight" });
      expect(tab("Units")).toHaveFocus(); // no waiting: nothing is left pending
      expect(tab("Units")).toHaveAttribute("aria-selected", "true");
      fireEvent.keyDown(tab("Units"), { key: "End" });
      expect(tab("Tracking")).toHaveFocus();
      fireEvent.keyDown(tab("Tracking"), { key: "Home" });
      expect(tab("General")).toHaveFocus();
    });

    it("does not steal focus from a field the user enters right after an arrow key (slow frames): typing and Enter stay in the field", async () => {
      slowFrames();
      localStorage.setItem("hasm.locale", "en");
      const onSubmit = vi.fn((e: { preventDefault(): void }) => e.preventDefault());
      render(
        <I18nProvider catalog={catalog}>
          <form onSubmit={onSubmit}>
            <Tabs label="Item" tabs={[
              { id: "general", label: "General", content: <input aria-label="Name" /> },
              { id: "units", label: "Units", content: <input aria-label="Factor" /> },
            ]} />
            <button type="submit">Save</button>
          </form>
        </I18nProvider>,
      );
      tab("General").focus();
      fireEvent.keyDown(tab("General"), { key: "ArrowRight" });
      await userEvent.setup({ delay: null }).click(screen.getByLabelText("Factor")); // at once, well inside the 100 ms frame
      expect(screen.getByLabelText("Factor")).toHaveFocus();
      await userEvent.setup({ delay: 40 }).keyboard("12345{Enter}"); // ~240 ms of typing, across the 100 ms frame
      await sleep(150); // every frame requested by the key press has fired by now
      expect(screen.getByLabelText("Factor")).toHaveFocus();
      expect(screen.getByLabelText("Factor")).toHaveValue("12345");
      expect(onSubmit).toHaveBeenCalledTimes(1); // Enter reached the field, not a tab button
      expect(tab("Units")).toHaveAttribute("aria-selected", "true");
    });

    it("does not pull focus back to the tab list when the user tabs into the panel right after an arrow key (slow frames)", async () => {
      slowFrames();
      setup("en");
      tab("General").focus();
      await userEvent.keyboard("{ArrowRight}");
      await userEvent.tab();
      expect(screen.getByLabelText("Factor")).toHaveFocus();
      await sleep(150);
      expect(screen.getByLabelText("Factor")).toHaveFocus();
    });

    it("unmounting right after an arrow key leaves nothing pending: no error and no focus on a removed tab", async () => {
      const raf = slowFrames();
      const errors = vi.spyOn(console, "error").mockImplementation(() => {});
      try {
        localStorage.setItem("hasm.locale", "en");
        const view = render(<I18nProvider catalog={catalog}><button type="button">Outside</button><Form /></I18nProvider>);
        tab("General").focus();
        fireEvent.keyDown(tab("General"), { key: "ArrowRight" });
        view.rerender(<I18nProvider catalog={catalog}><button type="button">Outside</button></I18nProvider>); // Tabs unmounted, frame (if any) still pending
        const outside = screen.getByRole("button", { name: "Outside" });
        outside.focus();
        await sleep(150);
        expect(outside).toHaveFocus();
        expect(screen.queryByRole("tab")).toBeNull();
        expect(errors).not.toHaveBeenCalled();
        expect(raf).not.toHaveBeenCalled(); // Tabs schedules no frame at all, so there is nothing to cancel on unmount
      } finally { errors.mockRestore(); }
    });
  });
});
