# HASM — Phase 3.9A: Party registry (shared identity and company-local record)

Sources: SRS §3 (bullets 3 and 4: `Party` with `GlobalPartyId` in the system database and `LocalPartyId` in the company; shared identifying data versus independent financial data per company; change of shared identifying data by proposal of an authorized user, central approval and audit, never a view of another company's balances), SRS §17 bullet 4 (no distributed atomicity across databases: reliable request log, retry and reconciliation, local event kept consistent), SRS §19 rule 4 (no cross-company visibility through a shared party identity or a user session), scenario 21.3 (a user of company A cannot query company B balances even when both share a `GlobalPartyId`); ARCHITECTURE §4 (Parties module), §5 (company isolation, outbox, no cross-database commit), §6 (opaque physical names); DECISIONS D-006; ROADMAP 3.9A.

This slice builds the **registry only**: no customer or supplier role, account, term, credit limit, control account or balance (Phase 3.9B), no import (3.9C).

## Design and decisions (written before the implementation; standing authority, conservative where the SRS is silent)

### Data placement
* **System database — the shared identity** (`GlobalParty`): party type (1 organization, 2 individual), legal name in Arabic (required) and English (optional), tax number and commercial registration number (optional), status (1 active, 2 inactive), revision. **Nothing else**: no balance, account, term, limit, address book, contact, note or any company-specific value; PostgreSQL has no column for them (a test pins the column set).
* **System database — the central proposals** (`PartyProposal`) and the **links** (`PartyLink`: company, its `LocalPartyId`, the `GlobalPartyId`, the approving proposal). A link is created only by a central approval and is append-only (unlinking is outside this slice).
* **Company database — the local record** (`LocalParty`, id = `LocalPartyId`): company-local code, the company's own display name, notes, active flag, link state (1 pending central decision, 2 linked, 3 proposal rejected) and the `GlobalPartyId` once linked (immutable once set). Company-specific data never goes to the system database.
* **Company database — the request log (outbox)** (`PartyRequest`): one row per cross-database request, its id is the **idempotency key** sent to the system database; kind (1 new shared identity, 2 change of shared data), the proposed values as typed columns, delivery status (1 pending, 2 delivered, 3 completed), outcome (1 approved, 2 rejected, 3 refused by the registry), attempts, last error code. At most one open request (pending or delivered) per local party (unique index).

### D1 — who approves centrally
The SRS requires a central approval by an authorized actor but names no role. The only central (cross-company) role that exists is the **identity administrator** (system-level flag, `IdentityAdministrator` policy, `/api/v1/admin/*`). Decision: the central approval queue is reserved to **active identity administrators** (checked by the policy and again inside the approving transaction), and the approver is **never the proposer** (service and PostgreSQL CHECK). A dedicated "master data approver" role is deferred (no new system-level RBAC is invented here).

### D2 — company permissions (system migration 0022)
Company-scoped permissions `company.parties.view | create | update | submit | configure`, all in the Company Administrator preset: *view* the local parties and the shared identity they are linked to; *create* a local party (which always proposes its shared identity); *update* the company-local fields; *submit* a proposal to change the shared data of a linked party, or to propose again after a rejection; *configure* synchronize the request log and run the reconciliation. No company permission can approve.

### D3 — how a company links to an existing shared identity without seeing the registry
Company users can **never search or list the central registry** (that would disclose which parties other companies deal with). A new local party always carries a proposal of identifying data; the **central approver** compares it with the registry (search by name or identifier, central-only endpoint) and decides: approve as a **new** `GlobalParty`, approve by **linking to an existing** active `GlobalParty`, or reject with a note. A company therefore only ever reads the identities it is linked to, through its own link (the query joins the link of the session's company).

### D4 — uniqueness of identifiers
To prevent duplicate shared identities, a tax number and a registration number are each unique in the registry (normalized: trimmed, upper case, inner spaces removed; letters, digits, `-`, `/`, `.` only, at most 50). An approval that would create or change an identity into a duplicate identifier is refused (`identifier_in_use`); the approver links to the existing identity instead. A company can link at most one local party to a given `GlobalPartyId` (unique in both databases).

### D5 — cross-database protocol (SRS 17.4: no distributed commit)
1. **Local first:** the local party (or the change request) and its outbox row are written in ONE company transaction with their business audit events.
2. **Delivery** (best effort right after the commit, and again on every retry): the system database inserts the proposal keyed by `(company, request id)` **only if absent** (unique constraint; a repeated delivery returns the existing proposal), with a system audit event; then a company transaction marks the request delivered. A failure between the two leaves the request pending locally; the next retry finds the proposal and only records the delivery: **no duplicate**.
3. **Outcome:** after the central decision, the company pulls it (synchronize, reconciliation, or the next delivery attempt) and applies it in one company transaction: approved → the local party is linked (`GlobalPartyId` set, state linked); rejected → state rejected (a new proposal is possible); a change proposal only completes the request (the shared data is read live through the link).
4. A permanent refusal by the registry (e.g. a change proposal for a party the company is not linked to) completes the request with outcome *refused* instead of retrying forever.
5. Failures are recorded on the request (attempt count, short error code, never a message) with an audit event; nothing is retried in the background in this slice (no worker exists yet): the company user (permission *configure*) runs **Synchronize** or the **Reconciliation**; the UI shows pending requests.

### D6 — reconciliation
`reconcile` compares the company's request log and local links with the system proposals and links **of that company only** and reports: `request_not_delivered` (repairable: deliver), `request_delivered_not_recorded` (repairable: record), `outcome_not_applied` (repairable: apply), `link_missing_locally` (system link for an existing local party that is not linked; repairable: apply the link), `request_missing_in_system` and `link_missing_in_system` (local state the registry does not confirm: reported, never fabricated), `link_mismatch` (local and system disagree on the `GlobalPartyId`: reported), `orphan_system_link` and `proposal_unknown_locally` (the registry knows a local party or request the company database does not have, e.g. after restoring an older company backup — SRS 21.8: reported). With `repair` the repairable findings are fixed by the same idempotent operations and one audit event records the run; without it the call reads only.

### D7 — audit and immutability
Company business audit events 1300–1308 (local party created/updated, request queued/delivered/delivery failed, outcome linked/rejected, change completed, reconciliation repaired); system audit events 1310–1314 on the system stream (proposal received, approved as new identity, approved by linking, change approved, rejected). PostgreSQL refuses: deleting or truncating any of the five tables; changing a decided proposal, a link, the proposed values or creator of a request, the identity of a local party or its `GlobalPartyId` once set; a decision by the proposer; an illegal status transition.

### D8 — least privilege
Company runtime role: SELECT, INSERT, UPDATE on `LocalParty` and `PartyRequest` (no DELETE). The system tables are used by the system runtime account (SELECT, INSERT, UPDATE on `GlobalParty` and `PartyProposal`, SELECT, INSERT on `PartyLink`), granted by the DBA like the other system tables (docs/DEVELOPMENT.md).

### D9 — migration numbers
Company migration 0020 and system migration 0022 are the next numbers on `main` (760a97a). Phase 3.8B-3 (open in parallel) uses the same numbers; whichever merges second renumbers its migrations and the count fixtures (migration versions must be contiguous).

## API (both clients)
* `/api/v1/company/parties` (session company only, `no-store`, anti-CSRF, `If-Match` on changes): list, get, create, update (local fields), `propose` (change of shared data or a new proposal after a rejection), `sync`, `reconcile` (`?repair=true`).
* `/api/v1/admin/parties` (identity administrators only): `proposals` (list, get), `proposals/{id}/approve` (`{ linkTo }` optional), `proposals/{id}/reject` (`{ note }`), `search` (registry search for the approver).

## UI
Shared React screens for Web and Tauri: **Parties** (`/company/parties`, tabs: Details, Shared identity, Requests) and **Party approvals** (`/admin/party-proposals`, identity administrators only); Arabic RTL and English LTR (`messages.party.ts`).
