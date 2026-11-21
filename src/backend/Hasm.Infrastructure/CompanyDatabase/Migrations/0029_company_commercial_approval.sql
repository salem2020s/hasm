-- Migration 0029: commercial document approval, numbering and server-produced snapshots (Phase 3.13 slice S1b). Forward-only: additive nullable columns on the two document tables, one new index, and CREATE OR REPLACE of
-- the three guard functions of migration 0028 (their names are kept; no object is dropped and no generated name is reused for a different object). No existing row is read or modified, nothing is seeded.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-0029-commercial-approval.cjs (logical dictionary: CompanySchema.CommercialApproval.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS ADDS (docs/PHASE-3.13.md S1b; owner decisions D-049..D-052): the official number (allocated at the FIRST approval, kept through re-approval, voided and never reused on cancel), the approval record, the exchange rate,
-- the submit fingerprint and the price/tax snapshots (produced by the server; sealed at approval), plus the full commercial-status guard: draft -> submitted -> approved or rejected, re-approval (approved -> draft), reopening
-- (rejected -> draft), and the cancellation of an approved document. Physical stock and accounting effects are later slices: the guard knows nothing of them yet.
-- Invariants enforced by PostgreSQL:
--   * an approved document has its number, approver, seal and fingerprint; the approver is never the creator, the last editor or the submitter; the sealed fingerprint equals the one produced at submit (a changed price is refused);
--   * the date of a numbered document stays inside the fiscal year of its number; the quote of a submitted document is frozen when it is decided (approved, rejected or cancelled);
--   * the official number is set ONLY by the approval transition and never changes or disappears afterwards; the mode never changes once a number exists; an approved document changes only its note (anything material goes back to draft);
--   * a draft carries no submitter, approval, seal, fingerprint or snapshot (they are produced at submit and approval);
--   * every event must describe a transition the guard allows (kind, from-status and to-status), continue the status of the previous event and carry the document's current revision and status.

ALTER TABLE t_04d17dc0 ADD COLUMN c_2c5d8f9f varchar(60) NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_c8e6ee5a uuid NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_2f938a9f uuid NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_1fd181aa timestamptz NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_ded55736 timestamptz NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_087f9d00 uuid NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_e05c15dc numeric(28,12) NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_b2fcdc92 varchar(60) NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_8e5953af char(64) NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_608f77c6 text NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_47fb663d char(64) NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_cf5e89bf numeric(28,6) NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_62ca242b numeric(28,6) NULL;
ALTER TABLE t_04d17dc0 ADD COLUMN c_f510980d numeric(28,6) NULL;
ALTER TABLE t_04d17dc0 ADD CONSTRAINT k_6fdf2408 FOREIGN KEY (c_c8e6ee5a) REFERENCES t_4b5bb222 (c_cae1c56a);
ALTER TABLE t_04d17dc0 ADD CONSTRAINT k_c41c52db CHECK (c_8cb62046 <> 3 OR (c_2c5d8f9f IS NOT NULL AND c_2f938a9f IS NOT NULL
    AND c_1fd181aa IS NOT NULL AND c_ded55736 IS NOT NULL AND c_8e5953af IS NOT NULL AND c_c8e6ee5a IS NOT NULL));
ALTER TABLE t_04d17dc0 ADD CONSTRAINT k_1323aceb CHECK (c_8e5953af IS NULL OR (c_47fb663d ~ '^[0-9a-f]{64}$' AND c_608f77c6 IS NOT NULL
    AND c_e05c15dc IS NOT NULL AND c_e05c15dc > 0 AND c_cf5e89bf IS NOT NULL AND c_62ca242b IS NOT NULL AND c_f510980d IS NOT NULL));
CREATE UNIQUE INDEX k_f0530938 ON t_04d17dc0 (c_56e38e95, c_2c5d8f9f) WHERE c_2c5d8f9f IS NOT NULL;

ALTER TABLE t_33fca2e4 ADD COLUMN c_e9275526 text NULL;
ALTER TABLE t_33fca2e4 ADD COLUMN c_7f4ef4e3 char(64) NULL;
ALTER TABLE t_33fca2e4 ADD COLUMN c_75d6f071 numeric(28,6) NULL;
ALTER TABLE t_33fca2e4 ADD CONSTRAINT k_5108e179 CHECK ((c_e9275526 IS NULL) = (c_7f4ef4e3 IS NULL) AND (c_7f4ef4e3 IS NULL) = (c_75d6f071 IS NULL)
    AND (c_7f4ef4e3 IS NULL OR c_7f4ef4e3 ~ '^[0-9a-f]{64}$'));

CREATE OR REPLACE FUNCTION f_bc72c55d() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.c_8cb62046 <> 1 THEN RAISE EXCEPTION 'a commercial document is created as a draft'; END IF;
        IF NEW.c_d2cb57e3 <> 1 THEN RAISE EXCEPTION 'a commercial document is created at revision 1'; END IF;
        IF NEW.c_abe96b9c IS NOT NULL OR NEW.c_2c5d8f9f IS NOT NULL OR NEW.c_2f938a9f IS NOT NULL
           OR NEW.c_ded55736 IS NOT NULL OR NEW.c_8e5953af IS NOT NULL THEN RAISE EXCEPTION 'a draft carries no submitter, number, approval or seal'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'commercial documents are never removed'; END IF;
    IF NEW.c_cceb8d4c IS DISTINCT FROM OLD.c_cceb8d4c OR NEW.c_56e38e95 IS DISTINCT FROM OLD.c_56e38e95
       OR NEW.c_aae9b28d IS DISTINCT FROM OLD.c_aae9b28d OR NEW.c_6258e1f8 IS DISTINCT FROM OLD.c_6258e1f8
       OR NEW.c_b9cf066a IS DISTINCT FROM OLD.c_b9cf066a OR NEW.c_79b8937e IS DISTINCT FROM OLD.c_79b8937e
       OR NEW.c_5412b89c IS DISTINCT FROM OLD.c_5412b89c THEN
        RAISE EXCEPTION 'the identity of a commercial document is immutable';
    END IF;
    IF NEW.c_d2cb57e3 <> OLD.c_d2cb57e3 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NOT ((OLD.c_8cb62046 = 1 AND NEW.c_8cb62046 IN (1, 2, 5)) OR (OLD.c_8cb62046 = 2 AND NEW.c_8cb62046 IN (1, 3, 4, 5))
            OR (OLD.c_8cb62046 = 3 AND NEW.c_8cb62046 IN (1, 3, 5)) OR (OLD.c_8cb62046 = 4 AND NEW.c_8cb62046 IN (1, 5))) THEN
        RAISE EXCEPTION 'illegal commercial document status transition';
    END IF;
    -- the number: set ONLY by the approval, never changed or removed afterwards; the fiscal year it was issued in goes with it; the mode is frozen once numbered
    IF NEW.c_2c5d8f9f IS DISTINCT FROM OLD.c_2c5d8f9f THEN
        IF OLD.c_2c5d8f9f IS NOT NULL THEN RAISE EXCEPTION 'the official number never changes'; END IF;
        IF NOT (OLD.c_8cb62046 = 2 AND NEW.c_8cb62046 = 3) THEN RAISE EXCEPTION 'the official number is issued only by the approval'; END IF;
    END IF;
    IF OLD.c_2c5d8f9f IS NOT NULL AND (NEW.c_c8e6ee5a IS DISTINCT FROM OLD.c_c8e6ee5a
       OR NEW.c_523bf0a9 IS DISTINCT FROM OLD.c_523bf0a9) THEN RAISE EXCEPTION 'the fiscal year of the number and the mode are frozen once the document is numbered'; END IF;
    -- the date of a NUMBERED document stays inside the fiscal year of its number (D-068): a date outside it would leave the number in a year the document no longer belongs to
    IF NEW.c_c8e6ee5a IS NOT NULL AND (NEW.c_e55881ff IS DISTINCT FROM OLD.c_e55881ff OR NEW.c_8cb62046 = 3)
       AND NOT EXISTS (SELECT 1 FROM t_4b5bb222 y WHERE y.c_cae1c56a = NEW.c_c8e6ee5a AND NEW.c_e55881ff BETWEEN y.c_5fd77e71 AND y.c_a4d1e8a9) THEN
        RAISE EXCEPTION 'the date of a numbered document stays within the fiscal year of its number';
    END IF;
    -- the quote of a SUBMITTED document is frozen when it is decided: approval, rejection or cancellation never rewrite the sealed content
    IF OLD.c_8cb62046 = 2 AND NEW.c_8cb62046 IN (3, 4, 5) AND (NEW.c_608f77c6 IS DISTINCT FROM OLD.c_608f77c6 OR NEW.c_47fb663d IS DISTINCT FROM OLD.c_47fb663d
       OR NEW.c_e05c15dc IS DISTINCT FROM OLD.c_e05c15dc OR NEW.c_b2fcdc92 IS DISTINCT FROM OLD.c_b2fcdc92
       OR NEW.c_cf5e89bf IS DISTINCT FROM OLD.c_cf5e89bf OR NEW.c_62ca242b IS DISTINCT FROM OLD.c_62ca242b
       OR NEW.c_f510980d IS DISTINCT FROM OLD.c_f510980d OR NEW.c_8e5953af IS DISTINCT FROM OLD.c_8e5953af) THEN
        RAISE EXCEPTION 'the quote of a submitted document is frozen when it is decided';
    END IF;
    IF NEW.c_8cb62046 <> OLD.c_8cb62046 AND (NEW.c_b2463fae IS DISTINCT FROM OLD.c_b2463fae OR NEW.c_496bb3d6 IS DISTINCT FROM OLD.c_496bb3d6
       OR NEW.c_e55881ff IS DISTINCT FROM OLD.c_e55881ff OR NEW.c_523bf0a9 IS DISTINCT FROM OLD.c_523bf0a9
       OR NEW.c_2bf97e4a IS DISTINCT FROM OLD.c_2bf97e4a) THEN
        RAISE EXCEPTION 'a status change does not edit the document';
    END IF;
    IF OLD.c_8cb62046 = 3 AND NEW.c_8cb62046 = 3 AND (NEW.c_b2463fae IS DISTINCT FROM OLD.c_b2463fae OR NEW.c_496bb3d6 IS DISTINCT FROM OLD.c_496bb3d6
       OR NEW.c_e55881ff IS DISTINCT FROM OLD.c_e55881ff OR NEW.c_523bf0a9 IS DISTINCT FROM OLD.c_523bf0a9
       OR NEW.c_2f938a9f IS DISTINCT FROM OLD.c_2f938a9f OR NEW.c_ded55736 IS DISTINCT FROM OLD.c_ded55736
       OR NEW.c_8e5953af IS DISTINCT FROM OLD.c_8e5953af OR NEW.c_47fb663d IS DISTINCT FROM OLD.c_47fb663d
       OR NEW.c_608f77c6 IS DISTINCT FROM OLD.c_608f77c6 OR NEW.c_1fd181aa IS DISTINCT FROM OLD.c_1fd181aa
       OR NEW.c_e05c15dc IS DISTINCT FROM OLD.c_e05c15dc OR NEW.c_b2fcdc92 IS DISTINCT FROM OLD.c_b2fcdc92
       OR NEW.c_cf5e89bf IS DISTINCT FROM OLD.c_cf5e89bf OR NEW.c_62ca242b IS DISTINCT FROM OLD.c_62ca242b
       OR NEW.c_f510980d IS DISTINCT FROM OLD.c_f510980d) THEN
        RAISE EXCEPTION 'an approved document changes only its note';
    END IF;
    -- submit: the submitter and the fingerprint of the server-produced quote are recorded
    IF NEW.c_8cb62046 = 2 AND OLD.c_8cb62046 = 1 AND (NEW.c_abe96b9c IS NULL OR NEW.c_8e5953af IS NULL) THEN RAISE EXCEPTION 'a submitted document names its submitter and carries its quote fingerprint'; END IF;
    -- approve: independent approver, the number, the seal, and the sealed fingerprint EQUALS the submitted one (a changed price is refused)
    IF NEW.c_8cb62046 = 3 AND OLD.c_8cb62046 = 2 THEN
        IF NEW.c_2f938a9f IS NULL OR NEW.c_2f938a9f = NEW.c_abe96b9c OR NEW.c_2f938a9f = NEW.c_6258e1f8
           OR NEW.c_2f938a9f = NEW.c_087f9d00 THEN RAISE EXCEPTION 'the approver must be independent of the creator, the last editor and the submitter'; END IF;
        IF NEW.c_2c5d8f9f IS NULL OR NEW.c_ded55736 IS NULL OR NEW.c_8e5953af IS DISTINCT FROM OLD.c_8e5953af THEN
            RAISE EXCEPTION 'an approval issues the number and seals the fingerprint that was submitted'; END IF;
    END IF;
    IF NEW.c_8cb62046 = 1 AND (NEW.c_abe96b9c IS NOT NULL OR NEW.c_2f938a9f IS NOT NULL OR NEW.c_ded55736 IS NOT NULL
       OR NEW.c_8e5953af IS NOT NULL OR NEW.c_47fb663d IS NOT NULL) THEN RAISE EXCEPTION 'a draft carries no submitter, approval, seal or snapshot'; END IF;
    IF NEW.c_8cb62046 = 5 AND NEW.c_abe96b9c IS DISTINCT FROM OLD.c_abe96b9c THEN RAISE EXCEPTION 'cancellation keeps the submitter record'; END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION f_7d3cbe20() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        -- an event describes what ALREADY happened to its document in this transaction: its revision and resulting status must be the document's current ones (no forged or future event)
        IF NOT EXISTS (SELECT 1 FROM t_04d17dc0 d WHERE d.c_cceb8d4c = NEW.c_20dec8a8 AND d.c_d2cb57e3 = NEW.c_dc2643b7 AND d.c_8cb62046 = NEW.c_166ae51d) THEN
            RAISE EXCEPTION 'a commercial document event must carry the current revision and status of its document';
        END IF;
        -- revision 1 is the creation (and only the creation); the event kind, from-status and to-status must be one of the transitions the document guard allows
        IF (NEW.c_dc2643b7 = 1) <> (NEW.c_1304e0f0 = 1) THEN RAISE EXCEPTION 'only the creation is the first event of a commercial document'; END IF;
        IF NOT ((NEW.c_1304e0f0 = 1 AND NEW.c_d2a4f738 IS NULL AND NEW.c_166ae51d = 1 AND NEW.c_dc2643b7 = 1)
             OR (NEW.c_1304e0f0 = 2 AND NEW.c_d2a4f738 = NEW.c_166ae51d AND NEW.c_166ae51d IN (1, 3))
             OR (NEW.c_1304e0f0 = 3 AND NEW.c_d2a4f738 = 1 AND NEW.c_166ae51d = 2)
             OR (NEW.c_1304e0f0 = 4 AND NEW.c_d2a4f738 = 2 AND NEW.c_166ae51d = 1)
             OR (NEW.c_1304e0f0 = 5 AND NEW.c_d2a4f738 IN (1, 2, 3, 4) AND NEW.c_166ae51d = 5)
             OR (NEW.c_1304e0f0 = 6 AND NEW.c_d2a4f738 = 2 AND NEW.c_166ae51d = 3)
             OR (NEW.c_1304e0f0 = 7 AND NEW.c_d2a4f738 = 2 AND NEW.c_166ae51d = 4)
             OR (NEW.c_1304e0f0 = 8 AND NEW.c_d2a4f738 = 3 AND NEW.c_166ae51d = 1)
             OR (NEW.c_1304e0f0 = 9 AND NEW.c_d2a4f738 = 4 AND NEW.c_166ae51d = 1)) THEN
            RAISE EXCEPTION 'a commercial document event kind does not match its transition';
        END IF;
        -- an event continues the status the previous event left
        IF NEW.c_dc2643b7 > 1 AND NOT EXISTS (SELECT 1 FROM t_9ff18b1b e WHERE e.c_20dec8a8 = NEW.c_20dec8a8
           AND e.c_dc2643b7 = NEW.c_dc2643b7 - 1 AND e.c_166ae51d = NEW.c_d2a4f738) THEN
            RAISE EXCEPTION 'a commercial document event must continue the status of the previous event';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'commercial document events are append-only';
END;
$$;
