#!/usr/bin/env bash
# One-shot run of the PostgreSQL integration suite WITHOUT a network namespace: the fallback for machines where run-isolated.sh cannot work (no root, or a container that
# forbids `unshare --net`). One command creates a fresh disposable cluster, runs the suite, verifies a full run against the per-class registry and ALWAYS destroys the cluster,
# its secrets and its directory again - after success, failure, Ctrl-C, SIGTERM and SIGHUP. run-isolated.sh stays the PREFERRED runner wherever it is available.
#
# Usage: scripts/test-pg/run-oneshot.sh [--force-shared] [--build] [extra dotnet test arguments, e.g. --filter "FullyQualifiedName~PricingApiTests"]
#        scripts/test-pg/run-oneshot.sh --recover     # only clean up a one-shot run that was killed with SIGKILL
# The suite of THIS checkout (the one the script lives in) is run. --force-shared is needed where isolated mode is available (the script otherwise refuses and names run-isolated.sh).
#
# Exit codes: 0 full run passed and verified against the registry | 1 tests, registry check or cluster creation failed | 2 safety refusal, usage error or incomplete cleanup
#             | 4 a SUBSET (extra dotnet test arguments) passed: never a verification of the suite | 130 / 143 interrupted (INT / TERM, HUP).
#
# Nothing in the safety chain is new or changed: the cluster is created, used and destroyed by the unchanged new-test-cluster.sh, run-pg-tests.sh and destroy-test-cluster.sh
# with HASM_TEST_ROOT=W/cluster, W being this run's own `mktemp -d` directory (/tmp/hasm-pg1-XXXXXX). So: 127.0.0.1:5433 only, no Unix-domain sockets, fresh random passwords,
# marker database and 256-bit fingerprint, the disposable acknowledgement, the two restricted roles, the root checks of common.sh, PostgresClusterIdentity untouched.
# Without a private namespace the cluster listens on the machine's own loopback, so only ONE such cluster can exist at a time and the script refuses instead of sharing:
#  - an inherited HASM_TEST_ROOT is refused (a one-shot run never uses, starts, stops or deletes somebody's standing cluster);
#  - the lock of the default shared cluster (/tmp/hasm-test.lock, the lock run-pg-tests.sh holds there) is taken for the whole run; if a holder has it, the script refuses;
#  - anything already listening on port 5433 is a refusal (read from /proc, and again by new-test-cluster.sh); nothing is ever connected to on 5432 or on a foreign server;
#  - one of the capacity tokens of run-isolated.sh (/tmp/hasm-iso-capacity/slot<N>.lock, HASM_ISO_WORKERS, default 2, maximum 3) is taken, so a one-shot cluster counts toward
#    the machine-wide limit; when all are taken the script waits, exactly as run-isolated.sh does;
#  - the checkout itself is locked (the same flock run-isolated.sh takes on a worktree), because both runners share its bin/obj.
# Cleanup: every process started for the run is stopped - the worker shell as this script's own background job (from the shell's job table), everything it started by
# HASM_TEST_ROOT=W/cluster in the environment it was started with; never by name, and the server is left to pg_ctl. Then the results are
# copied out, destroy-test-cluster.sh deletes the cluster, and W - only W, after a pattern and ownership check - is removed. A run killed with SIGKILL records its W in
# /tmp/hasm-iso-capacity/oneshot.owner; the next one-shot run, or --recover, cleans it up the same way.
# Results (pg-integration.trx, postgres.log) go to HASM_ONESHOT_RESULTS/<W name> or to a kept `mktemp -d` directory /tmp/hasm-pg1-results-XXXXXX.
set -euo pipefail
SELF="$(readlink -f "${BASH_SOURCE[0]}")"
HERE="$(dirname "${SELF}")"
die() { echo "ERROR: $*" >&2; exit 2; }

MODE=run; force=no; build=no
if [[ "${1:-}" == "--recover" ]]; then MODE=recover; shift; [[ $# -eq 0 ]] || die "--recover takes no arguments."; fi
if [[ "${MODE}" == run ]]; then
  if [[ "${1:-}" == "--force-shared" ]]; then force=yes; shift; fi
  if [[ "${1:-}" == "--build" ]]; then build=yes; shift; fi
  [[ "${1:-}" != "--force-shared" && "${1:-}" != "--recover" ]] || die "Usage: $0 [--force-shared] [--build] [extra dotnet test arguments] | $0 --recover"
fi
full=yes; [[ $# -eq 0 ]] || full=no

[[ -z "${HASM_TEST_ROOT:-}" ]] || die "HASM_TEST_ROOT is set (${HASM_TEST_ROOT}). A one-shot run creates and destroys its OWN root and never uses an existing one; unset it (for a standing cluster use run-pg-tests.sh)."
for c in flock mktemp awk readlink; do command -v "$c" >/dev/null 2>&1 || die "$c is required."; done
WT="$(cd "${HERE}/../.." && pwd -P)"
[[ -x "${HERE}/new-test-cluster.sh" && -x "${HERE}/run-pg-tests.sh" && -x "${HERE}/destroy-test-cluster.sh" && -d "${WT}/tests/integration/Hasm.Database.IntegrationTests" ]] \
  || die "${WT} is not a HASM checkout with the scripts/test-pg harness."
if [[ "${MODE}" == run ]]; then
  if [[ "${force}" == no && "$(id -u)" == "0" ]] && command -v unshare >/dev/null 2>&1 && unshare --net -- true 2>/dev/null; then
    die "Isolated mode is available on this machine: use scripts/test-pg/run-isolated.sh \"${WT}\" (preferred). Pass --force-shared as the first argument to run the one-shot shared mode anyway."
  fi
  [[ "${full}" == no ]] || { command -v node >/dev/null 2>&1 && [[ -f "${WT}/tools/verify/pg-trx-check.js" ]]; } || die "node and tools/verify/pg-trx-check.js are required to verify a full run."
fi

WORKERS="${HASM_ISO_WORKERS:-2}"
[[ "${WORKERS}" =~ ^[1-3]$ ]] || die "HASM_ISO_WORKERS must be 1, 2 or 3."
TMPBASE="${HASM_ISO_TMP:-/tmp}"; TMPBASE="${TMPBASE%/}"
[[ "${TMPBASE}" == /* && -d "${TMPBASE}" && ! -L "${TMPBASE}" && "$(readlink -m "${TMPBASE}")" == "${TMPBASE}" ]] || die "HASM_ISO_TMP must be an existing absolute canonical directory."
CAP="${HASM_ISO_CAPACITY_DIR:-/tmp/hasm-iso-capacity}"; CAP="${CAP%/}"
[[ "${CAP}" == /* && "$(readlink -m "${CAP}")" == "${CAP}" ]] || die "HASM_ISO_CAPACITY_DIR must be an absolute canonical path."
if [[ ! -e "${CAP}" ]]; then mkdir -m 700 "${CAP}" 2>/dev/null || [[ -d "${CAP}" ]] || die "Cannot create ${CAP}."; fi
[[ -d "${CAP}" && ! -L "${CAP}" && -O "${CAP}" && -z "$(find "${CAP}" -maxdepth 0 -perm /077)" ]] || die "${CAP} must be a plain directory owned by the current user, mode 700 (the capacity tokens of run-isolated.sh)."
OWNER_FILE="${CAP}/oneshot.owner"
[[ ! -L "${OWNER_FILE}" ]] || die "Refusing: ${OWNER_FILE} is a symbolic link."
SHARED_LOCK=/tmp/hasm-test.lock                      # the lock of the default shared cluster (common.sh: "${TEST_ROOT}.lock" for /tmp/hasm-test); never deleted
# True when <path> is exactly a run directory as mktemp creates it: directly under the temporary base, named hasm-pg1-<6 letters or digits>. A regular expression on the base
# name and an equality on the parent, not a glob: in [[ == ]] a "?" also matches "/", so a glob would accept a path such as <base>/hasm-pg1-/../xy.
is_run_dir() { [[ "$(dirname -- "$1")" == "${TMPBASE}" && "$(basename -- "$1")" =~ ^hasm-pg1-[A-Za-z0-9]{6}$ ]]; }
PG_ACCOUNT=""; if [[ "$(id -u)" == "0" ]]; then PG_ACCOUNT="${HASM_PG_RUN_AS:-postgres}"; id -u "${PG_ACCOUNT}" >/dev/null 2>&1 || die "The server account '${PG_ACCOUNT}' does not exist."; fi

# Opens a lock file on the given descriptor without ever creating it through a symbolic link or truncating it (the same rules as take_lock in common.sh).
open_lock() {   # $1 = descriptor number, $2 = file
  [[ ! -L "$2" ]] || die "Refusing: $2 is a symbolic link."
  if [[ ! -e "$2" ]]; then ( set -C; umask 077; : > "$2" ) 2>/dev/null || true; fi
  [[ -f "$2" && ! -L "$2" ]] || die "Refusing: $2 is not a regular file."
  eval "exec $1<\"\$2\"" 2>/dev/null || die "Cannot open $2 (owned by another user?); refusing."
}
# True when something listens on TCP port 5433 in this network namespace. Read from /proc: no connection is made to anything. Status 1 = nothing listens; any other
# status (unreadable /proc, awk failure) = unknown, and refuse_if_port_taken refuses on it: this check never passes on missing information.
port_5433_listening() {
  local files=(/proc/self/net/tcp)
  [[ -r /proc/self/net/tcp ]] || return 2      # the IPv4 table is REQUIRED: the test clusters listen on 127.0.0.1, and the IPv6 table alone would not show such a listener
  [[ -r /proc/self/net/tcp6 ]] && files+=(/proc/self/net/tcp6)      # absent where IPv6 is switched off
  awk 'FNR > 1 && $4 == "0A" { n = split($2, a, ":"); if (toupper(a[n]) == "1539") found = 1 } END { exit found ? 0 : 1 }' "${files[@]}"
}
refuse_if_port_taken() {
  local rc=0
  port_5433_listening || rc=$?
  [[ "${rc}" != 0 ]] || die "Something already listens on port 5433 (a standing test cluster? stop it with its own stop-test-cluster.sh). Refusing: a one-shot run only ever uses a cluster it created itself."
  [[ "${rc}" == 1 ]] || die "Cannot read the listening sockets of this machine from /proc (status ${rc}), so port 5433 cannot be shown to be free. Refusing."
}
# The processes started for the run that owns <root> (its scripts, dotnet test and the test hosts): HASM_TEST_ROOT=<root> in the environment they were started with. The
# PostgreSQL server programs are never listed (pg_ctl stops the server through destroy-test-cluster.sh), nor is this script.
run_pids() {
  local root="$1" f p
  # one grep over every environment block (NUL-separated records, whole-record match): no process is started per PID, so this stays fast on a loaded machine
  while IFS= read -r f; do
    p="${f#/proc/}"; p="${p%/environ}"
    [[ "${p}" =~ ^[0-9]+$ && "${p}" != "$$" && "${p}" != "${BASHPID}" ]] || continue
    [[ "$(readlink "/proc/${p}/exe" 2>/dev/null)" != */postgres ]] || continue
    echo "${p}"
  done < <(grep -lzxF -- "HASM_TEST_ROOT=${root}" /proc/[0-9]*/environ 2>/dev/null || true)
  return 0
}
# TERM, then KILL after a grace period. Returns 1 if a process of the run survives.
stop_run() {
  local root="$1" sig p deadline left=""
  for sig in TERM KILL; do
    for p in $(run_pids "${root}"); do kill "-${sig}" "${p}" 2>/dev/null || true; done
    deadline=$((SECONDS + 10))                             # a grace period by the clock, not by a number of polls
    while :; do
      left="$(run_pids "${root}")"
      [[ -z "${left}" ]] && return 0
      (( SECONDS < deadline )) || break
      sleep 0.25
    done
  done
  echo "WARNING: process(es) of the run on ${root} survived SIGKILL: ${left//$'\n'/ }" >&2; return 1
}
# Destroys the cluster under <W>/cluster with the unchanged destroy-test-cluster.sh. It refuses while a test session is still connected; the sessions of the processes that
# were just stopped need a moment to end, so it is tried a few times.
destroy_cluster() {
  local w="$1" try
  [[ -e "${w}/cluster" ]] || return 0
  if [[ ! -f "${w}/cluster/.hasm-disposable-test-cluster" ]]; then
    rmdir "${w}/cluster" 2>/dev/null && return 0          # created but never marked: only an EMPTY directory is removed
    echo "WARNING: ${w}/cluster has no cluster marker; left untouched." >&2; return 1
  fi
  for try in 1 2 3 4 5; do
    if HASM_TEST_ROOT="${w}/cluster" "${HERE}/destroy-test-cluster.sh" 5<&- 6<&- 7<&-; then return 0; fi   # fd 9: our held cluster lock in cleanup (accepted by take_lock); not open in recovery
    sleep 2
  done
  return 1
}
# Removes a run directory: only one matching the mktemp pattern, a plain directory owned by us, after its cluster is gone.
remove_w() {
  local w="$1"
  [[ "${w}" != */ ]] && is_run_dir "${w}" && [[ -d "${w}" && ! -L "${w}" && -O "${w}" && ! -e "${w}/cluster" ]] || { echo "WARNING: not removing ${w}." >&2; return 1; }
  rm -rf -- "${w}"
}
# A one-shot run that was killed with SIGKILL: stop what it left running, destroy its cluster, remove its directory. Called only while the shared lock is held.
recover_previous() {
  local old
  [[ -f "${OWNER_FILE}" ]] || return 0
  old="$(head -1 "${OWNER_FILE}")"
  [[ -n "${old}" ]] || return 0
  [[ "${old}" != */ ]] && is_run_dir "${old}" || { echo "WARNING: ignoring unexpected owner record ${old}." >&2; : > "${OWNER_FILE}"; return 0; }
  [[ -e "${old}" ]] || { : > "${OWNER_FILE}"; return 0; }
  [[ -d "${old}" && ! -L "${old}" && -O "${old}" ]] || die "${old} (recorded by an earlier one-shot run) is not a plain directory owned by the current user; not touching it."
  echo "Recovering ${old}, left behind by an earlier one-shot run..." >&2
  stop_run "${old}/cluster" || die "Could not stop the leftover processes of ${old}."
  destroy_cluster "${old}" || die "Could not destroy the cluster of ${old}; see above."
  remove_w "${old}" || die "Could not remove ${old}."
  : > "${OWNER_FILE}"
}

if [[ "${MODE}" == recover ]]; then
  open_lock 5 "${SHARED_LOCK}"
  flock -n 5 || die "The shared cluster lock ${SHARED_LOCK} is held (a run is active); nothing recovered."
  recover_previous
  echo "one-shot: recovery finished."
  exit 0
fi

# One run per checkout (bin/obj are shared with run-isolated.sh, which takes the same lock): an flock on the checkout directory itself, never given to the worker.
exec 6<"${WT}"
flock -n 6 || die "Another run (run-isolated.sh or run-oneshot.sh) is using ${WT}."

# The build happens before a capacity token is taken, as in run-isolated.sh.
if [[ "${build}" == yes ]]; then (cd "${WT}" && dotnet build src/backend/Hasm.slnx -c Release --nologo -v q 6<&-) || { echo "ERROR: Build failed." >&2; exit 1; }; fi

# Early refusal, before waiting for a capacity token: a held shared lock or a listener on 5433 will not go away by waiting here. Both are checked again, for real, below.
open_lock 5 "${SHARED_LOCK}"
flock -n 5 || die "The shared cluster lock ${SHARED_LOCK} is held (holder: $(cat /tmp/hasm-test/.run.holder 2>/dev/null || echo "another one-shot run or a cluster script")). Only one cluster can use this machine's port 5433 at a time."
recover_previous              # a one-shot run killed with SIGKILL may be what still holds the port: clean it up first (under the lock)
exec 5<&-
refuse_if_port_taken

SLOT=""
try_slots() {
  local n
  for n in $(seq 1 "${WORKERS}"); do
    open_lock 7 "${CAP}/slot${n}.lock"
    if flock -n 7; then SLOT="${n}"; return 0; fi
    exec 7<&-
  done
  return 1
}
waited=0
until try_slots; do
  (( waited % 60 == 0 )) && echo "All ${WORKERS} worker slots are busy (isolated or one-shot runs); waiting..." >&2
  sleep 5; waited=$((waited + 5))
done

# The single shared-mode holder. Refused, never waited for: a standing cluster's user decides when it is free.
open_lock 5 "${SHARED_LOCK}"
flock -n 5 || die "The shared cluster lock ${SHARED_LOCK} is held (holder: $(cat /tmp/hasm-test/.run.holder 2>/dev/null || echo "another one-shot run or a cluster script")). Only one cluster can use this machine's port 5433 at a time."
recover_previous
refuse_if_port_taken

# The results directory is checked BEFORE anything of the run exists. An EXISTING one is used as it is (its mode is the caller's business: it may be a shared directory);
# only a missing one is created, owner-only.
if [[ -n "${HASM_ONESHOT_RESULTS:-}" ]]; then
  if [[ -e "${HASM_ONESHOT_RESULTS}" || -L "${HASM_ONESHOT_RESULTS}" ]]; then [[ -d "${HASM_ONESHOT_RESULTS}" ]] || die "HASM_ONESHOT_RESULTS is not a directory."; else mkdir -p -m 700 -- "${HASM_ONESHOT_RESULTS}"; fi
fi

# The run's own directory. As root: root:<server group> 0710, so the server account may traverse to its 0700 data directory and nobody else may enter. Otherwise 0700.
W=""; INNER=""; CODE=2; CLEANED=no; CLEAN_OK=yes; RES=""
cleanup() {
  trap '' INT TERM HUP        # FIRST: a signal that arrives from here on must not start a second, empty cleanup and exit with the cluster still up
  [[ "${CLEANED}" == no && -n "${W}" ]] || return 0; CLEANED=yes
  local ok=yes
  # The worker shell itself is stopped as well, as this script's own background job: between new-test-cluster.sh and the exec of run-pg-tests.sh it has no child and does
  # not yet carry HASM_TEST_ROOT in the environment it was started with, so stop_run alone would not see it and the tests would start after all. Its PID is taken from the
  # shell's job table HERE (jobs -pr: running jobs only), not from a variable set by the main flow: the table names the worker from the moment it is started, also before
  # its PID has been stored anywhere, and no longer names it once it has been reaped, so a number that may belong to another process by now is never signalled.
  local worker p
  worker="$(jobs -pr)"
  for p in ${worker}; do kill -TERM "${p}" 2>/dev/null || true; done
  stop_run "${W}/cluster" || ok=no
  worker="$(jobs -pr)"      # read AGAIN: bash reaps a finished background job in its SIGCHLD handler without any wait, so a PID read before stop_run may be free (or reused) by now
  for p in ${worker}; do kill -KILL "${p}" 2>/dev/null || true; done     # only a job the table still lists as running
  wait 2>/dev/null || true
  stop_run "${W}/cluster" || ok=no      # anything the worker still started before it was stopped
  if [[ -n "${RES}" ]]; then
    cp -f "${W}/cluster/trx/pg-integration.trx" "${RES}/" 2>/dev/null || true      # keep the evidence before the cluster is destroyed
    cp -f "${W}/cluster/logs/postgres.log" "${RES}/postgres.log" 2>/dev/null || true
  fi
  destroy_cluster "${W}" || ok=no
  exec 9<&-
  if [[ "${ok}" == yes ]] && remove_w "${W}"; then
    : > "${OWNER_FILE}"
    echo "one-shot: cleaned up (run processes gone; cluster data, secrets, lock and temp files removed with ${W}). Results: ${RES:-none}"
  else
    echo "WARNING: cleanup of ${W} was incomplete; the next one-shot run, or run-oneshot.sh --recover, recovers it." >&2; CLEAN_OK=no
  fi
}
on_signal() { echo "one-shot: interrupted; cleaning up ${W:-(nothing created yet)}..." >&2; CODE="$1"; cleanup; [[ "${CLEAN_OK}" == yes ]] || CODE=2; trap - EXIT; exit "${CODE}"; }
trap 'on_signal 130' INT
trap 'on_signal 143' TERM HUP
trap 'cleanup; [[ "${CLEAN_OK}" == yes ]] || CODE=2; exit "${CODE}"' EXIT
W="$(mktemp -d "${TMPBASE}/hasm-pg1-XXXXXX")"     # the traps are already in place: from here on every exit path cleans up

if [[ -n "${PG_ACCOUNT}" ]]; then chmod 700 "${W}"; chgrp "$(id -gn "${PG_ACCOUNT}")" "${W}"; chmod 710 "${W}"; else chmod 700 "${W}"; fi
( umask 077; printf '%s\n' "${W}" > "${OWNER_FILE}.tmp" ) && mv -f "${OWNER_FILE}.tmp" "${OWNER_FILE}"
install -d -m 700 "${W}/tmp"
if [[ -n "${HASM_ONESHOT_RESULTS:-}" ]]; then
  # The per-run sub-directory must not exist yet: mkdir without -p fails on anything that is already there (a directory, a file, a symbolic link somebody planted), so
  # nothing existing is ever reused or has its mode changed.
  newres="${HASM_ONESHOT_RESULTS%/}/$(basename "${W}")"
  mkdir -m 700 -- "${newres}" || die "Cannot create the results directory ${newres} (it must not exist yet)."     # RES stays empty when this refuses: the cleanup then copies nothing into, and never names, the refused path
  RES="${newres}"
else RES="$(mktemp -d "${TMPBASE}/hasm-pg1-results-XXXXXX")"; chmod 700 "${RES}"; fi
HASM_TEST_ROOT="${W}/cluster"   # NOT exported here: only the worker's processes carry it in their environment, which is how cleanup and recovery find them
# shellcheck source=common.sh
. "${HERE}/common.sh"           # the same settings and checks as every other script, for HASM_TEST_ROOT=W/cluster (lock W/cluster.lock)
die() { echo "ERROR: $*" >&2; exit 2; }
assert_safe_locations
take_lock                       # fd 9: the run's own cluster lock, inherited by the worker's scripts

echo "one-shot: checkout ${WT}, slot ${SLOT}/${WORKERS}, run dir ${W}, results ${RES} (shared mode: no private namespace; this is the only cluster on port 5433)"
# In the background so that a signal reaches the traps at once. The worker inherits fd 9 (the cluster lock) on purpose; the shared lock, the checkout lock and the capacity
# token are NOT given to it, so a process that outlives this script can never keep them.
(
  exec 5<&- 6<&- 7<&-
  export HASM_TEST_ROOT TMPDIR="${W}/tmp" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
  "${HERE}/new-test-cluster.sh"
  exec "${HERE}/run-pg-tests.sh" "$@"
) &
INNER=$!
set +e
wait "${INNER}"; CODE=$?
set -e
INNER=""
[[ "${CODE}" == 0 ]] || CODE=1
cleanup
if [[ "${CODE}" == 0 && "${CLEAN_OK}" == yes ]]; then
  if [[ "${full}" == yes ]]; then
    set +e
    (cd "${WT}" && node tools/verify/pg-trx-check.js "${RES}/pg-integration.trx" .github/workflows/backend.yml)
    CODE=$?
    set -e
    if [[ "${CODE}" == 0 ]]; then echo "one-shot: VERIFIED - the full suite passed (0 failed, 0 skipped) and matches the per-class registry."; else echo "one-shot: FAILED - the per-class registry check did not pass." >&2; CODE=1; fi
  else
    echo "one-shot: SUBSET PASSED (exit 4) - extra dotnet test arguments were given; this is NOT a verification of the full suite and no registry check was run."
    CODE=4
  fi
fi
exit "${CODE}"
