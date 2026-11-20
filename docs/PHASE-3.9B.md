# HASM - Phase 3.9B: Customers and suppliers (company-local accounts)

Sources: SRS 7.1 (credit limits, representatives) and 10.1 (receivables and payables, credit limits); ROADMAP 3.9B (customer and supplier roles, terms, credit-limit data, control-account mapping, sales-representative model, screens). OD-3 (DEFAULT): a sales representative is a company-local record (code, Arabic/English name, active flag, optional link to a user), no commission calculation.

Scope guard: this phase builds **configuration only**. No document, balance, journal, statement or credit check exists yet (sales and purchasing documents come with 3.13-3.17). The shared identity of a party stays in the system database and carries none of this.

## Branch and migration coordination

This branch is stacked on PR #54 (tax engine, company 0022 / system 0024) and merges the PR #53 branch (3.9A follow-ups), so migration numbers stay contiguous (the loader requires contiguous versions): **company 0023, system 0025**. PR #48 (3.8D) had reserved 0023/0025 in an earlier handoff; that reservation is released here and #48 renumbers to 0024/0026 when it next rebases (its pinned counts move with it). The merge order is free, but every PR merged after another must re-pin the migration-count tests and the PostgreSQL registry in `backend.yml`.

## Data (company migration 0023, generated: `tools/schema/company-0023-party-roles.cjs`, opaque names derived from `hasm.company.0023.partyroles.v1:<token>`)

* `SalesRepresentative`: code (unique, immutable), Arabic/English name, optional user id (unique when present), active flag, revision. Never deleted.
* `PartyRole`: local party, kind (1 customer, 2 supplier), payment mode (1 cash, 2 credit, 3 mixed), term days (0-3650; none for cash), credit limit (numeric(20,4) in the company base currency, null = none recorded), control account, representative (customers only), active flag, revision. One role per (party, kind); a party can be both. Never deleted.
* PostgreSQL enforces: revision advances by one; identity, party and kind immutable; a role's control account must be an active, postable ASSET account for a customer and LIABILITY account for a supplier (checked whenever it is set); credit limit >= 0; only customers have a representative.

## Service, API and permissions

`PartyRoleService` (audited, one company transaction per write, events 1500-1503, update audit records the previous terms) and `PartyRoleManagementService` (`company.partyroles.view|configure`, `company.salesreps.view|configure`, company-wide, all in the Company Administrator preset by system migration 0025). API: `/api/v1/company/party-roles` (GET `?kind=1|2`, POST, PUT with If-Match) and `/api/v1/company/sales-representatives`; no DELETE route; omitted required fields are errors, an omitted `isActive` never changes state, a PUT replaces the terms (a null clears the limit, account or representative).

## Screen

`pages/PartyRoles.tsx` (route `/company/party-roles`, menu under Company): one shared screen for Web and Tauri with Tabs (Customers, Suppliers, Representatives; a tab appears only with its view permission). Create and edit forms offer only parties without that role and only postable accounts of the right kind; numbers accept Arabic-Indic digits; unsaved state is reported to the shared guard; conflicts reload.

## Tests

3 unit tests (name derivation) + 6 PostgreSQL service tests (`PartyRoleTests`: validation against the chart, terms, audit with previous values, revision fences, immutability, database guards on direct writes, authorization, concurrent creation) + 3 HTTP tests (`PartyRoleApiTests`), registry 979 -> 988; 7 component tests (`partyRoles.test.tsx`).

## Follow-ups

Credit-limit enforcement, aging and statements belong to 3.13-3.17; the party editor may later show the roles of a party; the customer/supplier import adapter is 3.9C.
