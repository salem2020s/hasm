# X4-PRESET-ROLE: plan for the three preset migrations (PLAN ONLY, nothing is built)

**Status: PLAN for independent review. Documentation only; no code, no migration file, no number is allocated.** All migration numbers below are **provisional** until the final merge round (system 0036 is allocated to the identity-integrity slice S-1; the presets take the next free system numbers in merge order, never reserved ahead in a pull request).

**Authority.** The owner approved Option A for X4-PRESET-ROLE on 2026-11-22 (section C of the consolidated message, recorded as D-254 once the decision-recording pull request is merged): use the established, narrowly scoped, transactional preset-role migration pattern, including paired DISABLE/ENABLE operations only where genuinely required; every new pair is explicitly reviewed and allowlisted by migration identity and checksum under the hardened guard of PR 188 (merged as `750333e`); legacy approved migrations stay unchanged; after the guard is merged and the implementation is independently verified, the planned Inventory Cost Approver, cash-account and short-close presets may proceed with strict RBAC and segregation of duties; no privileged role is granted automatically to existing users; no incomplete financial feature is activated merely because a preset exists. Nothing in this plan departs from that text.

## 1. What a preset migration is, and why a pair is genuinely required
Preset roles live in the system database: the role row (`t_359332ac`, `preset_key` not null) and its permission rows (`t_3e3d4119`). Two immutability triggers protect them (system migration 0004):
- `r_d3408f58` on the role table refuses an INSERT of a role with a preset key ("preset roles are immutable"), and any UPDATE or DELETE of one;
- `r_2601e368` on the role-permission table refuses any INSERT, UPDATE or DELETE of a row whose role is a preset ("preset role permissions are immutable").

So **creating a preset role needs both triggers paused** (as system migration 0030 did for the Retention Approver), and **adding a permission to an existing preset needs `r_2601e368` paused** (as 0034, MFA policy, did). There is no pair-free route: the alternatives are to replace the guard functions (a function replacement, a larger safeguard change that the guard also refuses unless allowlisted) or to make the preset an ordinary per-company role (the owner's rejected Option B). Therefore a DISABLE/ENABLE pair is genuinely required in every preset migration below. Each is one transaction (the runner's own), re-enabled before it ends and verified after the script by the runner's post-check. Each migration contains exactly the statements it needs and nothing else.

## 2. One migration per preset (recommended), not one combined migration
| | One migration per preset (recommended) | One combined migration |
|---|---|---|
| Review | each is a few lines; the reviewer reads one pair and one insert | one larger file mixing three features |
| Timing | each lands with its own slice (short-close with the short-close service, cash with the cash slice, cost approver with the plan-approval slice), exactly when its permission code exists in the catalog; the code definition and the seed must agree in the same change (the pinned equality test) | couples three independent slices: the first to be ready waits for the last, or ships a seed for a code that does not exist yet |
| Allowlist | one `ApprovedTriggerSwitches` entry each (identity plus checksum) | one entry, but any later edit during one slice's review changes the checksum of all three |
| Numbers | three numbers allocated at their own merge | one number |

The recommendation is three separate system migrations, each its own small pull request or part of its slice, each with its own allowlist entry. A combined migration is acceptable only if all three slices are ready at the same final round; it would then carry the union of the pairs.

## 3. The three presets: exact permission codes
Codes are taken from `PermissionCatalog` on `main`; a code that is not in the catalog yet is said so.

### 3.1 Inventory Cost Approver (new preset role; permission rows only for codes that exist)
- Provisional identity: id `5c1b0f6e-0000-4a00-8000-000000000009` (next after the Retention Approver `...0008`), preset key `inventory-cost-approver`, name "Inventory Cost Approver", active.
- Permissions: **`company.compensationplans.approve`** (warehouse-scoped; privileged under D-096 as an approve action) and nothing else today. It does NOT hold `company.compensationplans.create`: the creator and the approver are different roles, and the service additionally refuses an approver who is the creator, last editor or submitter (X3/X4, D-191).
- **No execute permission, no revaluation and no landed-cost permission is seeded.** Those codes do not exist in the catalog (the revaluation and landed-cost contracts call them "working names"). Each is added to this preset by its own later slice, with its own migration and its own allowlist entry (a preset's permission rows are immutable, so a later addition needs a pair too). While execution is closed the preset therefore grants only what already exists: approving a compensation plan, which changes no books.
- Open point for the reviewer (not an owner question): whether an approver needs a read permission to see a plan. The catalog has none for `company.compensationplans` (actions: create, approve). If reading a plan needs another permission, the plan-approval slice states it and adds it to this preset in the same migration, never to Company Administrator.
- Not granted to anyone: the migration inserts no role assignment. The preset is assigned offline by the owner account through the administrative tool (the D-066 pattern), because a company administrator cannot grant a permission they do not hold.

### 3.2 Cash and bank accounts: two dedicated presets (lead direction: no widening of Company Administrator)
Direction (lead, 2026-11-23): widening the Company Administrator preset counts as granting new privileges to existing users, so the new or sensitive codes get **dedicated presets** and the Company Administrator preset is not changed. The earlier proposal to seed the cash codes into Company Administrator is withdrawn.
- Codes (catalog, Phase 3.14 slice S1): `company.cashaccounts.view`, `.create`, `.update`, `.activate`, and `company.cashaccountssensitive.view`. They stay excluded from Company Administrator in code (`PresetRoles.CashAccountCodes` and the `Except` list remain as they are), so the code definition of Company Administrator and its seed stay equal without any change.
- **Cash Account Manager** (provisional id `5c1b0f6e-0000-4a00-8000-00000000000a`, key `cash-account-manager`): `company.cashaccounts.view`, `.create`, `.update`, `.activate` (branch-scoped, master-data configuration). It does not hold the sensitive code.
- **Cash Account Sensitive Viewer** (provisional id `...0b`, key `cash-account-sensitive-viewer`): `company.cashaccountssensitive.view` only (D-155: the full number is shown only with this separate privileged permission; the resource is privileged by classification, so this preset is privileged and counts for mandatory MFA). The viewer role holds no create or update code, so the person who can read a full number is not the one who changes it unless a company assigns both roles to one person, which the offline assignment makes an explicit act.
- Both are created in ONE migration of the cash slice (two role inserts, five permission inserts, both triggers paused), assigned to nobody by the migration.
- Granting them activates no financial feature: cash and bank posting stays closed (D-214), and deactivation keeps its configuration-stage behaviour until posting is activated (owner decision H of 2026-11-22).

### 3.3 Short close: a dedicated preset
- Code: `company.commercialdocs.close` (branch-scoped, action Close; NOT an approval; not privileged by action or resource). It is not in the catalog on `main`: it arrives with the short-close slice (the work in progress of `feature/3-13-short-close`, which this plan only read), whose catalog change and this seed land together.
- **Commercial Document Closer** (provisional id `...0c`, key `commercial-document-closer`): `company.commercialdocs.close` only. Because the Company Administrator definition is "every catalog code except an exclusion list", adding the code to the catalog would silently add it to Company Administrator; the slice therefore adds `CommercialClose` to that exclusion list in the same change, so Company Administrator is unchanged and the pinned equality of definition and seed holds.
- The WIP migration (provisional system 0034 there) is reusable in the pair and insert shape but changes in substance: it creates a role (both triggers paused, as 0030) instead of inserting into Company Administrator; it is renumbered at the final round and its "ON HOLD" header is rewritten; its checksum changes with it.
- Segregation of duties: closing is audited, needs a reason and is never an approval (D-201); `company.commercialdocs.approve` stays outside Company Administrator (D-066), and neither the Commercial Approver nor the Closer holds the other's code.

## 4. Segregation of duties checked against the approved RBAC rules
- Approvers stay outside Company Administrator: the preset definition keeps its `Except([...])` list of approval codes (fiscal reopen, journal, imports, commercial, retention, compensation plans create and approve). This plan does not change Company Administrator at all; the only edit to that definition is adding the new `company.commercialdocs.close` code to its exclusion list when the code enters the catalog.
- No preset holds both a creating and an approving code of the same resource: Inventory Cost Approver has approve without create; Company Administrator has neither compensation-plan code; the cash sensitive viewer holds no create or update code.
- Privileged classification (D-096): the Inventory Cost Approver is privileged because it holds an approve action, so assigning it counts for the mandatory-MFA rule like the other approver presets; a test asserts this.
- A new test asserts that no preset holds an approve code together with its creator code.

## 5. No automatic grant, no activation
- No migration inserts into role assignments or user roles, and no existing role (Company Administrator included) gains a permission. A test compares the permission rows of every pre-existing preset before and after each migration and requires equality, and counts the assignment rows before and after the migration on a database that already has administrators and requires equality.
- The new role is assigned offline by the owner account through the administrative tool; no endpoint can assign it to oneself (a user cannot grant a permission they lack).
- The seeded codes activate nothing: compensation plan execution stays closed (T2 authorization design, owner decisions D1 to D6), cash and bank posting stays closed (D-214), and the short-close permission only enables an audited action that already needs its own reason.

## 6. The allowlist entries PR 188's `ApprovedTriggerSwitches` needs
Each entry is `(Set = System, Version = <final number>, Name = <final file name>, Checksum = <SHA-256 of the final file, line endings normalized>, Triggers = [...])`. Version, name and checksum are **finalised at the merge round** (a changed comment changes the checksum); the triggers are fixed now, sorted as the guard compares them:

| Migration | Triggers (table/trigger) | Why |
|---|---|---|
| Inventory Cost Approver (new role plus permission) | `t_359332ac/r_d3408f58`, `t_3e3d4119/r_2601e368` | the role insert and the permission insert are both blocked |
| Cash account presets (two new roles plus permissions) | `t_359332ac/r_d3408f58`, `t_3e3d4119/r_2601e368` | role inserts and permission inserts are blocked |
| Short close preset (new role plus permission) | `t_359332ac/r_d3408f58`, `t_3e3d4119/r_2601e368` | same |

Adding an entry is a safeguard change under the procedure of `docs/MIGRATION-GUARD.md`: the owner approval is Option A above, an independent security review reads each migration, and the pinned list in `MigrationTriggerSwitchAllowlistTests` is extended in the same reviewed change (26 entries today, 29 after the three). The guard also refuses a pair copied into another number, set or name, so an entry never covers a different migration.

## 7. Tests (each preset slice)
- **Unit:** the allowlist test (identity, checksum, triggers, count: 26 today, 29 after the three); the guard accepts each new migration as the exact allowlisted text and refuses the same text under another number or set; the preset definitions equal the seed (the existing pinned equality), with Company Administrator unchanged (the cash exclusion stays, the close code is excluded); the SoD test of section 4; the privileged-classification test; the endpoint permission matrix lists every new code.
- **PostgreSQL (isolated cluster):** the migration applies to a system database that already holds administrators and members (upgrade, migration by migration, journal and checksum asserted); the role, its permissions and the two triggers' states afterwards (`O`) are exactly as expected; the role-assignment and user-role counts are unchanged; a pair without the allowlist entry is refused (mutation: remove the entry); every pre-existing preset (Company Administrator included) has exactly the permissions it had before; the real restricted runtime role can still read the new rows and cannot write them; the company runtime and the API resolve permissions with the new codes.
- **Mutation proofs:** drop the pair (the migration then fails on the immutability trigger), drop the allowlist entry (refused at load), widen the permission list (the equality test fails).
- **Regression:** the full local PostgreSQL suite once per head through the CPU gate, the unit suite, hosted exact-head CI; the registry rises by the new PostgreSQL tests of each slice.
- **Shared frontend:** role names are data; if the roles screen lists preset keys, the new preset key and name are added to the Arabic and English message tables once, for Web and Tauri, with the shared component test.

## 8. Order and gating
1. This plan is reviewed independently (documentation only).
2. Each preset lands with its slice, after that slice's own review, in merge order; numbers allocated at the final round; the allowlist entry is written with the final checksum in the same pull request.
3. Nothing here is activated: the feature gates named in section 5 stay closed regardless of the presets.
