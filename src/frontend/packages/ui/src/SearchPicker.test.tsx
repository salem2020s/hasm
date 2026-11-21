import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { I18nProvider, SearchPicker, type PickerPage } from "./index";

const catalog = {
  ar: { "picker.search": "بحث", "picker.none": "— بلا —", "picker.showing": "يُعرض {shown} من {total}.", "picker.refine": "ضيّق البحث لإظهار البقية.", "picker.loading": "جارٍ التحميل…", "picker.error": "تعذّر تحميل القائمة.", "picker.retry": "إعادة المحاولة", "picker.no_results": "لا نتائج.", "picker.unknown_selection": "(سجل محدد غير معروض)" },
  en: { "picker.search": "Search", "picker.none": "— None —", "picker.showing": "Showing {shown} of {total}.", "picker.refine": "Refine the search to see the rest.", "picker.loading": "Loading…", "picker.error": "The list could not be loaded.", "picker.retry": "Retry", "picker.no_results": "No results.", "picker.unknown_selection": "(selected record not shown)" },
};
interface Row { id: string; name: string }
const rows = (n: number, prefix = "Item"): Row[] => Array.from({ length: n }, (_, i) => ({ id: `${prefix}${i}`, name: `${prefix} ${i}` }));
type Load = (s: string) => Promise<PickerPage<Row>>;

function Hosted({ load, initial, onPick, reloadKey, allowNone, disabled, label = "Item", selectedLabel }: { load: Load; initial: string; onPick?: (id: string, r: Row | null) => void; reloadKey?: string; allowNone?: boolean; disabled?: boolean; label?: string; selectedLabel?: string | null }) {
  const [v, setV] = useState(initial);
  return <SearchPicker label={label} reloadKey={reloadKey} allowNone={allowNone} disabled={disabled} value={v} onChange={(id, r) => { setV(id); onPick?.(id, r); }} load={load} getId={(r) => r.id} getLabel={(r) => r.name} selectedLabel={selectedLabel === null ? undefined : selectedLabel ?? (initial ? "Current record" : undefined)} debounceMs={10} />;
}
function Host(props: { load: Load; initial?: string; onPick?: (id: string, r: Row | null) => void; reloadKey?: string; allowNone?: boolean; disabled?: boolean; label?: string; selectedLabel?: string | null }) {
  return <I18nProvider catalog={catalog}><Hosted {...props} initial={props.initial ?? ""} /></I18nProvider>;
}
const listbox = (name = "Item") => screen.getByRole("combobox", { name }) as HTMLSelectElement;
const optionTexts = () => Array.from(listbox().options).map((o) => o.textContent);

describe("SearchPicker", () => {
  beforeEach(() => localStorage.setItem("hasm.locale", "en"));

  it("loads one bounded page, says when more matches exist, and sends the trimmed search text", async () => {
    const load = vi.fn().mockImplementation((s: string) => Promise.resolve({ items: s ? rows(2, "Hit") : rows(3), total: s ? 2 : 900 }));
    render(<Host load={load} />);
    expect(await screen.findByText(/Showing 3 of 900\./)).toBeInTheDocument();
    expect(screen.getByText(/Refine the search/)).toBeInTheDocument();
    await userEvent.type(screen.getByRole("searchbox", { name: "Item — Search" }), "  abc ");
    await waitFor(() => expect(load).toHaveBeenLastCalledWith("abc"));
    await waitFor(() => expect(optionTexts()).toEqual(["— None —", "Hit 0", "Hit 1"]));
    expect(screen.queryByText(/Showing/)).not.toBeInTheDocument();              // everything fits: no truncation notice
  });

  it("keeps the selection selectable after a new search no longer returns it", async () => {
    const load = vi.fn().mockImplementation((s: string) => Promise.resolve({ items: s ? rows(1, "Other") : rows(2), total: s ? 1 : 2 }));
    const onPick = vi.fn();
    render(<Host load={load} onPick={onPick} />);
    await waitFor(() => expect(optionTexts()).toContain("Item 1"));
    await userEvent.selectOptions(listbox(), "Item1");
    expect(onPick).toHaveBeenCalledWith("Item1", { id: "Item1", name: "Item 1" });
    await userEvent.type(screen.getByRole("searchbox"), "zzz");
    await waitFor(() => expect(optionTexts()).toContain("Other 0"));
    expect(optionTexts()).toContain("Item 1");                                   // the chosen record is not lost
    expect(listbox().value).toBe("Item1");
    await userEvent.selectOptions(listbox(), "");
    expect(onPick).toHaveBeenLastCalledWith("", null);
  });

  it("shows the supplied label for a current value that was never loaded", async () => {
    render(<Host load={() => Promise.resolve({ items: rows(2), total: 2 })} initial="gone" />);
    await waitFor(() => expect(optionTexts()).toContain("Item 1"));
    expect(optionTexts()).toContain("Current record");
    expect(listbox().value).toBe("gone");
  });

  it("shows a retryable error instead of an empty list, and recovers", async () => {
    const load = vi.fn().mockRejectedValueOnce(new Error("x")).mockResolvedValue({ items: rows(1), total: 1 });
    render(<Host load={load} />);
    expect(await screen.findByRole("alert")).toHaveTextContent("The list could not be loaded.");
    await userEvent.click(screen.getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(optionTexts()).toContain("Item 0"));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("ignores a slow response that arrives after a newer search", async () => {
    const pending: Array<(p: PickerPage<Row>) => void> = [];
    const load = vi.fn().mockImplementation(() => new Promise<PickerPage<Row>>((r) => { pending.push(r); }));
    render(<Host load={load} />);
    await waitFor(() => expect(load).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "b" } });
    await waitFor(() => expect(load).toHaveBeenCalledTimes(2));
    await act(async () => { pending[1]!({ items: rows(1, "New"), total: 1 }); });
    await act(async () => { pending[0]!({ items: rows(1, "Stale"), total: 1 }); });
    expect(optionTexts()).toContain("New 0");
    expect(optionTexts()).not.toContain("Stale 0");
  });

  it("is keyboard operable (native search box and listbox) and follows the Arabic catalog", async () => {
    localStorage.setItem("hasm.locale", "ar");
    render(<Host load={() => Promise.resolve({ items: [], total: 0 })} />);
    expect(await screen.findByText("لا نتائج.")).toBeInTheDocument();
    expect(screen.getByRole("searchbox", { name: "Item — بحث" })).toBeInTheDocument();
    expect(optionTexts()).toEqual(["— بلا —"]);
    await userEvent.tab();
    expect(screen.getByRole("searchbox")).toHaveFocus();
    await userEvent.tab();
    expect(listbox()).toHaveFocus();
  });
  it("reloads when reloadKey changes even though load keeps working through a ref", async () => {
    let group = "a";
    const load = vi.fn().mockImplementation(() => Promise.resolve({ items: rows(1, group), total: 1 }));
    const { rerender } = render(<Host load={load} reloadKey="a" />);
    await waitFor(() => expect(optionTexts()).toContain("a 0"));
    group = "b";
    rerender(<Host load={load} reloadKey="b" />);
    await waitFor(() => expect(optionTexts()).toContain("b 0"));
    expect(load).toHaveBeenCalledTimes(2);
    rerender(<Host load={load} reloadKey="b" />);                                   // same key: no extra request
    expect(load).toHaveBeenCalledTimes(2);
  });

  it("gives each picker a distinct search box name and does not announce Loading in the live region", async () => {
    let release: (p: PickerPage<Row>) => void = () => undefined;
    const load = vi.fn().mockImplementation(() => new Promise<PickerPage<Row>>((r) => { release = r; }));
    render(<><Host load={load} label="Customer" /><Host load={load} label="Item" /></>);
    expect(screen.getByRole("searchbox", { name: "Customer — Search" })).toBeInTheDocument();
    expect(screen.getByRole("searchbox", { name: "Item — Search" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByText("Loading…")).toHaveLength(2));
    for (const live of screen.getAllByRole("status")) expect(live).toHaveTextContent("");   // loading is visual only
    for (const l of screen.getAllByText("Loading…")) expect(l).toHaveAttribute("aria-hidden", "true");
    await act(async () => { release({ items: rows(1), total: 5 }); });
  });

  it("coalesces rapid typing into one request", async () => {
    const load = vi.fn().mockResolvedValue({ items: rows(1), total: 1 });
    render(<Host load={load} />);
    await waitFor(() => expect(load).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "a" } });
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "ab" } });
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "abc" } });
    await waitFor(() => expect(load).toHaveBeenLastCalledWith("abc"));
    expect(load).toHaveBeenCalledTimes(2);
  });

  it("is disabled as a whole, and allowNone=false removes the empty choice", async () => {
    render(<Host load={() => Promise.resolve({ items: rows(2), total: 2 })} disabled allowNone={false} />);
    await waitFor(() => expect(optionTexts()).toEqual(["Item 0", "Item 1"]));
    expect(listbox()).toBeDisabled();
    expect(screen.getByRole("searchbox")).toBeDisabled();
  });

  it("never shows a raw id for an unknown selection without a label, and drops stale results after a failed search", async () => {
    const load = vi.fn().mockResolvedValueOnce({ items: rows(2), total: 2 }).mockRejectedValue(new Error("x"));
    render(<Host load={load} initial="3f2a-guid" selectedLabel={null} />);
    await waitFor(() => expect(optionTexts()).toContain("Item 1"));
    expect(optionTexts()).toContain("(selected record not shown)");
    expect(optionTexts().join()).not.toContain("3f2a-guid");
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "zz" } });
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(optionTexts()).not.toContain("Item 1");                                  // the old results are not presented as current
    expect(screen.getByRole("searchbox")).toHaveAttribute("dir", "auto");
  });

  it("drops a response that arrives after unmount without errors", async () => {
    let release: (p: PickerPage<Row>) => void = () => undefined;
    const err = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const { unmount } = render(<Host load={() => new Promise<PickerPage<Row>>((r) => { release = r; })} />);
    await waitFor(() => expect(screen.getByText("Loading…")).toBeInTheDocument());
    unmount();
    await act(async () => { release({ items: rows(1), total: 1 }); });
    expect(err).not.toHaveBeenCalled();
    err.mockRestore();
  });
  it("passes id, invalid, describedBy and required to the list so a real label and an error text can be linked", async () => {
    localStorage.setItem("hasm.locale", "en");
    render(
      <I18nProvider catalog={catalog}>
        <label htmlFor="my-list">Item</label>
        <SearchPicker id="my-list" label="Item" value="" onChange={() => undefined} load={() => Promise.resolve({ items: rows(1), total: 1 })} getId={(r: Row) => r.id} getLabel={(r: Row) => r.name}
          invalid describedBy="my-error" required />
        <p id="my-error">Bad value</p>
      </I18nProvider>,
    );
    const box = await screen.findByRole("combobox", { name: "Item" });
    expect(box).toHaveAttribute("id", "my-list");
    expect(box).toHaveAttribute("aria-invalid", "true");
    expect(box).toHaveAttribute("aria-required", "true");
    expect(box).toHaveAccessibleDescription("Bad value");
    expect(screen.getByRole("searchbox")).toHaveAttribute("aria-controls", "my-list");
    expect(document.querySelector('label[for="my-list"]')).not.toBeNull();
  });

  it("sets none of those attributes when they are not given", async () => {
    render(<Host load={() => Promise.resolve({ items: rows(1), total: 1 })} />);
    const box = await screen.findByRole("combobox", { name: "Item" });
    expect(box).not.toHaveAttribute("aria-invalid");
    expect(box).not.toHaveAttribute("aria-required");
    expect(box).not.toHaveAttribute("aria-describedby");
  });
  it("shows a caller-supplied text for the empty choice instead of the catalog default", async () => {
    localStorage.setItem("hasm.locale", "en");
    render(
      <I18nProvider catalog={catalog}>
        <SearchPicker label="Item" value="" onChange={() => undefined} load={() => Promise.resolve({ items: rows(1), total: 1 })} getId={(r: Row) => r.id} getLabel={(r: Row) => r.name} noneLabel="All customers" />
      </I18nProvider>,
    );
    await screen.findByRole("option", { name: "Item 0" });
    expect(screen.getByRole("option", { name: "All customers" })).toHaveValue("");
    expect(screen.queryByRole("option", { name: "— None —" })).not.toBeInTheDocument();
  });

  it("can disable only the search box while the list stays usable", async () => {
    localStorage.setItem("hasm.locale", "en");
    render(
      <I18nProvider catalog={catalog}>
        <SearchPicker label="Item" value="" onChange={() => undefined} load={() => Promise.resolve({ items: rows(1), total: 1 })} getId={(r: Row) => r.id} getLabel={(r: Row) => r.name} searchDisabled />
      </I18nProvider>,
    );
    await screen.findByRole("option", { name: "Item 0" });
    expect(screen.getByRole("searchbox")).toBeDisabled();
    expect(screen.getByRole("combobox", { name: "Item" })).toBeEnabled();
  });
});
