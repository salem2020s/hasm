#!/usr/bin/env bash
# Stops the disposable test cluster (fast shutdown). Data and secrets stay.
. "$(dirname "${BASH_SOURCE[0]}")/common.sh"
assert_safe_locations
assert_cluster_present
take_lock
neutralize_stale_pidfile   # pg_ctl below must never signal a reused PID; a live server of this cluster is left for the normal stop
if ! cluster_running; then echo "Test cluster is not running."; exit 0; fi
as_pg "${PG_BIN}/pg_ctl" -D "${DATA_DIR}" -m fast -w -t 60 stop >/dev/null
cluster_running && die "Test cluster did not stop in time."
echo "Test cluster stopped."
