#!/usr/bin/env bash
# Self-test for run-isolated.sh recovery (no PostgreSQL server is started; nothing outside its own temporary directories is touched).
# It builds a fake left-over worker directory whose postmaster.pid points at a LIVE decoy process of the server account (a reused PID, e.g. another worker's server), runs
# `run-isolated.sh --recover` against a private capacity directory, and requires: the decoy is still alive (pg_ctl never signalled it), the left-over directory is gone and the
# owner record is cleared. A second case checks that an orphan still running in a left-over private namespace (without a server) is stopped.
# Usage (root): scripts/test-pg/test-run-isolated-recovery.sh
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
die() { echo "FAIL: $*" >&2; exit 1; }
[[ "$(id -u)" == "0" ]] || die "needs root."
ACCOUNT="${HASM_PG_RUN_AS:-postgres}"
T="$(mktemp -d "${TMPDIR:-/tmp}/hasm-iso-selftest-XXXXXX")"; chmod 700 "${T}"
CAP="${T}/cap"; mkdir -m 700 "${CAP}"
decoy=""; orphan=""; W1=""; W2=""
cleanup() {
  [[ -n "${decoy}" ]] && kill "${decoy}" 2>/dev/null || true
  [[ -n "${orphan}" ]] && kill "${orphan}" 2>/dev/null || true
  for w in "${W1}" "${W2}"; do [[ -n "${w}" && "${w}" == /tmp/hasm-pgw-?????? && -d "${w}" ]] && rm -rf -- "${w}"; done
  [[ "${T}" == "${TMPDIR:-/tmp}"/hasm-iso-selftest-?????? ]] && rm -rf -- "${T}"
}
trap 'rc=$?; cleanup; exit "${rc}"' EXIT

# a fake left-over worker directory, laid out as run-isolated.sh and new-test-cluster.sh create it
fake_w() {
  local w
  w="$(mktemp -d /tmp/hasm-pgw-XXXXXX)"; chgrp "$(id -gn "${ACCOUNT}")" "${w}"; chmod 710 "${w}"
  mkdir -m 711 "${w}/cluster"; ( umask 077; : > "${w}/cluster/.hasm-disposable-test-cluster" )
  install -d -o "${ACCOUNT}" -m 700 "${w}/cluster/pgdata"
  echo 16 | runuser -u "${ACCOUNT}" -- sh -c 'cat > "$1"' sh "${w}/cluster/pgdata/PG_VERSION"
  echo "${w}"
}

echo "case 1: stale postmaster.pid pointing at a live process of '${ACCOUNT}'"
W1="$(fake_w)"
setsid runuser -u "${ACCOUNT}" -- sleep 600 & r=$!
for _ in $(seq 1 50); do decoy="$(pgrep -u "${ACCOUNT}" -x sleep -P "${r}" || true)"; [[ -n "${decoy}" ]] && break; [[ "$(cat "/proc/${r}/comm" 2>/dev/null)" == sleep ]] && { decoy="${r}"; break; }; sleep 0.1; done
[[ -n "${decoy}" && "$(stat -c %U "/proc/${decoy}")" == "${ACCOUNT}" ]] || die "could not start the decoy."
printf '%s\n%s\n' "${decoy}" "${W1}/cluster/pgdata" | runuser -u "${ACCOUNT}" -- sh -c 'cat > "$1"' sh "${W1}/cluster/pgdata/postmaster.pid"
printf '%s\n' "${W1}" > "${CAP}/slot1.owner"
HASM_ISO_CAPACITY_DIR="${CAP}" "${HERE}/run-isolated.sh" --recover
kill -0 "${decoy}" 2>/dev/null || die "the decoy process was signalled."
[[ ! -e "${W1}" ]] || die "${W1} was not removed."
[[ ! -s "${CAP}/slot1.owner" ]] || die "owner record not cleared."
echo "ok: decoy ${decoy} still alive, left-over directory removed."

echo "case 2: orphan still running in a left-over private namespace, no server"
W2="$(fake_w)"
HASM_TEST_ROOT="${W2}/cluster" setsid unshare --net -- sleep 600 & orphan=$!
for _ in $(seq 1 50); do [[ "$(readlink "/proc/${orphan}/ns/net")" != "$(readlink /proc/self/ns/net)" ]] && break; sleep 0.1; done
printf '%s\n' "${W2}" > "${CAP}/slot2.owner"
HASM_ISO_CAPACITY_DIR="${CAP}" "${HERE}/run-isolated.sh" --recover
for _ in $(seq 1 20); do kill -0 "${orphan}" 2>/dev/null || break; [[ "$(awk '{print $3}' "/proc/${orphan}/stat" 2>/dev/null)" == Z ]] && break; sleep 0.1; done
[[ ! -d "/proc/${orphan}" || "$(awk '{print $3}' "/proc/${orphan}/stat" 2>/dev/null)" == Z ]] || die "the orphan was not stopped."
wait "${orphan}" 2>/dev/null || true; orphan=""
kill -0 "${decoy}" 2>/dev/null || die "the decoy process was signalled in case 2."
[[ ! -e "${W2}" ]] || die "${W2} was not removed."
echo "ok: orphan stopped, left-over directory removed, decoy untouched."
echo "PASS"
