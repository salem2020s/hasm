#!/usr/bin/env node
// Deterministic generator for company migration 0040 (Phase 3.14 slice S2a: cash vouchers WITHOUT any financial posting - header, append-only line sets and the append-only event history).
// SINGLE SOURCE: the SQL template below, written with LOGICAL tokens such as {{CashVoucher.Kind}}. Every opaque physical name (D-029) is derived as
//   prefix + first 8 hex digits of SHA-256("hasm.company.<MIGRATION>.cash-vouchers.v1:" + token)
// (same rules as tools/schema/company-0035-cash-bank.cjs: a collision aborts the run; names are fixed once committed). References to names that already exist are written [[Entity.Name]] and resolved from the committed generated dictionaries, never re-derived.
// Usage:  node tools/schema/company-0040-cash-vouchers.cjs --write | --check
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const root = path.resolve(__dirname, "..", "..");
const infra = path.join(root, "src", "backend", "Hasm.Infrastructure");
const MIGRATION = "0040";   // the company migration number (by merge order: main holds 0001-0039)
const outSql = path.join(infra, "CompanyDatabase", "Migrations", `${MIGRATION}_company_cash_vouchers.sql`);
const outCs = path.join(infra, "CompanyDatabase", "CompanySchema.CashVouchers.cs");

function parseDictionary(file) {
  const text = fs.readFileSync(path.join(infra, "CompanyDatabase", file), "utf8");
  const map = new Map();
  for (const m of text.matchAll(/public static class (\w+)\s*\{([\s\S]*?)\n    \}/g))
    for (const c of m[2].matchAll(/public const string (\w+) = "([tcfkr]_[0-9a-f]{8})";/g)) map.set(`${m[1]}.${c[1]}`, c[2]);
  return map;
}
const EXISTING = new Map([...parseDictionary("CompanySchema.Accounts.cs"), ...parseDictionary("CompanySchema.Branches.cs"), ...parseDictionary("CompanySchema.Configuration.cs"), ...parseDictionary("CompanySchema.CashBank.cs"),
  ...parseDictionary("CompanySchema.Parties.cs"), ...parseDictionary("CompanySchema.PartyRoles.cs")]);
const ext = (key) => { const v = EXISTING.get(key); if (!v) throw new Error("unknown existing name " + key); return v; };

const TEMPLATE = `-- Migration ${MIGRATION}: cash vouchers WITHOUT any financial posting (Phase 3.14 slice S2a; decisions D-156, D-211, D-214). Forward-only and ADDITIVE: three new tables with their guards and one new guard trigger on the cash account
-- master; no existing table, column, constraint or function is altered, nothing is seeded, and NOTHING here touches the ledger, the numbering or any balance.
-- Opaque physical names (D-029) generated deterministically by tools/schema/company-${MIGRATION}-cash-vouchers.cjs (logical dictionary: CompanySchema.CashVouchers.cs). Do not edit by hand: re-run the generator.
--
-- WHAT THIS IS (docs/PHASE-3.14.md sections 4 and 6; D-211 Q2, Q3, Q11): a receipt (kind 1) or payment (kind 2) voucher on ONE cash or bank account, in the branch and the (base) currency of that account, with an optional party
-- reference and counter lines (an explicitly chosen counter account and a positive amount). D-214 keeps financial posting for cash and bank CLOSED: this slice has no approval, no official number, no journal entry, no link to
-- one and no reversal. A voucher here is a draft, a submitted draft or a cancelled one; the columns and the status values of the later slice (S2b) are added by its own migration.
-- Invariants enforced by PostgreSQL:
--   * vouchers, lines and events are never removed; the identity of a voucher (kind, cash account, branch, currency, creator, idempotency key, payload hash) never changes; every change advances the revision by one;
--   * the status guard allows ONLY draft -> draft (an edit), draft -> submitted and draft/submitted -> cancelled (the state machine of the plan's section 6, without its approval half). Approval, rejection, every later
--     state AND submitted -> draft (a withdrawal is not in the approved state machine) are NOT in this guard: a direct statement attempting them is refused. From submitted the only move is the cancellation. A cancelled
--     voucher is final; a status change edits nothing; a submitted or cancelled voucher is not edited;
--   * a voucher is created, and submitted, only on an ACTIVE cash or bank account whose branch and currency are the voucher's, the currency being the base currency (Q2);
--   * lines are APPEND-ONLY line sets: every edit of a draft writes a whole new set under the new revision (the voucher names its current set); a set is closed once its revision has its history event; a line is written only
--     while the voucher is a draft; no line is ever updated or deleted, so the history of a draft's lines is kept;
--   * a counter account is an active, non-group account that admits the base currency, is NOT bound to any cash or bank account (that would be a transfer in disguise, Q7) and is NOT the control account of any party role (Q3);
--     checked when a line is written and again for every current line when the voucher is submitted; a submitted voucher has at least one line;
--   * the events are append-only, one per revision, each carrying the CURRENT revision and status of its voucher and STARTING at the status the previous event ended at (so a change cannot be recorded under the kind of
--     another one), a cancellation event carries a non-blank reason, and every revision of a voucher has its event when its transaction commits;
--   * (cash account master, D-211 Q1 and the plan's section 4) the branch and the bound general-ledger account of a cash or bank account are FROZEN once any voucher refers to it, and it is not deactivated while one of its
--     vouchers is not cancelled (in this slice every voucher that is not cancelled is open: nothing can be approved yet). The guard of the account row decides on what it READS of the voucher table, which is race-free
--     only at READ COMMITTED (the read follows the row lock): a change of a cash account at any other isolation level is refused.

CREATE TABLE {{CashVoucher}} (
    {{CashVoucher.Id}} uuid NOT NULL,
    {{CashVoucher.Kind}} smallint NOT NULL,
    {{CashVoucher.CashAccountId}} uuid NOT NULL,
    {{CashVoucher.BranchId}} uuid NOT NULL,
    {{CashVoucher.Currency}} varchar(3) NOT NULL,
    {{CashVoucher.VoucherDate}} date NOT NULL,
    {{CashVoucher.PartyId}} uuid NULL,
    {{CashVoucher.Reference}} varchar(100) NULL,
    {{CashVoucher.Note}} varchar(500) NULL,
    {{CashVoucher.Status}} smallint NOT NULL,
    {{CashVoucher.LinesRevision}} bigint NOT NULL,
    {{CashVoucher.IdempotencyKey}} varchar(64) NOT NULL,
    {{CashVoucher.PayloadHash}} char(64) NOT NULL,
    {{CashVoucher.SubmittedBy}} uuid NULL,
    {{CashVoucher.UpdatedBy}} uuid NOT NULL,   -- the last EDITOR of the content (a status change does not change it); the independent-approver rule of the later slice compares against it
    {{CashVoucher.Revision}} bigint NOT NULL DEFAULT 1,
    {{CashVoucher.CreatedBy}} uuid NOT NULL,
    {{CashVoucher.CreatedAtUtc}} timestamptz NOT NULL,
    {{CashVoucher.UpdatedAtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{CashVoucher.Pk}} PRIMARY KEY ({{CashVoucher.Id}}),
    CONSTRAINT {{CashVoucher.FkCashAccount}} FOREIGN KEY ({{CashVoucher.CashAccountId}}) REFERENCES [[CashAccount.Table]] ([[CashAccount.Id]]),
    CONSTRAINT {{CashVoucher.FkBranch}} FOREIGN KEY ({{CashVoucher.BranchId}}) REFERENCES [[Branch.Table]] ([[Branch.Id]]),
    CONSTRAINT {{CashVoucher.FkParty}} FOREIGN KEY ({{CashVoucher.PartyId}}) REFERENCES [[LocalParty.Table]] ([[LocalParty.Id]]),
    -- the value ranges are deliberately wider than this slice uses (a constraint is never dropped): the guard function below, which a later migration replaces, admits only what the slice implements
    CONSTRAINT {{CashVoucher.CkValues}} CHECK ({{CashVoucher.Revision}} > 0 AND {{CashVoucher.LinesRevision}} > 0 AND {{CashVoucher.LinesRevision}} <= {{CashVoucher.Revision}} AND {{CashVoucher.Kind}} IN (1, 2)
        AND {{CashVoucher.Status}} BETWEEN 1 AND 9 AND {{CashVoucher.Currency}} ~ '^[A-Z]{3}$' AND {{CashVoucher.PayloadHash}} ~ '^[0-9a-f]{64}$' AND char_length({{CashVoucher.IdempotencyKey}}) BETWEEN 8 AND 64
        AND ({{CashVoucher.Reference}} IS NULL OR char_length(btrim({{CashVoucher.Reference}})) >= 1) AND ({{CashVoucher.Note}} IS NULL OR char_length(btrim({{CashVoucher.Note}})) >= 1))
);
CREATE UNIQUE INDEX {{CashVoucher.UqIdempotency}} ON {{CashVoucher}} ({{CashVoucher.CreatedBy}}, {{CashVoucher.IdempotencyKey}});
CREATE INDEX {{CashVoucher.IxBranch}} ON {{CashVoucher}} ({{CashVoucher.BranchId}}, {{CashVoucher.CreatedAtUtc}});
CREATE INDEX {{CashVoucher.IxCashAccount}} ON {{CashVoucher}} ({{CashVoucher.CashAccountId}}, {{CashVoucher.Status}});

CREATE TABLE {{CashVoucherLine}} (
    {{CashVoucherLine.Id}} uuid NOT NULL,
    {{CashVoucherLine.VoucherId}} uuid NOT NULL,
    {{CashVoucherLine.Revision}} bigint NOT NULL,   -- the revision of the voucher that wrote this line set
    {{CashVoucherLine.LineNo}} integer NOT NULL,
    {{CashVoucherLine.AccountId}} uuid NOT NULL,
    {{CashVoucherLine.Amount}} numeric(20,4) NOT NULL,
    {{CashVoucherLine.Description}} varchar(500) NULL,
    CONSTRAINT {{CashVoucherLine.Pk}} PRIMARY KEY ({{CashVoucherLine.Id}}),
    CONSTRAINT {{CashVoucherLine.FkVoucher}} FOREIGN KEY ({{CashVoucherLine.VoucherId}}) REFERENCES {{CashVoucher}} ({{CashVoucher.Id}}),
    CONSTRAINT {{CashVoucherLine.FkAccount}} FOREIGN KEY ({{CashVoucherLine.AccountId}}) REFERENCES [[Account.Table]] ([[Account.Id]]),
    CONSTRAINT {{CashVoucherLine.CkValues}} CHECK ({{CashVoucherLine.Revision}} > 0 AND {{CashVoucherLine.LineNo}} BETWEEN 1 AND 499 AND {{CashVoucherLine.Amount}} > 0 AND {{CashVoucherLine.Amount}} <= 999999999999999
        AND ({{CashVoucherLine.Description}} IS NULL OR char_length(btrim({{CashVoucherLine.Description}})) >= 1))
);
CREATE UNIQUE INDEX {{CashVoucherLine.UqLine}} ON {{CashVoucherLine}} ({{CashVoucherLine.VoucherId}}, {{CashVoucherLine.Revision}}, {{CashVoucherLine.LineNo}});
CREATE INDEX {{CashVoucherLine.IxAccount}} ON {{CashVoucherLine}} ({{CashVoucherLine.AccountId}});

CREATE TABLE {{CashVoucherEvent}} (
    {{CashVoucherEvent.Id}} uuid NOT NULL,
    {{CashVoucherEvent.VoucherId}} uuid NOT NULL,
    {{CashVoucherEvent.Kind}} smallint NOT NULL,
    {{CashVoucherEvent.FromStatus}} smallint NULL,
    {{CashVoucherEvent.ToStatus}} smallint NOT NULL,
    {{CashVoucherEvent.Revision}} bigint NOT NULL,
    {{CashVoucherEvent.ActorUserId}} uuid NOT NULL,
    {{CashVoucherEvent.Reason}} varchar(500) NULL,
    {{CashVoucherEvent.AtUtc}} timestamptz NOT NULL,
    CONSTRAINT {{CashVoucherEvent.Pk}} PRIMARY KEY ({{CashVoucherEvent.Id}}),
    CONSTRAINT {{CashVoucherEvent.FkVoucher}} FOREIGN KEY ({{CashVoucherEvent.VoucherId}}) REFERENCES {{CashVoucher}} ({{CashVoucher.Id}}),
    CONSTRAINT {{CashVoucherEvent.CkValues}} CHECK ({{CashVoucherEvent.Kind}} BETWEEN 1 AND 30 AND {{CashVoucherEvent.ToStatus}} BETWEEN 1 AND 9 AND ({{CashVoucherEvent.FromStatus}} IS NULL OR {{CashVoucherEvent.FromStatus}} BETWEEN 1 AND 9) AND {{CashVoucherEvent.Revision}} > 0)
);
CREATE UNIQUE INDEX {{CashVoucherEvent.UqRevision}} ON {{CashVoucherEvent}} ({{CashVoucherEvent.VoucherId}}, {{CashVoucherEvent.Revision}});

-- the account rule of a voucher: an ACTIVE cash or bank account of the voucher's branch, in the base currency. The share lock conflicts with every change of the account row, so a voucher is never written beside a
-- deactivation or a re-binding of its account (the account's own guard below then sees the committed voucher).
CREATE FUNCTION {{CashVoucher.FnAccount}}(p_account uuid, p_branch uuid, p_currency varchar) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    a record;
    v_base varchar(3);
BEGIN
    SELECT cc.[[CompanyCurrency.Code]] INTO v_base FROM [[CompanyCurrency.Table]] cc WHERE cc.[[CompanyCurrency.IsBase]] AND cc.[[CompanyCurrency.Enabled]] LIMIT 1;
    IF v_base IS NULL THEN RAISE EXCEPTION 'cash_base_currency_not_selected'; END IF;
    SELECT x.[[CashAccount.IsActive]] AS is_active, x.[[CashAccount.BranchId]] AS branch, x.[[CashAccount.Currency]] AS ccy INTO a FROM [[CashAccount.Table]] x WHERE x.[[CashAccount.Id]] = p_account FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'cash_voucher_account_not_found'; END IF;
    IF NOT a.is_active THEN RAISE EXCEPTION 'cash_voucher_account_inactive'; END IF;
    IF a.branch IS DISTINCT FROM p_branch THEN RAISE EXCEPTION 'cash_voucher_branch_mismatch'; END IF;
    IF a.ccy IS DISTINCT FROM v_base OR p_currency IS DISTINCT FROM v_base THEN RAISE EXCEPTION 'cash_voucher_currency_not_base'; END IF;
END;
$$;

-- the counter-account rule (docs/PHASE-3.14.md section 6, fail closed): postable and active, admits the base currency, not bound to a cash or bank account, not the control account of any party role
CREATE FUNCTION {{CashVoucherLine.FnCounter}}(p_account uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    a record;
    v_base varchar(3);
BEGIN
    SELECT cc.[[CompanyCurrency.Code]] INTO v_base FROM [[CompanyCurrency.Table]] cc WHERE cc.[[CompanyCurrency.IsBase]] AND cc.[[CompanyCurrency.Enabled]] LIMIT 1;
    IF v_base IS NULL THEN RAISE EXCEPTION 'cash_base_currency_not_selected'; END IF;
    SELECT x.[[Account.IsGroup]] AS is_group, x.[[Account.IsActive]] AS is_active, x.[[Account.CurrencyPolicy]] AS policy, x.[[Account.CurrencyCode]] AS ccy INTO a FROM [[Account.Table]] x WHERE x.[[Account.Id]] = p_account;
    IF NOT FOUND THEN RAISE EXCEPTION 'cash_voucher_counter_not_found'; END IF;
    IF a.is_group OR NOT a.is_active THEN RAISE EXCEPTION 'cash_voucher_counter_not_postable'; END IF;
    IF NOT (a.policy IN (1, 2) OR (a.policy = 3 AND a.ccy = v_base)) THEN RAISE EXCEPTION 'cash_voucher_counter_currency'; END IF;
    IF EXISTS (SELECT 1 FROM [[CashAccount.Table]] c WHERE c.[[CashAccount.AccountId]] = p_account) THEN RAISE EXCEPTION 'cash_voucher_counter_is_cash_account'; END IF;
    IF EXISTS (SELECT 1 FROM [[PartyRole.Table]] r WHERE r.[[PartyRole.ControlAccountId]] = p_account) THEN RAISE EXCEPTION 'cash_voucher_counter_is_control_account'; END IF;
END;
$$;

CREATE FUNCTION {{CashVoucher.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    l record;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.{{CashVoucher.Status}} <> 1 THEN RAISE EXCEPTION 'a cash voucher is created as a draft'; END IF;
        IF NEW.{{CashVoucher.Revision}} <> 1 OR NEW.{{CashVoucher.LinesRevision}} <> 1 THEN RAISE EXCEPTION 'a cash voucher is created at revision 1'; END IF;
        IF NEW.{{CashVoucher.SubmittedBy}} IS NOT NULL THEN RAISE EXCEPTION 'a draft has no submitter'; END IF;
        IF NEW.{{CashVoucher.UpdatedBy}} IS DISTINCT FROM NEW.{{CashVoucher.CreatedBy}} THEN RAISE EXCEPTION 'the creator is the first editor of a cash voucher'; END IF;
        PERFORM {{CashVoucher.FnAccount}}(NEW.{{CashVoucher.CashAccountId}}, NEW.{{CashVoucher.BranchId}}, NEW.{{CashVoucher.Currency}});
        RETURN NEW;
    END IF;
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'cash vouchers are never removed'; END IF;
    IF NEW.{{CashVoucher.Id}} IS DISTINCT FROM OLD.{{CashVoucher.Id}} OR NEW.{{CashVoucher.Kind}} IS DISTINCT FROM OLD.{{CashVoucher.Kind}} OR NEW.{{CashVoucher.CashAccountId}} IS DISTINCT FROM OLD.{{CashVoucher.CashAccountId}}
       OR NEW.{{CashVoucher.BranchId}} IS DISTINCT FROM OLD.{{CashVoucher.BranchId}} OR NEW.{{CashVoucher.Currency}} IS DISTINCT FROM OLD.{{CashVoucher.Currency}} OR NEW.{{CashVoucher.CreatedBy}} IS DISTINCT FROM OLD.{{CashVoucher.CreatedBy}}
       OR NEW.{{CashVoucher.CreatedAtUtc}} IS DISTINCT FROM OLD.{{CashVoucher.CreatedAtUtc}} OR NEW.{{CashVoucher.IdempotencyKey}} IS DISTINCT FROM OLD.{{CashVoucher.IdempotencyKey}}
       OR NEW.{{CashVoucher.PayloadHash}} IS DISTINCT FROM OLD.{{CashVoucher.PayloadHash}} THEN
        RAISE EXCEPTION 'the identity of a cash voucher is immutable';
    END IF;
    IF NEW.{{CashVoucher.Revision}} <> OLD.{{CashVoucher.Revision}} + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    IF NOT ((OLD.{{CashVoucher.Status}} = 1 AND NEW.{{CashVoucher.Status}} IN (1, 2, 5)) OR (OLD.{{CashVoucher.Status}} = 2 AND NEW.{{CashVoucher.Status}} = 5)) THEN
        RAISE EXCEPTION 'illegal cash voucher status transition';
    END IF;
    IF NEW.{{CashVoucher.Status}} <> OLD.{{CashVoucher.Status}} THEN
        IF NEW.{{CashVoucher.VoucherDate}} IS DISTINCT FROM OLD.{{CashVoucher.VoucherDate}} OR NEW.{{CashVoucher.PartyId}} IS DISTINCT FROM OLD.{{CashVoucher.PartyId}} OR NEW.{{CashVoucher.Reference}} IS DISTINCT FROM OLD.{{CashVoucher.Reference}}
           OR NEW.{{CashVoucher.Note}} IS DISTINCT FROM OLD.{{CashVoucher.Note}} OR NEW.{{CashVoucher.LinesRevision}} IS DISTINCT FROM OLD.{{CashVoucher.LinesRevision}} OR NEW.{{CashVoucher.UpdatedBy}} IS DISTINCT FROM OLD.{{CashVoucher.UpdatedBy}} THEN
            RAISE EXCEPTION 'a status change does not edit the cash voucher';
        END IF;
    ELSIF NEW.{{CashVoucher.LinesRevision}} <> NEW.{{CashVoucher.Revision}} THEN
        RAISE EXCEPTION 'an edit of a draft writes a new line set under the new revision';
    END IF;
    IF NEW.{{CashVoucher.Status}} = 2 AND NEW.{{CashVoucher.SubmittedBy}} IS NULL THEN RAISE EXCEPTION 'a submitted cash voucher names its submitter'; END IF;
    IF NEW.{{CashVoucher.Status}} = 1 AND NEW.{{CashVoucher.SubmittedBy}} IS NOT NULL THEN RAISE EXCEPTION 'a draft has no submitter'; END IF;
    IF NEW.{{CashVoucher.Status}} = 5 AND NEW.{{CashVoucher.SubmittedBy}} IS DISTINCT FROM OLD.{{CashVoucher.SubmittedBy}} THEN RAISE EXCEPTION 'cancellation keeps the submitter record'; END IF;
    IF NEW.{{CashVoucher.Status}} = 2 THEN
        -- submission re-checks everything a draft was checked for: the account may have been deactivated, or a counter account bound, made a control account or deactivated, since the lines were written
        PERFORM {{CashVoucher.FnAccount}}(NEW.{{CashVoucher.CashAccountId}}, NEW.{{CashVoucher.BranchId}}, NEW.{{CashVoucher.Currency}});
        IF NOT EXISTS (SELECT 1 FROM {{CashVoucherLine}} x WHERE x.{{CashVoucherLine.VoucherId}} = NEW.{{CashVoucher.Id}} AND x.{{CashVoucherLine.Revision}} = NEW.{{CashVoucher.LinesRevision}}) THEN
            RAISE EXCEPTION 'cash_voucher_lines_required';
        END IF;
        FOR l IN SELECT x.{{CashVoucherLine.AccountId}} AS account FROM {{CashVoucherLine}} x WHERE x.{{CashVoucherLine.VoucherId}} = NEW.{{CashVoucher.Id}} AND x.{{CashVoucherLine.Revision}} = NEW.{{CashVoucher.LinesRevision}} LOOP
            PERFORM {{CashVoucherLine.FnCounter}}(l.account);
        END LOOP;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CashVoucher.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{CashVoucher}} FOR EACH ROW EXECUTE FUNCTION {{CashVoucher.FnGuard}}();
CREATE TRIGGER {{CashVoucher.TrTruncate}} BEFORE TRUNCATE ON {{CashVoucher}} FOR EACH STATEMENT EXECUTE FUNCTION {{CashVoucher.FnGuard}}();

-- every revision of a voucher has its history event when the transaction commits (the event is written after the change, so the check is deferred)
CREATE FUNCTION {{CashVoucher.FnHistory}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM {{CashVoucherEvent}} e WHERE e.{{CashVoucherEvent.VoucherId}} = NEW.{{CashVoucher.Id}} AND e.{{CashVoucherEvent.Revision}} = NEW.{{CashVoucher.Revision}}) THEN
        RAISE EXCEPTION 'every revision of a cash voucher has its history event';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER {{CashVoucher.TrHistory}} AFTER INSERT OR UPDATE ON {{CashVoucher}} DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {{CashVoucher.FnHistory}}();
REVOKE ALL ON TABLE {{CashVoucher}} FROM PUBLIC;

CREATE FUNCTION {{CashVoucherLine.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v record;
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'cash voucher lines are append-only'; END IF;
    -- a share lock conflicts with a concurrent status UPDATE of the voucher: a line can never commit beside a submit or a cancel
    SELECT d.{{CashVoucher.Status}} AS status, d.{{CashVoucher.Revision}} AS revision, d.{{CashVoucher.LinesRevision}} AS lines_revision INTO v FROM {{CashVoucher}} d WHERE d.{{CashVoucher.Id}} = NEW.{{CashVoucherLine.VoucherId}} FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'a cash voucher line belongs to a voucher'; END IF;
    IF v.status <> 1 THEN RAISE EXCEPTION 'the lines of a cash voucher are written only while it is a draft'; END IF;
    IF NEW.{{CashVoucherLine.Revision}} <> v.revision OR NEW.{{CashVoucherLine.Revision}} <> v.lines_revision THEN RAISE EXCEPTION 'a line set carries the current revision of its cash voucher'; END IF;
    IF EXISTS (SELECT 1 FROM {{CashVoucherEvent}} e WHERE e.{{CashVoucherEvent.VoucherId}} = NEW.{{CashVoucherLine.VoucherId}} AND e.{{CashVoucherEvent.Revision}} = NEW.{{CashVoucherLine.Revision}}) THEN
        RAISE EXCEPTION 'the line set of a recorded revision is closed';
    END IF;
    PERFORM {{CashVoucherLine.FnCounter}}(NEW.{{CashVoucherLine.AccountId}});
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CashVoucherLine.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{CashVoucherLine}} FOR EACH ROW EXECUTE FUNCTION {{CashVoucherLine.FnGuard}}();
CREATE TRIGGER {{CashVoucherLine.TrTruncate}} BEFORE TRUNCATE ON {{CashVoucherLine}} FOR EACH STATEMENT EXECUTE FUNCTION {{CashVoucherLine.FnGuard}}();
REVOKE ALL ON TABLE {{CashVoucherLine}} FROM PUBLIC;

CREATE FUNCTION {{CashVoucherEvent.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'cash voucher events are append-only'; END IF;
    -- the events of this slice: 1 created, 2 updated, 3 submitted, 5 cancelled, each with the statuses it connects (kind 4 is not used: there is no withdrawal)
    IF NOT ((NEW.{{CashVoucherEvent.Kind}} = 1 AND NEW.{{CashVoucherEvent.FromStatus}} IS NULL AND NEW.{{CashVoucherEvent.ToStatus}} = 1)
         OR (NEW.{{CashVoucherEvent.Kind}} = 2 AND NEW.{{CashVoucherEvent.FromStatus}} = 1 AND NEW.{{CashVoucherEvent.ToStatus}} = 1)
         OR (NEW.{{CashVoucherEvent.Kind}} = 3 AND NEW.{{CashVoucherEvent.FromStatus}} = 1 AND NEW.{{CashVoucherEvent.ToStatus}} = 2)
         OR (NEW.{{CashVoucherEvent.Kind}} = 5 AND NEW.{{CashVoucherEvent.FromStatus}} IN (1, 2) AND NEW.{{CashVoucherEvent.ToStatus}} = 5)) THEN
        RAISE EXCEPTION 'unknown cash voucher event';
    END IF;
    IF (NEW.{{CashVoucherEvent.Kind}} = 1) <> (NEW.{{CashVoucherEvent.Revision}} = 1) THEN RAISE EXCEPTION 'the first event of a cash voucher is its creation'; END IF;
    -- an event describes what ALREADY happened to its voucher in this transaction: its revision and resulting status must be the voucher's current ones (no forged or future event)
    IF NOT EXISTS (SELECT 1 FROM {{CashVoucher}} d WHERE d.{{CashVoucher.Id}} = NEW.{{CashVoucherEvent.VoucherId}} AND d.{{CashVoucher.Revision}} = NEW.{{CashVoucherEvent.Revision}} AND d.{{CashVoucher.Status}} = NEW.{{CashVoucherEvent.ToStatus}}) THEN
        RAISE EXCEPTION 'a cash voucher event must carry the current revision and status of its voucher';
    END IF;
    -- ... and it starts where the previous event ended (events are append-only and one per revision, so that row is fixed): with the pairs above, the kind of an event is thereby the change that really happened
    IF NEW.{{CashVoucherEvent.Revision}} > 1 AND NOT EXISTS (SELECT 1 FROM {{CashVoucherEvent}} p WHERE p.{{CashVoucherEvent.VoucherId}} = NEW.{{CashVoucherEvent.VoucherId}} AND p.{{CashVoucherEvent.Revision}} = NEW.{{CashVoucherEvent.Revision}} - 1
        AND p.{{CashVoucherEvent.ToStatus}} = NEW.{{CashVoucherEvent.FromStatus}}) THEN
        RAISE EXCEPTION 'a cash voucher event starts at the status its voucher had before the change';
    END IF;
    -- a cancellation carries its reason (not null, not empty, not only white space)
    IF NEW.{{CashVoucherEvent.Kind}} = 5 AND (NEW.{{CashVoucherEvent.Reason}} IS NULL OR NEW.{{CashVoucherEvent.Reason}} !~ '[^[:space:]]') THEN RAISE EXCEPTION 'a cancellation event carries its reason'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CashVoucherEvent.TrRow}} BEFORE INSERT OR UPDATE OR DELETE ON {{CashVoucherEvent}} FOR EACH ROW EXECUTE FUNCTION {{CashVoucherEvent.FnGuard}}();
CREATE TRIGGER {{CashVoucherEvent.TrTruncate}} BEFORE TRUNCATE ON {{CashVoucherEvent}} FOR EACH STATEMENT EXECUTE FUNCTION {{CashVoucherEvent.FnGuard}}();
REVOKE ALL ON TABLE {{CashVoucherEvent}} FROM PUBLIC;

-- the cash account master, now that a document can refer to it (docs/PHASE-3.14.md section 4): the branch and the bound ledger account are frozen once ANY voucher refers to the account (a different binding is a new cash or
-- bank account), and the account is not deactivated while one of its vouchers is open. In this slice a voucher is open unless it is cancelled (nothing can be approved yet); the slice that adds approval replaces this function.
-- The row is already locked when this BEFORE trigger runs, and a voucher takes a share lock on it, so at READ COMMITTED neither rule can be raced: the reads below follow the lock and see every committed voucher. At
-- REPEATABLE READ or SERIALIZABLE they would use the snapshot of the transaction, which can predate a committed voucher (a share lock changes nothing in the row, so no serialization error is raised): refused first.
CREATE FUNCTION {{CashVoucherAccountGuard.FnGuard}}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN RAISE EXCEPTION 'cash accounts are changed at READ COMMITTED only'; END IF;
    IF (NEW.[[CashAccount.BranchId]] IS DISTINCT FROM OLD.[[CashAccount.BranchId]] OR NEW.[[CashAccount.AccountId]] IS DISTINCT FROM OLD.[[CashAccount.AccountId]])
       AND EXISTS (SELECT 1 FROM {{CashVoucher}} v WHERE v.{{CashVoucher.CashAccountId}} = OLD.[[CashAccount.Id]]) THEN
        RAISE EXCEPTION 'cash_account_binding_frozen';
    END IF;
    IF OLD.[[CashAccount.IsActive]] AND NOT NEW.[[CashAccount.IsActive]]
       AND EXISTS (SELECT 1 FROM {{CashVoucher}} v WHERE v.{{CashVoucher.CashAccountId}} = OLD.[[CashAccount.Id]] AND v.{{CashVoucher.Status}} <> 5) THEN
        RAISE EXCEPTION 'cash_account_has_open_documents';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER {{CashVoucherAccountGuard.TrRow}} BEFORE UPDATE ON [[CashAccount.Table]] FOR EACH ROW EXECUTE FUNCTION {{CashVoucherAccountGuard.FnGuard}}();
`;

function prefixOf(token) {
  const last = token.split(".").pop();
  if (!token.includes(".")) return "t_";
  if (/^Fn[A-Z]/.test(last)) return "f_";
  if (/^Tr[A-Z]/.test(last)) return "r_";
  if (/^(Pk|Uq|Fk|Ck|Ix)([A-Z]|$)/.test(last)) return "k_";
  return "c_";
}
const physical = (token) => prefixOf(token) + crypto.createHash("sha256").update("hasm.company." + MIGRATION + ".cash-vouchers.v1:" + token).digest("hex").slice(0, 8);

function existingNames() {
  const names = new Set();
  const scan = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { if (e.name !== "bin" && e.name !== "obj") scan(p); continue; }
      if (p === outSql || p === outCs) continue;
      if (!/\.(sql|cs)$/.test(e.name)) continue;
      for (const m of fs.readFileSync(p, "utf8").matchAll(/\b[tcfkr]_[0-9a-f]{8}\b/g)) names.add(m[0]);
    }
  };
  scan(path.join(infra, "CompanyDatabase")); scan(path.join(infra, "SystemDatabase"));
  return names;
}

function generate() {
  const body = TEMPLATE.replace(/\[\[([A-Za-z0-9.]+)\]\]/g, (_, k) => ext(k));
  const tokens = [...new Set([...body.matchAll(/\{\{([A-Za-z0-9.]+)\}\}/g)].map((m) => m[1]))];
  const taken = existingNames();
  const byToken = new Map(), seen = new Map();
  for (const t of tokens) {
    const name = physical(t);
    if (taken.has(name)) throw new Error(`collision with an existing name: ${t} -> ${name}`);
    if (seen.has(name)) throw new Error(`collision inside the migration: ${t} and ${seen.get(name)} -> ${name}`);
    seen.set(name, t); byToken.set(t, name);
  }
  const sql = body.replace(/\{\{([A-Za-z0-9.]+)\}\}/g, (_, t) => byToken.get(t));
  const entities = [...new Set(tokens.map((t) => t.split(".")[0]))];
  let cs = "namespace Hasm.Infrastructure.CompanyDatabase;\n\n// GENERATED by tools/schema/company-" + MIGRATION + "-cash-vouchers.cjs for company migration " + MIGRATION + " (Phase 3.14 slice S2a, cash vouchers without posting). Do not edit: re-run the generator.\n" +
    "// Physical names are opaque (D-029), derived deterministically from the logical token, and fixed: changing one requires a new migration.\n// CashVoucher, CashVoucherLine and CashVoucherEvent name the tables with their columns, constraints, indexes, functions and triggers; CashVoucherGuard names the functions and the trigger this migration adds to the cash account master.\n" + "public static partial class CompanySchema\n{\n";
  for (const e of entities) {
    cs += `    public static class ${e}\n    {\n`;
    if (byToken.has(e)) cs += `        public const string Table = "${byToken.get(e)}";\n`;
    for (const t of tokens.filter((x) => x.startsWith(e + "."))) cs += `        public const string ${t.slice(e.length + 1)} = "${byToken.get(t)}";\n`;
    cs += "    }\n\n";
  }
  cs += "    private static IReadOnlyDictionary<string, string> CashVoucherMap { get; } = new Dictionary<string, string>\n    {\n";
  for (const t of tokens) cs += `        ["${t}"] = ${t.includes(".") ? t : t + ".Table"},\n`;
  cs += "    };\n}\n";
  return { sql, cs };
}

const { sql, cs } = generate();
const mode = process.argv[2];
if (mode === "--write") { fs.writeFileSync(outSql, sql); fs.writeFileSync(outCs, cs); console.log("written"); }
else if (mode === "--check") {
  const norm = (s) => s.replace(/\r\n/g, "\n");
  const ok = fs.existsSync(outSql) && fs.existsSync(outCs) && norm(fs.readFileSync(outSql, "utf8")) === sql && norm(fs.readFileSync(outCs, "utf8")) === cs;
  if (!ok) { console.error("generated cash-voucher schema differs from the committed files"); process.exit(1); }
  console.log("cash-voucher schema files match the generator");
} else { console.error("usage: --write | --check"); process.exit(2); }
