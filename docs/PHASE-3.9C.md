# HASM - Phase 3.9C: Party import adapter and reusable mapping templates

Sources: ROADMAP 3.9C (a parties adapter on the 3.2D framework; reusable mapping templates saved per company and adapter, versioned, never bypassing validation, dry run or approval), REPORTING-REQUIREMENTS section 3 item 11 (acceptance criterion 11, import part), **OD-9 (DEFAULT, in force)**: templates are saved per company and per import adapter, versioned and audited; applying one never skips validation, dry run or approval; per-user templates are not built.

## Slice plan

1. **Reusable mapping templates (this slice, framework level, usable by every adapter).** Company migration 0025.
2. Party import adapter (customers/suppliers and local parties on the 3.2D framework; creates local parties and their shared-identity proposals through the party service so the registry protocol is preserved).

## Slice 1 - templates (implemented)

* **Data** (company migration 0025, generated: `tools/schema/company-0025-import-templates.cjs`, names from `hasm.company.0025.importtemplates.v1:<token>`): `ImportMappingTemplate` (adapter code, name, version, retired flag, content JSON, creator, time). PostgreSQL: append-only (no update, delete or truncate), the version of one (adapter, name) is exactly one above the latest (no gaps), the adapter code and name are well formed, the content is bounded to 64 KB.
* **Content** is by HEADER NAME, never by column position: `fields: { key: { header, default } }` plus the number/date options, so the template applies to any file whose headers match (after the same normalization the automatic suggestion uses: case, spaces, underscores, dashes, Arabic-Indic digits). A header that is empty or equal to another header after normalization cannot be saved (`header_unusable`, `header_ambiguous`): such a template could not be re-applied deterministically.
* **Apply only pre-fills.** `POST .../imports/{id}/mapping/apply-template` resolves the latest NON-retired version against the staged headers and returns the mapping and the fields it could not place (`unmatched`: a missing or ambiguous header, or a column already taken); it writes nothing to the batch. The screen puts the result in the mapping form; saving it goes through the ordinary mapping validation (a required field left unmapped is refused), then the dry run, the independent approval and the plan hash, exactly as before. Content saved for different fields than the adapter currently has is refused (`template_incompatible`), never guessed.
* **Versions and concurrency.** A save names the version it is based on (0 = new; absent = 428; stale = 412 with the current version); one writer per template at a time (advisory lock); retirement is a new version that keeps the content and flags it retired; saving again revives it.
* **Authorization and audit.** Read: `company.imports.view` plus the adapter's own view permission (an adapter the caller cannot see shows no templates). Save, apply and retire: `company.imports.upload` plus the adapter's own upload permission, exactly like the mapping they pre-fill. Audit events 1110 (saved) and 1111 (retired).
* **API** (under `/api/v1/company/imports`): `GET templates?adapter=`, `GET templates/{adapter}/{name}/history`, `POST {id}/mapping/template` (save the batch's SAVED mapping), `POST {id}/mapping/apply-template`, `POST templates/{adapter}/{name}/retire`. No DELETE.
* **Screen:** a "Mapping templates" box in the mapping card of the shared Imports screen (Web and Tauri): apply a template (retired ones are not offered; unmatched fields are named), save the saved mapping as the next version of a named template (enabled only once the batch mapping is saved).
* **Tests:** 7 unit (content, validation, application, ambiguity, name derivation), 5 PostgreSQL service tests (`ImportTemplateTests`), 2 HTTP tests (`ImportTemplateApiTests`), 4 component tests; registry 1003 -> 1010; migration pins company 25.

## Migration coordination

Stacked on PR #57 (company 0024, system 0026): company 0025, no system migration in this slice. Contiguous numbering follows the merge order (see docs/INTEGRATION-STATUS.md).
