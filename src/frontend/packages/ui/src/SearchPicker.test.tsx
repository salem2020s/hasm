import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { I18nProvider, SearchPicker, type PickerPage } from "./index";

const catalog = {
  ar: { "picker.search": "بحث", "picker.none": "— بلا —", "picker.showing": "يُعرض {shown} من {total}.", "picker.refine": "ضيّق البحث لإظهار البقية.", "picker.loading": "جارٍ التحميل…", "picker.error": "تعذّر تحميل القائمة.", "picker.retry": "إعادة المحاولة", "picker.no_results": "لا نتائج." },
  en: { "picker.search": "Search", "picker.none": "— None —", "picker.showing": "Showing {shown} of {total}.", "picker.refine": "Refine the search to see the rest.", "picker.loading": "Loading…", "picker.error": "The list could not be loaded.", "picker.retry": "Retry", "picker.no_results": "No results." },
};
interface Row { id: string; name: string }
const rows = (n: number, prefix = "Item"): Row[] => Array.from({ length: n }, (_, i) => ({ id: `${prefix}${i}`, name: `${prefix} ${i}` }));
type Load = (s: string) => Promise<PickerPage<Row>>;

function Hosted({ load, initial, onPick }: { load: Load; initial: string; onPick?: (id: string, r: Row | null) => void }) {
  const [v, setV] = useState(initial);
  return <SearchPicker label="Item" value={v} onChange={(id, r) => { setV(id); onPick?.(id, r); }} load={load} getId={(r) => r.id} getLabel={(r) => r.name} selectedLabel={initial ? "Current record" : undefined} debounceMs={10} />;
}
function Host({ load, initial = "", onPick }: { load: Load; initial?: string; onPick?: (id: string, r: Row | null) => void }) {
  return <I18nProvider catalog={catalog}><Hosted load={load} initial={initial} onPick={onPick} /></I18nProvider>;
}
const listbox = () => screen.getByRole("combobox", { name: "Item" }) as HTMLSelectElement;
const optionTexts = () => Array.from(listbox().options).map((o) => o.textContent);

describe("SearchPicker", () => {
  beforeEach(() => localStorage.setItem("hasm.locale", "en"));

  it("loads one bounded page, says when more matches exist, and sends the trimmed search text", async () => {
    const load = vi.fn().mockImplementation((s: string) => Promise.resolve({ items: s ? rows(2, "Hit") : rows(3), total: s ? 2 : 900 }));
    render(<Host load={load} />);
    expect(await screen.findByText(/Showing 3 of 900\./)).toBeInTheDocument();
    expect(screen.getByText(/Refine the search/)).toBeInTheDocument();
    await userEvent.type(screen.getByRole("searchbox", { name: "Search" }), "  abc ");
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
    expect(screen.getByRole("searchbox", { name: "بحث" })).toBeInTheDocument();
    expect(optionTexts()).toEqual(["— بلا —"]);
    await userEvent.tab();
    expect(screen.getByRole("searchbox")).toHaveFocus();
    await userEvent.tab();
    expect(listbox()).toHaveFocus();
  });
});
