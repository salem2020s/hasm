#!/usr/bin/env bash
# Runs the PostgreSQL integration suite against the disposable test cluster with the SAME strictness as CI:
# HASM_REQUIRE_PG_TESTS=1 and HASM_REQUIRE_PG_ROLE_TESTS=1 (a closed gate fails instead of skipping), then requires 0 failed and 0 skipped/not executed from the TRX counters.
# Usage: scripts/test-pg/run-pg-tests.sh [--build] [extra dotnet test arguments, e.g. --filter "FullyQualifiedName~PricingApiTests"]
# Without --build the solution must already be built in Release (dotnet build src/backend/Hasm.slnx -c Release).
. "$(dirname "${BASH_SOURCE[0]}")/common.sh"
assert_safe_locations
assert_cluster_present
[[ -f "${ENV_FILE}" ]] || die "Missing ${ENV_FILE}; run new-test-cluster.sh."
# Two suites on one cluster interfere with each other (shared advisory locks, shared TRX path): refuse a second concurrent run.
command -v flock >/dev/null 2>&1 || die "flock is required."
HOLDER_FILE="${TEST_ROOT}/.run.holder"
exec 9>"${TEST_ROOT}/.run.lock"
if ! flock -n 9; then
  die "Another run-pg-tests.sh is already using this cluster; wait for it to finish. Holder: $(cat "${HOLDER_FILE}" 2>/dev/null || echo unknown)"
fi
# Single holder for the WHOLE run (the lock is released when this process exits): record who it is.
printf 'pid=%s worktree=%s started=%s\n' "$$" "${REPO_ROOT}" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "${HOLDER_FILE}"
trap 'rm -f "${HOLDER_FILE}"' EXIT
cluster_running || "${SCRIPT_DIR}/start-test-cluster.sh"

# The suites create and drop databases and roles cluster-wide: a second suite (another agent or shell) on the same cluster makes results meaningless.
# Refuse while any test login is connected; the superuser credential used for this read-only check lives in the owner-only secrets directory.
foreign_sessions() {
  PGPASSWORD="$(cat "${SUPER_FILE}")" "${PG_BIN}/psql" -h "${TEST_HOST}" -p "${TEST_PORT}" -U hasm_test_super -d postgres -X -q -At \
    -c "SELECT count(*) FROM pg_stat_activity WHERE usename IN ('hasm_test_admin','hasm_test_roleadmin') OR datname LIKE 'hasm_test\\_%' ESCAPE '\\'"
}
if [[ "${HASM_PG_ALLOW_SHARED:-}" != "1" ]]; then
  n="$(foreign_sessions)"
  [[ "${n}" == "0" ]] || die "${n} other session(s) are connected to the test cluster (another test run?). Wait for it to finish, or set HASM_PG_ALLOW_SHARED=1 to run anyway (results are then NOT trustworthy)."
fi

build=no
if [[ "${1:-}" == "--build" ]]; then build=yes; shift; fi
cd "${REPO_ROOT}"
[[ "${build}" == yes ]] && dotnet build src/backend/Hasm.slnx -c Release --nologo -v q

# shellcheck disable=SC1090
. "${ENV_FILE}"
export HASM_REQUIRE_PG_TESTS=1 HASM_REQUIRE_PG_ROLE_TESTS=1
trx_dir="${TEST_ROOT}/trx"
rm -rf "${trx_dir}"; install -d -m 700 "${trx_dir}"
start=$(date +%s)
set +e
dotnet test tests/integration/Hasm.Database.IntegrationTests --no-build -c Release --nologo \
  --logger "trx;LogFileName=pg-integration.trx" --results-directory "${trx_dir}" "$@"
code=$?
set -e
elapsed=$(( $(date +%s) - start ))

trx="${trx_dir}/pg-integration.trx"
[[ -f "${trx}" ]] || die "No TRX result file was produced (dotnet test exit code ${code})."
counters="$(grep -o '<Counters [^>]*>' "${trx}" | head -1)"
get() { sed -n "s/.*[ ]$1=\"\([0-9]*\)\".*/\1/p" <<<"${counters}"; }
total="$(get total)"; executed="$(get executed)"; passed="$(get passed)"; failed="$(get failed)"
echo "Post-run cluster activity check: $(foreign_sessions) test session(s) still connected (expected 0; a non-zero value means another process used the cluster)."
echo "PostgreSQL integration tests: total=${total} executed=${executed} passed=${passed} failed=${failed} duration=${elapsed}s (dotnet test exit code ${code})"
if [[ "${failed}" != "0" || "${executed}" != "${total}" || "${passed}" != "${total}" || "${code}" != "0" ]]; then
  die "Not clean: require 0 failed and 0 skipped/not executed with every test passed."
fi
echo "OK: ${passed}/${total} passed, 0 failed, 0 skipped."
