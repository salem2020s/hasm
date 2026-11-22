#!/usr/bin/env bash
# Self-test for the stale postmaster.pid guard of destroy-test-cluster.sh (no PostgreSQL server is started; nothing outside its own temporary directory is touched).
# It builds a fake stopped cluster root whose postmaster.pid records the PID of a LIVE decoy process of the server account (a reused PID), runs destroy-test-cluster.sh on it
# and requires: the decoy is still alive (nothing signalled it) and the root is gone. Without the guard pg_ctl takes the decoy for a running server and the root can never be
# destroyed. Port 5433 must be free (destroy-test-cluster.sh refuses to delete while anything listens there). Usage: scripts/test-pg/test-destroy-stale-pid.sh
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
die() { echo "FAIL: $*" >&2; exit 1; }
[[ -z "${HASM_TEST_ROOT:-}" ]] || die "unset HASM_TEST_ROOT first (the test uses its own temporary root)."
ACCOUNT=""; [[ "$(id -u)" != "0" ]] || ACCOUNT="${HASM_PG_RUN_AS:-postgres}"
as_account() { if [[ -n "${ACCOUNT}" ]]; then runuser -u "${ACCOUNT}" -- "$@"; else "$@"; fi; }
T="$(mktemp -d /tmp/hasm-destroy-selftest-XXXXXX)"
if [[ -n "${ACCOUNT}" ]]; then chgrp "$(id -gn "${ACCOUNT}")" "${T}"; chmod 710 "${T}"; else chmod 700 "${T}"; fi
R="${T}/cluster"; decoy=""
cleanup() {
  [[ -n "${decoy}" ]] && kill "${decoy}" 2>/dev/null || true
  [[ "${T}" == /tmp/hasm-destroy-selftest-?????? ]] && rm -rf -- "${T}"
}
trap 'rc=$?; cleanup; exit "${rc}"' EXIT

mkdir -m 711 "${R}"; ( umask 077; : > "${R}/.hasm-disposable-test-cluster" )
if [[ -n "${ACCOUNT}" ]]; then install -d -o "${ACCOUNT}" -m 700 "${R}/pgdata"; else install -d -m 700 "${R}/pgdata"; fi
echo 16 | as_account sh -c 'cat > "$1"' sh "${R}/pgdata/PG_VERSION"
tag="600.$$"                                     # a duration that names this test's decoy, so it is found by its exact command line
as_account sleep "${tag}" 2>/dev/null & disown   # 2>/dev/null: runuser reports "Terminated" when the decoy is removed at the end
for _ in $(seq 1 50); do decoy="$(pgrep -f -x "sleep ${tag}" | head -1 || true)"; [[ -n "${decoy}" ]] && break; sleep 0.1; done
[[ -n "${decoy}" ]] || die "could not start the decoy."
printf '%s\n%s\n' "${decoy}" "${R}/pgdata" | as_account sh -c 'cat > "$1"' sh "${R}/pgdata/postmaster.pid"

HASM_TEST_ROOT="${R}" "${HERE}/destroy-test-cluster.sh" || die "destroy-test-cluster.sh refused a stopped cluster with a stale postmaster.pid."
kill -0 "${decoy}" 2>/dev/null || die "the decoy process was signalled."
[[ ! -e "${R}" ]] || die "${R} was not removed."
echo "ok: decoy ${decoy} still alive, the root with the stale postmaster.pid was destroyed."
echo "PASS"
