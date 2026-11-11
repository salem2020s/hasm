# Phase 3.1A-2 — Company configuration schema (migration 0003) — **APPROVED (revised)**

**Status: approved by the project owner with the corrections below. This revision supersedes the earlier proposal text.** Implementation follows this document. Anything marked *Impl.* is an implementation choice made under that approval (documented, configurable where stated).

## 0. Approved requirements vs. implementation choices

Approved sources: SRS, DECISIONS (D-036 Trial; base currency; multi-currency), phase instruction, and the owner's approval of this document.
Excluded (no approved rules): tax rates/behaviour, tax-number formats, chart of accounts, costing method, negative stock, numbering, **monetary/quantity precision and rounding** (belong to accounting/inventory modules), exchange rates, fiscal calendar.

"Locked after financial activity" means: once a registered financial-history probe reports history, the field becomes immutable. **No such probe is implemented in this phase** (see §6).

## 1. CompanyProfile (one row; created by the first valid authorized PUT, never by GET)

| Field | Purpose | Type | Required | Default | Validation | Locked after financial activity |
|---|---|---|---|---|---|---|
| singleton | Singleton key | boolean PK = true | yes | true | CHECK | n/a |
| name_ar | Arabic company name | text | optional* | NULL | trim, NFC, 1-200, no control chars | no |
| name_en | English company name | text | optional* | NULL | same | no |
| legal_name | Registered legal name | text | no | NULL | 1-200 | no |
| commercial_registration_no | Registry number (country-neutral) | text | no | NULL | trim, 1-64, printable Unicode letters/digits/punctuation, no control chars | no |
| tax_registration_no | Tax identification number as issued (storage only; no tax behaviour) | text | no | NULL | same, 1-64 | no |
| country_code | ISO 3166-1 alpha-2 | char(2) | **yes** | none (never defaulted) | uppercase, in fixed ISO list | no |
| address_line1/2, city, region | Address | text | no | NULL | 1-200 / 1-100 | no |
| postal_code | Postal code | text | no | NULL | 1-20, no country format | no |
| phone | Phone | text | no | NULL | 5-30; digits, space, `+ - ( ) .` | no |
| email | Contact email | text | no | NULL | <=254, one address, syntax | no |
| website | Site | text | no | NULL | <=200, absolute http/https URL | no |
| revision | Optimistic-concurrency token | bigint | yes | 1 | >0, +1 per change | n/a |
| updated_at | UTC time | timestamptz | yes | server-set | never client-supplied | n/a |

\* **At least one of name_ar / name_en is required** (database CHECK + API). Display name = the name of the viewer's language, falling back to the other.

## 2. CompanySettings (one row; created by the first valid authorized PUT)

| Field | Purpose | Type | Required | Default | Validation | Locked after financial activity |
|---|---|---|---|---|---|---|
| singleton | Singleton key | boolean PK | yes | true | CHECK | n/a |
| default_language | Company default UI/document language (independent of each user's own language preference) | text | yes | `ar` | `ar`,`en` | no |
| date_format | Display format | text | yes | `yyyy-MM-dd` | `yyyy-MM-dd`,`dd/MM/yyyy`,`MM/dd/yyyy` | no |
| numeral_system | Digit display | text | yes | `latin` | `latin`,`arabic-indic` | no |
| time_zone | Company business time zone | text NULL | **no: NULL = setup incomplete** | NULL (never invented) | valid IANA id known to the server | **yes** (future probe) |
| revision, updated_at | as above | | | | | |

Until `time_zone` is set the API reports `setupComplete:false`; `ICompanyBusinessClock` refuses (`company_time_zone_not_configured`) for any future operation that needs company-local business dates. UTC is only the internal timestamp standard. **amount/quantity decimal places and every rounding rule are removed from this phase.**

## 3. Currency (reference; seeded, runtime has SELECT only)

| Field | Type | Notes |
|---|---|---|
| code | char(3) PK | ISO 4217 alphabetic, `^[A-Z]{3}$`; identifiers are never deleted or reused |
| numeric_code | char(3) UNIQUE | ISO 4217 numeric |
| name_en | text | fallback name; display names are localised from shared localisation resources (`Intl.DisplayNames`) with this fallback |
| minor_units | smallint NULL | standard exponent 0-4; **NULL where ISO defines none** (never invented) |
| is_active | boolean | false = withdrawn/historical (kept for history; cannot be newly enabled) |
| dataset_version | integer | version of the reviewed reference dataset that introduced/last revised the row |

Updates are versioned migrations. Dataset v1 is a reviewed list that **must be verified against the current ISO 4217 amendment before release** (recorded as a release item).

## 4. CompanyCurrency

| Field | Type | Notes |
|---|---|---|
| currency_code | char(3) PK, FK Currency | |
| is_base | boolean | partial unique index: at most one base; CHECK is_base ⇒ enabled |
| enabled | boolean | the base currency can never be disabled |
| revision, updated_at | bigint / timestamptz | per-row concurrency |

No base currency exists until selected during setup (posting is blocked until then). Rows are never deleted (trigger). **Trial: only the base currency may be enabled; enabling another is denied (`entitlement_denied`).** Switching the base while unlocked replaces the old base (disabled, not deleted). An empty fiscal year does not lock the base currency.

## 5. ConfigRevision (append-only)

| Field | Type | Notes |
|---|---|---|
| id | bigint identity PK | |
| entity | text | `profile`,`settings`,`currency` |
| entity_key | varchar(3) | currency code, else `''` |
| entity_revision | bigint | UNIQUE with (entity, entity_key) |
| actor_user_id | uuid | no FK (users live in Hasm_System) |
| changed_at | timestamptz | UTC |
| changes | jsonb | field-level before/after **only for explicitly permitted fields**; sensitive fields store the field name only |
| audit_stream, audit_seq | varchar(48), bigint | composite **FK to the business audit event of the same transaction**, UNIQUE: one event ↔ one revision |

UPDATE/DELETE/TRUNCATE refused by triggers (even for the owner); runtime role holds SELECT, INSERT only. Audit event Detail carries field names, the new revision and a SHA-256 prefix of the canonical `changes` (tamper-evident link) — never values of sensitive fields.

**Permitted before/after fields:** name_ar, name_en, legal_name, country_code, city, region, all settings, all currency attributes. **Sensitive (names only, no values in history or audit):** commercial_registration_no, tax_registration_no, address_line1/2, postal_code, phone, email, website. API responses carry them only to holders of `company.profile.view`.

## 6. Contracts and policies

- **Base-currency / setting lock (`IFinancialHistoryProbe`).** A registry lists required domains (`general_ledger`, `inventory_valuation`, `opening_balances`) each with `Implemented=false` today. A change to a locked field is refused if any probe reports history. Lock status is reported as `lockChecksImplemented:false` with the unimplemented domains — never as "safe". A guard test fails if a migration adds a table outside the approved list without registering a probe, so the contract cannot become a silent bypass.
- **Trial entitlement (`IEntitlementPolicy`).** Default edition is Trial; there is no allow-all production policy. Enforced now: single enabled currency. Not enforced yet, tracked as mandatory release/integration requirements, with the **existing pathways that can bypass them** (users: `POST /api/v1/admin/users`, `Hasm.Admin bootstrap-admin`, `grant-company-admin`, membership grants; company count: `register-company`/`provision-company`) are documented in DEVELOPMENT.md. Branches, warehouses and the 100-transaction limit await their modules.
- **Permissions (system migration 0006):** `company.profile.view|update`, `company.settings.view|update`, `company.currencies.view|update`, `company.config.view`. Company Administrator: all. Access Auditor: `company.config.view`. Access Manager: none.
- **Audit events (business stream):** 1000 profile created, 1001 profile updated, 1002 settings created, 1003 settings updated, 1004 currency enabled, 1005 currency disabled, 1006 base currency set.
- **Display-name projection (system migration 0006):** `display_name_ar`, `display_name_en` (nullable) and `display_name_revision bigint` on the registry; written only `WHERE display_name_revision < @rev`; failure after commit is reported (`projection:"pending"`) and recovered by an explicit sync endpoint and by any later authorized profile read/write.
- **Encryption at rest of sensitive identifiers is not implemented** (no approved key-management design yet): recorded as a release blocker.
