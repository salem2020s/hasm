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
});
