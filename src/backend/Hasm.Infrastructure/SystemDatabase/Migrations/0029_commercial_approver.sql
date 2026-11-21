-- Migration 0029: the Commercial Approver preset role (Phase 3.13 slice S1b). company.commercialdocs.approve (which also covers rejection) is NOT in the Company Administrator preset (separation of duties, like the journal and
-- import approvers): it belongs to this delegated preset, which the administrative tool grants offline because a company administrator cannot grant a permission they do not hold. The independent-approver rule
-- (never the creator, the last editor or the submitter) applies to every document in any case. Preset roles are immutable through the application, so the immutability triggers are paused for exactly this versioned change,
-- inside this transaction, and re-enabled before it ends.
ALTER TABLE t_359332ac DISABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 DISABLE TRIGGER r_2601e368;

INSERT INTO t_359332ac (c_59b19fa5, c_0f3f541d, c_65735af7, c_bb4f89f0, c_64bdf264, c_9f7e535b, c_c093e1d1, c_1dd9dad5) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000007', NULL, 'commercial-approver', 'Commercial Approver', true, now(), NULL, now());

INSERT INTO t_3e3d4119 (c_0258a1a8, c_27be2f9e) VALUES
    ('5c1b0f6e-0000-4a00-8000-000000000007', 'company.commercialdocs.view'),
    ('5c1b0f6e-0000-4a00-8000-000000000007', 'company.commercialdocs.approve');

ALTER TABLE t_359332ac ENABLE TRIGGER r_d3408f58;
ALTER TABLE t_3e3d4119 ENABLE TRIGGER r_2601e368;
