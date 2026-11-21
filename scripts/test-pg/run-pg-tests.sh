#!/usr/bin/env bash
# Runs the PostgreSQL integration suite against the disposable test cluster with the SAME strictness as CI:
# HASM_REQUIRE_PG_TESTS=1 and HASM_REQUIRE_PG_ROLE_TESTS=1 (a closed gate fails instead of skipping), then requires 0 failed and 0 skipped/not executed from the TRX counters.
# Usage: scripts/test-pg/run-pg-tests.sh [--build] [extra dotnet test arguments, e.g. --filter "FullyQualifiedName~PricingApiTests"]
# Without --build the solution must already be built in Release (dotnet build src/backend/Hasm.slnx -c Release).
# Any extra argument makes the run a SUBSET: it is reported as such (never as a verification of the suite) (run-oneshot.sh additionally exits 4 for a passing subset).
. "$(dirname "${BASH_SOURCE[0]}")/common.sh"
assert_safe_locations
assert_cluster_present
[[ -f "${ENV_FILE}" ]] || die "Missing ${ENV_FILE}; run new-test-cluster.sh."
# Two suites on one cluster interfere with each other (shared advisory locks, shared TRX path, cluster-wide roles): take the exclusive single-holder lock for the WHOLE run
# (released when this process exits; the lock file is never deleted) and record who holds it. destroy/stop/start/new refuse while it is held.
take_lock
printf 'pid=%s worktree=%s started=%s\n' "$$" "${REPO_ROOT}" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "${HOLDER_FILE}"
trap 'rm -f "${HOLDER_FILE}"' EXIT
cluster_running || "${SCRIPT_DIR}/start-test-cluster.sh"
# The suites create and drop databases and roles cluster-wide: refuse while any test login is connected (another suite run outside this script). There is no override:
# a run on a shared cluster is never trustworthy.
n="$(foreign_sessions)"
[[ "${n}" == "0" ]] || die "${n} other session(s) are connected to the test cluster (another test run?). Wait for it to finish."

build=no
if [[ "${1:-}" == "--build" ]]; then build=yes; shift; fi
subset=no; [[ $# -eq 0 ]] || subset=yes
cd "${REPO_ROOT}"
[[ "${build}" == yes ]] && dotnet build src/backend/Hasm.slnx -c Release --nologo -v q 9>&-    # fd 9 (the lock) is not inherited by build servers

# shellcheck disable=SC1090
. "${ENV_FILE}"
export HASM_REQUIRE_PG_TESTS=1 HASM_REQUIRE_PG_ROLE_TESTS=1
# Inherited CI-runner variables would make PostgresClusterIdentity accept the CI ports as well; this local path is port 5433 only.
unset GITHUB_ACTIONS GITHUB_RUN_ID RUNNER_ENVIRONMENT HASM_CI_PG_DIR
trx_dir="${TEST_ROOT}/trx"
rm -rf "${trx_dir}"; install -d -m 700 "${trx_dir}"
start=$(date +%s)
set +e
dotnet test tests/integration/Hasm.Database.IntegrationTests --no-build -c Release --nologo \
  --logger "trx;LogFileName=pg-integration.trx" --results-directory "${trx_dir}" "$@" 9>&-
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
if [[ "${subset}" == yes ]]; then
  echo "SUBSET PASSED: ${passed}/${total} passed, 0 failed, 0 skipped - extra dotnet test arguments were given, so this is NOT a verification of the full suite (no per-class registry check)."
else
  echo "OK: ${passed}/${total} passed, 0 failed, 0 skipped."
fi
