#!/usr/bin/env bash
# Shared settings and safety checks for the disposable HASM test PostgreSQL cluster on Linux (the bash twin of common.ps1). Sourced, never executed. No secrets live here.
# The permanent development cluster (127.0.0.1:5432, or any system cluster) is never referenced, started, stopped or modified by these scripts.
# Safety properties, identical to the Windows scripts: loopback 127.0.0.1 and port 5433 ONLY (hard-coded; any other value is refused), data and secrets under a root OUTSIDE
# every checkout of the repository with owner-only permissions, random fresh passwords, a marker database with a fresh 256-bit fingerprint, no Unix-domain sockets, nothing started as a service.
set -euo pipefail

# Inherited settings that could redirect psql or the test process to another server are removed before anything else: libpq variables (PGHOST, PGPORT, PGSERVICE, PGOPTIONS,
# PGPASSWORD, ...; PG followed by a LETTER, so the harness's own PG_BIN/PG_RUN_AS are not matched), PSQLRC, any inherited HASM_TEST_PG_* (the only source of these is the
# generated secrets/test-env.sh, sourced by run-pg-tests.sh) and application settings (Hasm__*, matched without regard to case because .NET reads them that way).
# Only names that are valid shell identifiers can be listed and removed here. Everything a script needs is set explicitly afterwards.
scrub_inherited_env() {
  local v
  while IFS= read -r v; do unset "${v}"; done < <( { compgen -e | grep -E '^(PG[A-Z][A-Z0-9_]*|PSQLRC|HASM_TEST_PG_[A-Z0-9_]*)$'; compgen -e | grep -Ei '^hasm__'; } || true)
}
scrub_inherited_env

TEST_PORT=5433                                         # fixed on purpose; the integration-test gate (PostgresClusterIdentity) also requires it
if [[ -n "${HASM_TEST_PORT:-}" && "${HASM_TEST_PORT}" != "${TEST_PORT}" ]]; then
  echo "Refusing: the disposable test cluster port is fixed at ${TEST_PORT} (HASM_TEST_PORT=${HASM_TEST_PORT} is not allowed)." >&2; exit 1
fi
TEST_HOST=127.0.0.1
TEST_ROOT="${HASM_TEST_ROOT:-/tmp/hasm-test}"
TEST_ROOT="${TEST_ROOT%/}"
PG_BIN="${HASM_PG_BIN:-/usr/lib/postgresql/16/bin}"
DATA_DIR="${TEST_ROOT}/pgdata"
LOG_DIR="${TEST_ROOT}/logs"
SECRETS_DIR="${TEST_ROOT}/secrets"
ENV_FILE="${SECRETS_DIR}/test-env.sh"
SUPER_FILE="${SECRETS_DIR}/superuser.txt"
MARKER_FILE="${TEST_ROOT}/.hasm-disposable-test-cluster"   # written only by new-test-cluster.sh; destroy-test-cluster.sh refuses to delete anything without it
HOLDER_FILE="${TEST_ROOT}/.run.holder"
LOCK_FILE="${TEST_ROOT}.lock"                              # a SIBLING of the root, so deleting the root never deletes the lock file (it is never deleted at all)
MARKER_DB=hasm_test_cluster_identity
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd -P)"

die() { echo "ERROR: $*" >&2; exit 1; }

# PostgreSQL refuses to run as root: when this shell is root the server commands run as an unprivileged account (default "postgres", override HASM_PG_RUN_AS).
if [[ "$(id -u)" == "0" ]]; then PG_RUN_AS="${HASM_PG_RUN_AS:-postgres}"; else PG_RUN_AS="${HASM_PG_RUN_AS:-}"; fi
if [[ -n "${PG_RUN_AS}" ]]; then
  [[ "$(id -u)" == "0" ]] || die "HASM_PG_RUN_AS needs root (runuser)."
  id -u "${PG_RUN_AS}" >/dev/null 2>&1 || die "The account '${PG_RUN_AS}' does not exist."
  [[ "$(id -u "${PG_RUN_AS}")" != "0" ]] || die "The server must not run as root."
fi
# The lock descriptor (fd 9) is closed for every server command: the PostgreSQL daemon must never inherit it (it would keep the lock after the script ends).
as_pg() { if [[ -n "${PG_RUN_AS}" ]]; then (cd / && exec 9>&- && runuser -u "${PG_RUN_AS}" -- "$@"); else (cd / && exec 9>&- && "$@"); fi; }
# Creates a directory owned by the server account (or the current user), mode 700. Only ever used for directories INSIDE the test root.
make_pg_dir() { if [[ -n "${PG_RUN_AS}" ]]; then install -d -o "${PG_RUN_AS}" -m 700 "$1"; else install -d -m 700 "$1"; fi; }

# Every checkout of this repository: the current worktree, the main checkout (top of the common git dir) and every `git worktree list` path.
# Fails closed: without git, or when git cannot answer, it returns non-zero (the caller refuses) instead of checking only the current worktree.
repo_checkouts() {
  local common wt
  command -v git >/dev/null 2>&1 || return 1
  common="$(git -C "${SCRIPT_DIR}" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)" || return 1
  [[ -n "${common}" ]] || return 1
  wt="$(git -C "${SCRIPT_DIR}" worktree list --porcelain 2>/dev/null)" || return 1
  echo "${REPO_ROOT}"
  readlink -m "${common}/.."
  sed -n 's/^worktree //p' <<<"${wt}" | while read -r w; do readlink -m "${w}"; done
}

assert_safe_locations() {
  [[ "${TEST_ROOT}" == /* ]] || die "HASM_TEST_ROOT must be an absolute path."
  [[ "${PG_BIN}" == /* && -x "${PG_BIN}/pg_ctl" && -x "${PG_BIN}/initdb" ]] || die "PostgreSQL binaries not found in ${PG_BIN} (set HASM_PG_BIN)."
  [[ -n "${TEST_ROOT}" ]] || die "HASM_TEST_ROOT must not be /."
  # The literal path must ALREADY be canonical: no symbolic link in any component (also a parent folder), no "//", "." or "..". readlink -m resolves all of them.
  local real home
  [[ ! -L "${TEST_ROOT}" ]] || die "Refusing: ${TEST_ROOT} is a symbolic link."
  real="$(readlink -m "${TEST_ROOT}")"
  [[ "${real}" == "${TEST_ROOT}" ]] || die "Refusing: HASM_TEST_ROOT must be a canonical path without symbolic links, '//', '.' or '..' (${TEST_ROOT} resolves to ${real})."
  home="$(readlink -m "${HOME:-/nonexistent-home}")"
  # broad directories are never the root itself (a sub-directory such as /tmp/hasm-test is fine); system and PostgreSQL locations are never allowed at all
  case "${real}" in
    /|/tmp|/var|/var/tmp|/var/lib|/home|/srv|/opt|/mnt|/run|/media|/root|/usr|/usr/local|"${home}"|"$(dirname "${home}")") die "Refusing: ${real} is a broad system or home directory, not a dedicated test root." ;;
    /usr/*|/etc|/etc/*|/bin|/bin/*|/sbin|/sbin/*|/lib|/lib/*|/lib64|/lib64/*|/boot|/boot/*|/dev|/dev/*|/proc|/proc/*|/sys|/sys/*|/var/lib/postgresql|/var/lib/postgresql/*|/var/lib/pgsql|/var/lib/pgsql/*|/opt/postgresql*)
      die "Refusing: ${real} is a system or permanent PostgreSQL location." ;;
  esac
  # outside EVERY checkout of the repository (agent worktrees live inside the main checkout), and no checkout inside the root
  local c checkouts
  checkouts="$(repo_checkouts)" || die "Cannot determine the checkouts of the repository (git is required and must answer); refusing."
  while read -r c; do
    [[ -n "${c}" ]] || continue
    case "${real}/" in "${c}/"*) die "Refusing: the test root must be OUTSIDE the repository (${c})." ;; esac
    case "${c}/" in "${real}/"*) die "Refusing: the test root would contain the repository checkout ${c}." ;; esac
  done < <(sort -u <<<"${checkouts}")
}
assert_cluster_present() { [[ -f "${DATA_DIR}/PG_VERSION" && -f "${MARKER_FILE}" ]] || die "No test cluster at ${DATA_DIR}; run new-test-cluster.sh first."; }
# True when <pid> is a live, non-zombie process whose command line names exactly this cluster's data directory (a server of THIS cluster).
pid_serves_data_dir() {
  local pid="$1"
  [[ "${pid}" =~ ^[0-9]+$ && -d "/proc/${pid}" && "$(awk '{print $3}' "/proc/${pid}/stat" 2>/dev/null)" != Z ]] || return 1
  grep -qxF -- "${DATA_DIR}" < <(tr '\0' '\n' 2>/dev/null < "/proc/${pid}/cmdline")   # not a pipeline: grep -q leaving early must not turn into a pipefail error
}
# pg_ctl stop signals whatever PID postmaster.pid records. If that PID is not a live server of THIS data directory (the server died and the PID may have been reused by an
# unrelated process of the same account), the file is moved aside inside the data directory so that pg_ctl sees no server and signals nothing. Fail closed: a live server that
# cannot be identified (unreadable /proc) is treated the same way, and the callers then refuse because the port is still held.
neutralize_stale_pidfile() {
  local f="${DATA_DIR}/postmaster.pid" pid
  [[ -e "${f}" || -L "${f}" ]] || return 0
  pid="$(head -1 "${f}" 2>/dev/null || true)"
  if [[ ! -L "${f}" ]] && pid_serves_data_dir "${pid}"; then return 0; fi
  as_pg mv -f -- "${f}" "${DATA_DIR}/postmaster.pid.stale" 2>/dev/null || mv -f -- "${f}" "${DATA_DIR}/postmaster.pid.stale"
  echo "Moved aside a stale postmaster.pid in ${DATA_DIR} (PID ${pid:-?} is not a server of this cluster; nothing is signalled)." >&2
}
cluster_running() { as_pg "${PG_BIN}/pg_ctl" -D "${DATA_DIR}" status >/dev/null 2>&1; }
port_listening() { (exec 3<>"/dev/tcp/${TEST_HOST}/${TEST_PORT}") >/dev/null 2>&1; }
new_secret() { od -An -N32 -tx1 /dev/urandom | tr -d ' \n'; }   # 256 random bits as 64 hex characters

# Exclusive single-holder lock for every script that changes the cluster or runs the suite. The lock file is a sibling of the root and is never deleted.
# A child script started by a lock holder inherits the open lock descriptor (fd 9); flock is ALWAYS run on it (an inherited description that already holds the lock succeeds again,
# anything else - including a descriptor opened without the lock - must really acquire it or the script refuses). Nothing (no environment switch) can skip the lock.
# The lock file is created with noclobber (O_EXCL: never through a symbolic link) and opened read-only, so it is never truncated or created by the open itself.
take_lock() {
  command -v flock >/dev/null 2>&1 || die "flock is required."
  [[ ! -L "${LOCK_FILE}" ]] || die "Refusing: ${LOCK_FILE} is a symbolic link."
  if [[ ! -e "${LOCK_FILE}" ]]; then ( set -C; umask 077; : > "${LOCK_FILE}" ) 2>/dev/null || true; fi
  [[ -f "${LOCK_FILE}" && ! -L "${LOCK_FILE}" ]] || die "Refusing: ${LOCK_FILE} is not a regular file."
  if [[ -e /proc/self/fd/9 && "$(readlink -f /proc/self/fd/9 2>/dev/null)" == "$(readlink -f "${LOCK_FILE}")" ]]; then
    : # inherited from the holder: verified below by flock itself
  else
    exec 9<"${LOCK_FILE}"
  fi
  if ! flock -n 9; then
    die "Another script is using this cluster (exclusive lock ${LOCK_FILE}). Holder: $(cat "${HOLDER_FILE}" 2>/dev/null || echo unknown). Wait for it to finish."
  fi
}

# Number of test sessions (the two test logins or any hasm_test_* database) currently connected to the cluster; read-only, uses the creation-only superuser from the owner-only secrets directory.
foreign_sessions() {
  PGPASSWORD="$(cat "${SUPER_FILE}")" "${PG_BIN}/psql" -h "${TEST_HOST}" -p "${TEST_PORT}" -U hasm_test_super -d postgres -X -q -At \
    -c "SELECT count(*) FROM pg_stat_activity WHERE usename IN ('hasm_test_admin','hasm_test_roleadmin') OR datname LIKE 'hasm_test\\_%' ESCAPE '\\'"
}
