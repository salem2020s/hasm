#!/usr/bin/env bash
# Regression test for the process-matching helpers of run-isolated.sh (no PostgreSQL, nothing outside its own temporary processes): pid_env_root and pid_serves must answer
# "yes" EVERY time for a process whose environment (or command line) is larger than one 8 KiB write of tr, under `set -o pipefail` as in run-isolated.sh. With `grep -q` in the
# pipeline they answered "no" now and then (SIGPIPE of tr), which made `--recover` skip a live orphan. The functions are extracted from run-isolated.sh itself.
# Usage: scripts/test-pg/test-pid-env-root.sh
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
die() { echo "FAIL: $*" >&2; exit 1; }
src="${HERE}/run-isolated.sh"
eval "$(sed -n '/^pid_env_root()/p' "${src}")"
eval "$(sed -n '/^pid_serves() {/,/^}/p' "${src}")"
declare -F pid_env_root >/dev/null && declare -F pid_serves >/dev/null || die "could not extract the helpers from run-isolated.sh."
ROOT=/tmp/hasm-selftest-root/cluster
big="$(head -c 30000 /dev/zero | tr '\0' 'x')"
# a large environment, with the variable we look for FIRST so grep matches early and exits while tr still has chunks to write
env -i HASM_TEST_ROOT="${ROOT}" PATH="${PATH}" A_BIG_ONE="${big}" B_BIG_ONE="${big}" sleep 60 & p=$!
trap 'kill "${p}" 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do [[ "$(readlink "/proc/${p}/exe" 2>/dev/null)" == */sleep ]] && break; sleep 0.1; done
[[ "$(wc -c < "/proc/${p}/environ")" -gt 20000 ]] || die "the test process has no large environment."
miss=0
for _ in $(seq 1 1500); do pid_env_root "${p}" "${ROOT}" || miss=$((miss + 1)); done
[[ "${miss}" == 0 ]] || die "pid_env_root answered no ${miss} of 1500 times for a process that carries the variable."
pid_env_root "${p}" "${ROOT}x" && die "pid_env_root matched a different root."
echo "PASS"
