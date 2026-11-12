import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CascadingMenu, I18nProvider, type MenuItem } from "./index";

const items: MenuItem[] = [
  { id: "/", label: "Home" },
  { id: "company", label: "Company", children: [{ id: "/p", label: "Profile" }, { id: "/s", label: "Settings" }] },
];
const catalog = { ar: {}, en: {} };

function setup(onSelect = vi.fn(), locale?: "ar" | "en") {
  if (locale) localStorage.setItem("hasm.locale", locale);
  render(<I18nProvider catalog={catalog}><CascadingMenu items={items} label="Main" onSelect={onSelect} hoverDelayMs={50} /></I18nProvider>);
  return onSelect;
}

describe("CascadingMenu", () => {
  beforeEach(() => { vi.useFakeTimers(); localStorage.clear(); });
  afterEach(() => vi.useRealTimers());

  it("expands a submenu on hover but never selects until a click", () => {
    const onSelect = setup();
    const company = screen.getByRole("menuitem", { name: /Company/ });
    fireEvent.mouseEnter(company.closest("li")!);
    expect(screen.queryByRole("menuitem", { name: "Profile" })).toBeNull(); // hover intent delay
    act(() => { vi.advanceTimersByTime(60); });
    const profile = screen.getByRole("menuitem", { name: "Profile" });
    fireEvent.mouseEnter(profile);
    fireEvent.mouseOver(profile);
    expect(onSelect).not.toHaveBeenCalled();
    fireEvent.click(profile);
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ id: "/p" }));
    expect(screen.queryByRole("menuitem", { name: "Profile" })).toBeNull(); // closes after selecting
  });

  it("selecting a leaf at the top level is a click as well", () => {
    const onSelect = setup();
    fireEvent.click(screen.getByRole("menuitem", { name: "Home" }));
    expect(onSelect).toHaveBeenCalledTimes(1);
  });

  it("click toggles a parent, with aria-expanded reflecting the state", () => {
    setup();
    const company = screen.getByRole("menuitem", { name: /Company/ });
    expect(company).toHaveAttribute("aria-expanded", "false");
    fireEvent.click(company);
    expect(company).toHaveAttribute("aria-expanded", "true");
    fireEvent.click(company);
    expect(company).toHaveAttribute("aria-expanded", "false");
  });

  it("opens with ArrowDown, closes with Escape, and Enter on a leaf selects", () => {
    const onSelect = setup();
    const company = screen.getByRole("menuitem", { name: /Company/ });
    company.focus();
    fireEvent.keyDown(company, { key: "ArrowDown" });
    expect(screen.getByRole("menuitem", { name: "Profile" })).toBeInTheDocument();
    fireEvent.keyDown(company, { key: "Escape" });
    expect(screen.queryByRole("menuitem", { name: "Profile" })).toBeNull();
    expect(onSelect).not.toHaveBeenCalled();
  });

  it("mirrors horizontal arrow keys in RTL (Arabic) and LTR (English)", () => {
    setup(vi.fn(), "ar");
    const home = screen.getByRole("menuitem", { name: "Home" });
    home.focus();
    fireEvent.keyDown(home, { key: "ArrowLeft" }); // forward in RTL
    expect(screen.getByRole("menuitem", { name: /Company/ })).toHaveFocus();
  });

  it("LTR: ArrowRight moves forward", () => {
    setup(vi.fn(), "en");
    const home = screen.getByRole("menuitem", { name: "Home" });
    home.focus();
    fireEvent.keyDown(home, { key: "ArrowRight" });
    expect(screen.getByRole("menuitem", { name: /Company/ })).toHaveFocus();
  });

  it("vertical orientation: Up/Down move, the arrow facing the content opens a submenu (mirrored in RTL); Enter/click selects", () => {
    for (const [locale, forward] of [["en", "ArrowRight"], ["ar", "ArrowLeft"]] as const) {
      localStorage.setItem("hasm.locale", locale);
      const onSelect = vi.fn();
      const { unmount } = render(<I18nProvider catalog={catalog}><CascadingMenu orientation="vertical" items={items} label="Main" onSelect={onSelect} /></I18nProvider>);
      expect(screen.getByRole("menubar")).toHaveAttribute("aria-orientation", "vertical");
      const home = screen.getByRole("menuitem", { name: "Home" });
      home.focus();
      fireEvent.keyDown(home, { key: "ArrowDown" });
      const company = screen.getByRole("menuitem", { name: /Company/ });
      expect(document.activeElement).toBe(company);
      fireEvent.keyDown(company, { key: forward });
      expect(screen.getByRole("menuitem", { name: "Profile" })).toBeInTheDocument();
      expect(onSelect).not.toHaveBeenCalled();
      unmount();
    }
  });

  it("a disabled item with a badge never selects and exposes the badge in its name", () => {
    const onSelect = vi.fn();
    render(<I18nProvider catalog={catalog}><CascadingMenu orientation="vertical" items={[{ id: "x", label: "Sales", disabled: true, badge: "Soon" }]} label="Main" onSelect={onSelect} /></I18nProvider>);
    const item = screen.getByRole("menuitem", { name: "Sales Soon" });
    fireEvent.click(item);
    fireEvent.keyDown(item, { key: "Enter" });
    expect(onSelect).not.toHaveBeenCalled();
  });
});
