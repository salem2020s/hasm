#!/usr/bin/env bash
# Runs the PostgreSQL integration suite of one worktree against its OWN disposable cluster inside a PRIVATE network namespace, so that two (at most three) workers can run at
# the same time on one Linux machine without sharing a cluster, port, lock, credentials, logs or results. Additive: the default single-cluster mode (new-/run-pg-tests.sh on
# /tmp/hasm-test) is unchanged and never touched.
#
# Usage: scripts/test-pg/run-isolated.sh <worktree> [--build] [extra dotnet test arguments, e.g. --filter "FullyQualifiedName~PricingApiTests"]
#        scripts/test-pg/run-isolated.sh --recover      # only clean up runs that were killed with SIGKILL (free capacity tokens only; busy ones are skipped)
#
# How it stays safe (nothing in the existing safety chain is changed or bypassed):
#  - Capacity: a run first takes one of HASM_ISO_WORKERS (default 2, hard maximum 3) capacity tokens, an exclusive flock on /tmp/hasm-iso-capacity/slot<N>.lock (never deleted);
#    when all are taken it waits. The token only limits how many clusters run at once; it holds no data.
#  - Everything of the run lives in its OWN `mktemp -d` directory W (/tmp/hasm-pgw-XXXXXX, root:postgres 0710 - the server account may only traverse it): the cluster root
#    W/cluster (data, logs, secrets, TRX; created by the unchanged new-test-cluster.sh), its lock W/cluster.lock, and the tests' TMPDIR W/tmp.
#  - The worktree's OWN unchanged new-test-cluster.sh and run-pg-tests.sh run with HASM_TEST_ROOT=W/cluster INSIDE `unshare --net`: a new network namespace whose loopback is
#    private, so the cluster listens on exactly 127.0.0.1:5433 (hard-coded, as PostgresClusterIdentity requires) but only inside the namespace. dotnet test runs in the same
#    namespace; no other TCP listener of the machine (the shared /tmp/hasm-test cluster, another worker, anything on 5432) is reachable from inside, and the worker is not
#    reachable from outside. The namespace isolates TCP/IP only: filesystem Unix-domain sockets stay reachable, so the protection against the development cluster remains what it
#    always was - PostgresClusterIdentity, the generated Host=127.0.0.1;Port=5433 connection strings and unix_socket_directories='' of the test clusters.
#  - Every run creates a fresh cluster (new random passwords, a fresh 256-bit fingerprint in the marker database, the same restricted roles and acknowledgement as always).
#  - Cleanup on success, failure, Ctrl-C, SIGTERM and SIGHUP: every process of the run's namespace is killed (identified through a held namespace descriptor, never by name),
#    the evidence is copied out, destroy-test-cluster.sh deletes the cluster, and W (only W, after a prefix check) is removed. A run killed with SIGKILL is recovered by the next
#    run that takes the same capacity token, or by --recover (it records its W in /tmp/hasm-iso-capacity/slot<N>.owner). Before the unchanged destroy-test-cluster.sh runs, a
#    postmaster.pid whose PID is not a live process of THAT data directory is moved aside inside W, so pg_ctl can never signal a reused PID (e.g. another worker's server).
#  - Results (run.log, pg-integration.trx, postgres.log) go to HASM_ISO_RESULTS/<W name> or to a new `mktemp -d` directory /tmp/hasm-pgw-results-XXXXXX, which is kept.
#  - A full run (no extra arguments) is verified against the per-class registry (tools/verify/pg-trx-check.js with .github/workflows/backend.yml of the worktree).
# Requires root (network namespaces, runuser), unshare, flock, python3 or perl (to bring the private loopback up; no `ip` needed). Linux only.
set -euo pipefail
SELF="$(readlink -f "${BASH_SOURCE[0]}")"
HERE="$(dirname "${SELF}")"
die() { echo "ERROR: $*" >&2; exit 1; }
own_net() { readlink /proc/self/ns/net; }

# Brings the loopback interface of the CURRENT (private) network namespace up; the kernel then gives it 127.0.0.1/8.
lo_up() {
  if command -v python3 >/dev/null 2>&1; then
    python3 -I -c '
import socket, fcntl, struct
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
flags = struct.unpack("16sH", fcntl.ioctl(s, 0x8913, struct.pack("16sH", b"lo", 0)))[1]   # SIOCGIFFLAGS
fcntl.ioctl(s, 0x8914, struct.pack("16sH", b"lo", flags | 1))                             # SIOCSIFFLAGS |= IFF_UP'
  elif command -v perl >/dev/null 2>&1; then
    perl -e 'use Socket; socket(my $s, PF_INET, SOCK_DGRAM, 0) or die; my $r = pack("a16 s", "lo", 0); ioctl($s, 0x8913, $r) or die "get: $!";
             my ($n, $f) = unpack("a16 s", $r); ioctl($s, 0x8914, pack("a16 s", "lo", $f | 1)) or die "set: $!";'
  else
    die "python3 or perl is required to bring up the private loopback."
  fi
}
# Listening TCP ports of the current namespace, read from /proc (no connection is made to anything).
listening_ports() {
  local h f files=()
  for f in /proc/self/net/tcp /proc/self/net/tcp6; do [[ -r "${f}" ]] && files+=("${f}"); done
  awk 'FNR > 1 && $4 == "0A" { n = split($2, a, ":"); print a[n] }' "${files[@]}" | while read -r h; do echo $((16#${h})); done | sort -un | paste -sd, -
}
fingerprint_hash() { printf '%s' "$(sed -n "s/^export HASM_TEST_PG_CLUSTER_FINGERPRINT='\(.*\)'$/\1/p" "$1")" | sha256sum | cut -c1-12; }

# ---------------------------------------------------------------- inner part: runs INSIDE the private network namespace
if [[ "${1:-}" == "--_inner" ]]; then
  outer_ns="$2"; WT="$3"; shift 3
  [[ "$(own_net)" != "${outer_ns}" ]] || die "Inner part is not in a private network namespace; refusing."
  lo_up
  l="$(listening_ports)"
  [[ -z "${l}" ]] || die "The private namespace already has listening sockets (${l}); refusing."
  echo "isolated: private network namespace $(own_net) (outer ${outer_ns}), loopback up, no listening sockets before the cluster is created."
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
  "${WT}/scripts/test-pg/new-test-cluster.sh"
  echo "isolated: listening in this namespace after creation: $(listening_ports) (cluster fingerprint sha256 prefix: $(fingerprint_hash "${HASM_TEST_ROOT}/secrets/test-env.sh"))"
  exec "${WT}/scripts/test-pg/run-pg-tests.sh" "$@"
fi

# ---------------------------------------------------------------- outer part
MODE=run
if [[ "${1:-}" == "--recover" ]]; then MODE=recover; shift; [[ $# -eq 0 ]] || die "--recover takes no arguments."
else [[ $# -ge 1 && "$1" != -* ]] || die "Usage: $0 <worktree> [--build] [extra dotnet test arguments] | $0 --recover"; fi
[[ "$(id -u)" == "0" ]] || die "run-isolated.sh needs root (network namespaces and runuser)."
for c in unshare flock runuser awk sha256sum mktemp; do command -v "$c" >/dev/null 2>&1 || die "$c is required."; done
unshare --net -- true 2>/dev/null || die "unshare --net is not permitted here (it needs root with CAP_SYS_ADMIN and a kernel/container that allows new network namespaces). Isolated mode is unavailable on this machine: use scripts/test-pg/run-oneshot.sh (one self-cleaning run at a time), or a standing shared cluster with run-pg-tests.sh, instead."

if [[ "${MODE}" == run ]]; then
WT="$(cd "$1" 2>/dev/null && pwd -P)" || die "Worktree $1 not found."; shift
[[ -x "${WT}/scripts/test-pg/run-pg-tests.sh" && -x "${WT}/scripts/test-pg/new-test-cluster.sh" && -d "${WT}/tests/integration/Hasm.Database.IntegrationTests" ]] \
  || die "${WT} is not a HASM checkout with the scripts/test-pg harness."
mine="$(git -C "${HERE}" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)" || die "git must answer for ${HERE}."
theirs="$(git -C "${WT}" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)" || die "git must answer for ${WT}."
[[ "$(readlink -m "${mine}")" == "$(readlink -m "${theirs}")" ]] || die "${WT} is not a worktree of this repository; refusing."
build=no
if [[ "${1:-}" == "--build" ]]; then build=yes; shift; fi
full=yes; [[ $# -eq 0 ]] || full=no
fi

WORKERS="${HASM_ISO_WORKERS:-2}"
[[ "${WORKERS}" =~ ^[1-3]$ ]] || die "HASM_ISO_WORKERS must be 1, 2 or 3 (more parallel clusters were not measured as safe on this machine)."
TMPBASE="${HASM_ISO_TMP:-/tmp}"; TMPBASE="${TMPBASE%/}"
[[ "${TMPBASE}" == /* && -d "${TMPBASE}" && ! -L "${TMPBASE}" && "$(readlink -m "${TMPBASE}")" == "${TMPBASE}" ]] || die "HASM_ISO_TMP must be an existing absolute canonical directory."
CAP="${HASM_ISO_CAPACITY_DIR:-/tmp/hasm-iso-capacity}"; CAP="${CAP%/}"
[[ "${CAP}" == /* && "$(readlink -m "${CAP}")" == "${CAP}" ]] || die "HASM_ISO_CAPACITY_DIR must be an absolute canonical path."
if [[ ! -e "${CAP}" ]]; then mkdir -m 700 "${CAP}" 2>/dev/null || [[ -d "${CAP}" ]] || die "Cannot create ${CAP}."; fi
[[ -d "${CAP}" && ! -L "${CAP}" && -O "${CAP}" && -z "$(find "${CAP}" -maxdepth 0 -perm /077)" ]] || die "${CAP} must be a plain directory owned by the current user, mode 700."
PG_ACCOUNT="${HASM_PG_RUN_AS:-postgres}"
PG_GROUP="$(id -gn "${PG_ACCOUNT}" 2>/dev/null)" || die "The server account '${PG_ACCOUNT}' does not exist."

OUTER_NS="$(own_net)"
# True when <path> is exactly a run directory as mktemp creates it: directly under the temporary base, named hasm-pgw-<6 letters or digits>. A regular expression on the base
# name and an equality on the parent, not a glob: in [[ == ]] a "?" also matches "/", so a glob would accept a path such as <base>/hasm-pgw-/../xy.
is_run_dir() { [[ "$(dirname -- "$1")" == "${TMPBASE}" && "$(basename -- "$1")" =~ ^hasm-pgw-[A-Za-z0-9]{6}$ ]]; }
SLOT=""; OWNER_FILE=""

NS_HELD=no
# Kills every process whose network namespace is the one held on fd 8 (never the outer namespace): TERM, then KILL after a grace period. Returns 1 if any survives.
kill_ns() {
  [[ "${NS_HELD}" == yes ]] || return 0
  local target sig p left=0 i
  target="$(readlink /proc/$$/fd/8)"
  [[ -n "${target}" && "${target}" == net:* && "${target}" != "${OUTER_NS}" ]] || { echo "WARNING: held namespace descriptor is not a private namespace; nothing killed." >&2; return 1; }
  for sig in TERM KILL; do
    for p in /proc/[0-9]*; do [[ "$(readlink "${p}/ns/net" 2>/dev/null)" == "${target}" ]] && kill "-${sig}" "${p#/proc/}" 2>/dev/null || true; done
    for i in $(seq 1 40); do
      left=0
      for p in /proc/[0-9]*; do [[ "$(readlink "${p}/ns/net" 2>/dev/null)" == "${target}" ]] && [[ "$(awk '{print $3}' "${p}/stat" 2>/dev/null)" != Z ]] && left=$((left + 1)); done
      [[ "${left}" == 0 ]] && return 0
      sleep 0.25
    done
  done
  echo "WARNING: ${left} process(es) of namespace ${target} survived SIGKILL." >&2; return 1
}
# True when <pid> is a live, non-zombie process whose command line names exactly the data directory <dir> (a server of that cluster).
pid_serves() {
  local pid="$1" dir="$2"
  [[ "${pid}" =~ ^[0-9]+$ && -d "/proc/${pid}" && "$(awk '{print $3}' "/proc/${pid}/stat" 2>/dev/null)" != Z ]] || return 1
  tr '\0' '\n' 2>/dev/null < "/proc/${pid}/cmdline" | grep -xF -- "${dir}" >/dev/null     # no -q: grep reads everything, so tr is never killed by SIGPIPE (pipefail would turn a match into a failure)
}
# True when <pid> was started with HASM_TEST_ROOT=<root> in its environment (the scripts and dotnet processes of the run that owns <root>).
# No grep -q here: it exits at the first match, tr (which writes in 8 KiB chunks) then dies of SIGPIPE on the next chunk of a large environment, and under pipefail a MATCH became a
# failure (about 2% of the calls idle, a third under load: the intermittent "the orphan was not stopped" of test-run-isolated-recovery.sh case 2).
pid_env_root() { tr '\0' '\n' 2>/dev/null < "/proc/$1/environ" | grep -xF -- "HASM_TEST_ROOT=$2" >/dev/null; }
# The unchanged destroy-test-cluster.sh asks pg_ctl about postmaster.pid and stops "the server" with -m immediate. If the recorded PID is not a live server of THIS data
# directory (the server died and the PID may have been reused, e.g. by another worker's server running as the same account), the file is moved aside inside W so that
# pg_ctl sees no server and signals nothing.
neutralize_pidfile() {
  local w="$1" f pid
  f="${w}/cluster/pgdata/postmaster.pid"
  [[ -e "${f}" || -L "${f}" ]] || return 0
  pid="$(head -1 "${f}" 2>/dev/null || true)"
  if [[ ! -L "${f}" ]] && pid_serves "${pid}" "${w}/cluster/pgdata"; then return 0; fi
  mv -f -- "${f}" "${w}/cluster/pgdata/postmaster.pid.stale"
  echo "isolated: moved aside a stale postmaster.pid of ${w} (PID ${pid:-?} is not a server of that cluster; nothing is signalled)." >&2
}
# Destroys the cluster under <W>/cluster with the unchanged destroy-test-cluster.sh, in a fresh empty namespace (nothing else listens there) and without our descriptors.
destroy_cluster() {
  local w="$1"
  [[ -e "${w}/cluster" ]] || return 0
  [[ -f "${w}/cluster/.hasm-disposable-test-cluster" ]] || { echo "WARNING: ${w}/cluster has no cluster marker; left untouched." >&2; return 1; }
  neutralize_pidfile "${w}"
  HASM_TEST_ROOT="${w}/cluster" unshare --net -- "${HERE}/destroy-test-cluster.sh" >/dev/null 6<&- 7<&- 8<&-   # fd 9: in cleanup our held cluster lock (accepted by take_lock), in recovery not open
}
# Removes a worker directory, only one matching the mktemp pattern, owned by us, after its cluster is gone.
remove_w() {
  local w="$1"
  [[ "${w}" != */ ]] && is_run_dir "${w}" && [[ -d "${w}" && ! -L "${w}" && -O "${w}" && ! -e "${w}/cluster" ]] || { echo "WARNING: not removing ${w}." >&2; return 1; }
  rm -rf -- "${w}"
}
# A process that still belongs to the run that owned <old>: the live server of its data directory, or a process started with HASM_TEST_ROOT=<old>/cluster, in a namespace
# other than ours. Prints one PID or nothing.
old_run_anchor() {
  local old="$1" pid p
  pid="$(head -1 "${old}/cluster/pgdata/postmaster.pid" 2>/dev/null || true)"
  if pid_serves "${pid}" "${old}/cluster/pgdata"; then echo "${pid}"; return 0; fi
  for p in /proc/[0-9]*; do
    p="${p#/proc/}"
    [[ "${p}" != "$$" && "$(readlink "/proc/${p}/ns/net" 2>/dev/null)" != "${OUTER_NS}" ]] || continue
    pid_env_root "${p}" "${old}/cluster" && { echo "${p}"; return 0; }
  done
  return 0
}
# Stops every process of the namespace(s) of the run that owned <old>. The namespace is held through a descriptor and re-verified against the anchor before anything is killed.
stop_old_run() {
  local old="$1" pid ns round
  for round in 1 2 3 4; do
    pid="$(old_run_anchor "${old}")"
    [[ -n "${pid}" ]] || return 0
    ns="$(readlink "/proc/${pid}/ns/net" 2>/dev/null)" || continue
    [[ "${ns}" != "${OUTER_NS}" ]] || die "A process of ${old} runs in the outer namespace; not touching it."
    exec 8<"/proc/${pid}/ns/net" 2>/dev/null || continue
    if [[ "$(readlink /proc/$$/fd/8)" == "${ns}" && "$(readlink "/proc/${pid}/ns/net" 2>/dev/null)" == "${ns}" ]] \
       && { pid_serves "${pid}" "${old}/cluster/pgdata" || pid_env_root "${pid}" "${old}/cluster"; }; then
      NS_HELD=yes
      kill_ns || die "Could not stop the leftover processes of ${old}."
    fi
    exec 8<&-; NS_HELD=no
  done
  [[ -z "$(old_run_anchor "${old}")" ]] || die "Processes of ${old} are still running; not recovering it."
}
# A run on this capacity token that was killed with SIGKILL: stop what still runs in ITS namespace, destroy its cluster, remove its directory.
recover_previous() {
  local old
  [[ -f "${OWNER_FILE}" && ! -L "${OWNER_FILE}" ]] || return 0
  old="$(head -1 "${OWNER_FILE}")"
  [[ -n "${old}" ]] || return 0
  [[ "${old}" != */ ]] && is_run_dir "${old}" || { echo "WARNING: ignoring unexpected owner record ${old}." >&2; : > "${OWNER_FILE}"; return 0; }
  [[ -e "${old}" ]] || { : > "${OWNER_FILE}"; return 0; }
  echo "Recovering ${old}, left behind by an earlier run on slot ${SLOT}..." >&2
  stop_old_run "${old}"
  destroy_cluster "${old}" || die "Could not recover ${old}; see above."
  remove_w "${old}" || die "Could not remove ${old}."
  : > "${OWNER_FILE}"
}

# Capacity token on fd 7 for the whole run. It is closed for the worker itself, so an orphaned server can never keep it.
try_slots() {
  local n lf
  for n in $(seq 1 "${WORKERS}"); do
    lf="${CAP}/slot${n}.lock"
    [[ ! -L "${lf}" ]] || die "Refusing: ${lf} is a symbolic link."
    if [[ ! -e "${lf}" ]]; then ( set -C; umask 077; : > "${lf}" ) 2>/dev/null || true; fi
    [[ -f "${lf}" && ! -L "${lf}" ]] || die "Refusing: ${lf} is not a regular file."
    exec 7<"${lf}"
    if flock -n 7; then SLOT="${n}"; return 0; fi
    exec 7<&-
  done
  return 1
}
if [[ "${MODE}" == recover ]]; then
  for n in 1 2 3; do
    lf="${CAP}/slot${n}.lock"
    [[ -s "${CAP}/slot${n}.owner" ]] || continue
    [[ ! -L "${lf}" ]] || die "Refusing: ${lf} is a symbolic link."
    if [[ ! -e "${lf}" ]]; then ( set -C; umask 077; : > "${lf}" ) 2>/dev/null || true; fi
    [[ -f "${lf}" && ! -L "${lf}" ]] || die "Refusing: ${lf} is not a regular file."
    exec 7<"${lf}"
    if flock -n 7; then SLOT="${n}"; OWNER_FILE="${CAP}/slot${n}.owner"; recover_previous; else echo "slot ${n} is in use; skipped." >&2; fi
    exec 7<&-
  done
  echo "isolated: recovery finished."
  exit 0
fi

# One isolated run per worktree (they share bin/obj): an flock on the worktree directory itself (no file is created), held until this script exits, never given to the worker.
exec 6<"${WT}"
flock -n 6 || die "Another isolated run is using ${WT}; use a separate worktree per worker."

# The build (needs the network for restore) happens outside the namespace and before a capacity token is taken.
if [[ "${build}" == yes ]]; then (cd "${WT}" && dotnet build src/backend/Hasm.slnx -c Release --nologo -v q 6<&-) || die "Build failed."; fi

waited=0
until try_slots; do
  (( waited % 60 == 0 )) && echo "All ${WORKERS} isolated worker slots are busy; waiting..." >&2
  sleep 5; waited=$((waited + 5))
done
OWNER_FILE="${CAP}/slot${SLOT}.owner"

recover_previous

# The run's own directory. root:<server group> 0710: the server account may traverse to its 0700 data directory, nobody else may enter.
# The results directory is checked BEFORE the run directory and the owner record exist, so a refusal leaves nothing behind. An EXISTING one is used as it is (its mode is the
# caller's business: it may be a shared directory); only a missing one is created, owner-only.
if [[ -n "${HASM_ISO_RESULTS:-}" ]]; then
  if [[ -e "${HASM_ISO_RESULTS}" || -L "${HASM_ISO_RESULTS}" ]]; then [[ -d "${HASM_ISO_RESULTS}" ]] || die "HASM_ISO_RESULTS is not a directory."; else mkdir -p -m 700 -- "${HASM_ISO_RESULTS}"; fi
fi
W="$(mktemp -d "${TMPBASE}/hasm-pgw-XXXXXX")"
chmod 700 "${W}"; chgrp "${PG_GROUP}" "${W}"; chmod 710 "${W}"
if [[ -n "${HASM_ISO_RESULTS:-}" ]]; then
  # The per-run sub-directory must not exist yet: mkdir without -p fails on anything that is already there (a directory, a file, a symbolic link somebody planted).
  RES="${HASM_ISO_RESULTS%/}/$(basename "${W}")"
  mkdir -m 700 -- "${RES}" || { rmdir -- "${W}"; die "Cannot create the results directory ${RES} (it must not exist yet)."; }     # W is still empty and unrecorded
else RES="$(mktemp -d "${TMPBASE}/hasm-pgw-results-XXXXXX")"; chmod 700 "${RES}"; fi
( umask 077; printf '%s\n' "${W}" > "${OWNER_FILE}.tmp" ) && mv -f "${OWNER_FILE}.tmp" "${OWNER_FILE}"
install -d -m 700 "${W}/tmp"
export HASM_TEST_ROOT="${W}/cluster"
# shellcheck source=common.sh
. "${HERE}/common.sh"           # the same settings and checks as every other script, for HASM_TEST_ROOT=W/cluster (lock W/cluster.lock)
assert_safe_locations
take_lock                       # fd 9: the run's own cluster lock, inherited by the worker's scripts

INNER=""; CODE=1; CLEANED=no; CLEAN_OK=yes
cleanup() {
  [[ "${CLEANED}" == no ]] || return 0; CLEANED=yes
  trap '' INT TERM HUP
  local ok=yes
  kill_ns || ok=no
  [[ -n "${INNER}" ]] && wait "${INNER}" 2>/dev/null || true
  cp -f "${W}/cluster/trx/pg-integration.trx" "${RES}/" 2>/dev/null || true       # keep the evidence before the cluster is destroyed
  cp -f "${W}/cluster/logs/postgres.log" "${RES}/postgres.log" 2>/dev/null || true
  if [[ "${NS_HELD}" == yes ]]; then exec 8<&-; NS_HELD=no; fi
  destroy_cluster "${W}" || ok=no
  exec 9<&-
  if [[ "${ok}" == yes ]] && remove_w "${W}"; then
    : > "${OWNER_FILE}"
    echo "isolated: cleaned up (namespace processes gone; cluster data, secrets, lock and temp files removed with ${W}). Results: ${RES}"
  else
    echo "WARNING: cleanup of ${W} was incomplete; the next run on slot ${SLOT} recovers it." >&2; CLEAN_OK=no
  fi
}
on_signal() { echo "isolated: interrupted; cleaning up ${W}..." >&2; CODE=130; cleanup; exit 130; }
trap on_signal INT TERM HUP
trap 'cleanup; [[ "${CLEAN_OK}" == yes ]] || CODE=1; exit "${CODE}"' EXIT

echo "isolated: worktree ${WT}, slot ${SLOT}/${WORKERS}, worker dir ${W}, results ${RES}"
# Started in the background so this shell can hold the worker's namespace (fd 8) and kill it on any exit. fd 9 (the cluster lock) is inherited on purpose; fd 7 is not.
TMPDIR="${W}/tmp" unshare --net -- "${SELF}" --_inner "${OUTER_NS}" "${WT}" "$@" 6<&- 7<&- > >(exec 6<&- 7<&- 9<&-; exec tee "${RES}/run.log") 2>&1 &
INNER=$!
ns=""
for _ in $(seq 1 200); do
  ns="$(readlink "/proc/${INNER}/ns/net" 2>/dev/null || true)"
  [[ -n "${ns}" && "${ns}" != "${OUTER_NS}" ]] && break
  sleep 0.05
done
# INNER is our un-reaped child, so its pid cannot be reused: the descriptor below is the private namespace of this run.
exec 8<"/proc/${INNER}/ns/net"
[[ "$(readlink /proc/$$/fd/8)" == "${ns}" && "${ns}" == net:* && "${ns}" != "${OUTER_NS}" ]] || die "Could not hold the private namespace of the worker."
NS_HELD=yes
set +e
wait "${INNER}"; CODE=$?
set -e
INNER=""
if [[ "${CODE}" == 0 && "${full}" == yes ]]; then
  cleanup
  if command -v node >/dev/null 2>&1; then
    set +e
    (cd "${WT}" && node tools/verify/pg-trx-check.js "${RES}/pg-integration.trx" .github/workflows/backend.yml) 2>&1 | tee -a "${RES}/run.log"
    CODE="${PIPESTATUS[0]}"
    set -e
  else
    echo "WARNING: node not found; the per-class registry check (tools/verify/pg-trx-check.js) was not run." >&2; CODE=1
  fi
fi
exit "${CODE}"
