-- Migration 0002: provisioning attempt tracking (system database).
-- Adds the attempt counter and a short sanitized failure code (never an error message).
ALTER TABLE t_f446f4d0 ADD COLUMN c_bf5cb42b integer NOT NULL DEFAULT 0;
ALTER TABLE t_f446f4d0 ADD COLUMN c_e6369967 varchar(64) NULL;
