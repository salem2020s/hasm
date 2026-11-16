# HASM — Phase 3.7D: Posting and reversal screens for stock documents (as built)

Last slice of Phase 3.7: the user interface of 3.7B (posting) and 3.7C (reversal). Frontend only; no backend, schema or permission change. One shared screen for Web and Desktop.

* **Stock documents screen** (`StockDocuments.tsx`): a new **Posting** tab on an approved document shows the execution (costing operation, amounts per line, accounting state: pending, not required, posted, failed, reversed, with the journal entry), lets an authorized user post (amounts per line when the document needs them) and reverse (effective date and reason). Buttons follow the capabilities returned by the server (`post`, `reverse`, per warehouse); the server enforces authorization, state, period and concurrency, so nothing here is frontend-only.
* **api-client:** `getStockPosting`, `postStockDocument`, `reverseStockDocument` and the capability fields; messages in Arabic and English (`messages.stock.ts`).
* **Tests:** 6 new component tests (352 frontend tests passing at the time of writing), typecheck and production build green; the Tauri build compiles the same bundle.
* Standing UI rule: tabs through the shared `Tabs` component, Arabic RTL and English LTR, keyboard accessible.
