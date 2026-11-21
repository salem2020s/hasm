#!/usr/bin/env bash
# Stops the disposable test cluster and DELETES its data, logs and secrets. Refuses unless the root carries the marker file written by new-test-cluster.sh,
# so a wrongly set HASM_TEST_ROOT can never make this script delete an arbitrary directory.
. "$(dirname "${BASH_SOURCE[0]}")/common.sh"
assert_safe_locations
[[ -f "${MARKER_FILE}" ]] || die "No disposable test cluster marker in ${TEST_ROOT}; refusing to delete anything."
if [[ -f "${DATA_DIR}/PG_VERSION" ]] && cluster_running; then
  as_pg "${PG_BIN}/pg_ctl" -D "${DATA_DIR}" -m immediate -w -t 60 stop >/dev/null || true
fi
cluster_running && die "The cluster is still running; not deleting."
port_listening && die "Something still listens on ${TEST_HOST}:${TEST_PORT}; not deleting."
for d in "${DATA_DIR}" "${LOG_DIR}" "${SECRETS_DIR}" "${TEST_ROOT}/trx"; do
  [[ "$(readlink -m "${d}")" == "$(readlink -m "${TEST_ROOT}")"/* ]] || die "Refusing to delete ${d}: outside the test root."
  if [[ -e "${d}" ]]; then if [[ -n "${PG_RUN_AS}" ]]; then as_pg rm -rf "${d}" 2>/dev/null || rm -rf "${d}"; else rm -rf "${d}"; fi; fi
done
rm -f "${MARKER_FILE}" "${TEST_ROOT}/.run.lock" "${TEST_ROOT}/.run.holder"
rmdir "${TEST_ROOT}" 2>/dev/null || true
echo "Test cluster destroyed: data, logs and secrets under ${TEST_ROOT} removed."
