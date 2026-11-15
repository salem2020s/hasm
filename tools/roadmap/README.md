# Roadmap generator

`docs/ROADMAP.md`, `docs/RELEASE1-TRACEABILITY.md`, `docs/RELEASE1-DEPENDENCIES.md` and `docs/RELEASE1-ACCEPTANCE.md` are generated from the data in this folder; edit the data, never the generated files.

* `requirements.js` — every traced SRS item and reporting requirement with its phase(s), status and evidence.
* `phases.js` — every remaining phase (scope, prerequisites, deliverables, acceptance, tests, migration, risk).
* `decisions.js` — ambiguities OD-n / T-n with status and resolution.
* `appendix-a.md` — roadmap notes carried over from before the audit.
* `generate.js` — validates and writes the documents. It fails when an SRS requirement id (derived from `docs/SRS.md`) is missing or duplicated, an open requirement maps to no phase, a prerequisite is unknown, the phase graph has a cycle, or the preferred execution order violates a prerequisite.

Run `node tools/roadmap/generate.js` to regenerate and `node tools/roadmap/generate.js --check` to fail on drift (exit code 1 when a committed document differs). Node 20 or later, no dependencies. Wiring the check into an always-on CI job is planned work of Phase 4.0 and is not implemented yet.
