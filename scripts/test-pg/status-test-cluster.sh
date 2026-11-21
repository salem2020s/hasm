#!/usr/bin/env bash
# Shows whether the disposable test cluster runs and what listens on its port.
. "$(dirname "${BASH_SOURCE[0]}")/common.sh"
assert_safe_locations
assert_cluster_present
as_pg "${PG_BIN}/pg_ctl" -D "${DATA_DIR}" status || true
if command -v ss >/dev/null 2>&1; then
  listening="$(ss -ltn "sport = :${TEST_PORT}" 2>/dev/null | awk 'NR>1 {print $4}' | paste -sd, -)"
  if [[ -n "${listening}" ]]; then echo "Listening on: ${listening}"; else echo "Nothing listening on port ${TEST_PORT}."; fi
fi
