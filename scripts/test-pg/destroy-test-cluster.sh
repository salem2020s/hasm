#!/usr/bin/env bash
# Stops the disposable test cluster and DELETES its data, logs and secrets. Refuses unless the root carries the marker file written by new-test-cluster.sh,
# so a wrongly set HASM_TEST_ROOT can never make this script delete an arbitrary directory.
. "$(dirname "${BASH_SOURCE[0]}")/common.sh"
assert_safe_locations
[[ -f "${MARKER_FILE}" ]] || die "No disposable test cluster marker in ${TEST_ROOT}; refusing to delete anything."   # checked before the lock so a refusal leaves nothing behind
take_lock          # refuses while a run (or any other script) holds the cluster; the lock file is a sibling of the root and is never deleted
[[ -f "${MARKER_FILE}" ]] || die "No disposable test cluster marker in ${TEST_ROOT}; refusing to delete anything."
[[ -O "${TEST_ROOT}" ]] || die "${TEST_ROOT} is not owned by the current user; refusing to delete."
if [[ -f "${DATA_DIR}/PG_VERSION" ]] && cluster_running; then
  n="$(foreign_sessions)"
  [[ "${n}" == "0" ]] || die "${n} test session(s) are connected to the cluster (a test run outside the lock?); not deleting."
  as_pg "${PG_BIN}/pg_ctl" -D "${DATA_DIR}" -m immediate -w -t 60 stop >/dev/null || true
fi
cluster_running && die "The cluster is still running; not deleting."
port_listening && die "Something still listens on ${TEST_HOST}:${TEST_PORT}; not deleting."
for d in "${DATA_DIR}" "${LOG_DIR}" "${SECRETS_DIR}" "${TEST_ROOT}/trx"; do
  [[ "$(readlink -m "${d}")" == "$(readlink -m "${TEST_ROOT}")"/* ]] || die "Refusing to delete ${d}: outside the test root."
  if [[ -e "${d}" ]]; then if [[ -n "${PG_RUN_AS}" ]]; then as_pg rm -rf "${d}" 2>/dev/null || rm -rf "${d}"; else rm -rf "${d}"; fi; fi
done
rm -f "${HOLDER_FILE}" "${TEST_ROOT}/.run.lock" "${MARKER_FILE}"   # .run.lock: leftover of the first harness version (the lock now lives beside the root and is never deleted)
rmdir "${TEST_ROOT}" 2>/dev/null || true
echo "Test cluster destroyed: data, logs and secrets under ${TEST_ROOT} removed."
