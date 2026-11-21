#!/usr/bin/env bash
# Shared settings and safety checks for the disposable HASM test PostgreSQL cluster on Linux (the bash twin of common.ps1). Sourced, never executed. No secrets live here.
# The permanent development cluster (127.0.0.1:5432, or any system cluster) is never referenced, started, stopped or modified by these scripts.
# Safety properties, identical to the Windows scripts: loopback 127.0.0.1 and port 5433 ONLY (hard-coded; any other value is refused), data and secrets under a root OUTSIDE
# the repository with owner-only permissions, random fresh passwords, a marker database with a fresh 256-bit fingerprint, no Unix-domain sockets, nothing started as a service.
set -euo pipefail

TEST_PORT=5433                                         # fixed on purpose; the integration-test gate (PostgresClusterIdentity) also requires it
if [[ -n "${HASM_TEST_PORT:-}" && "${HASM_TEST_PORT}" != "${TEST_PORT}" ]]; then
  echo "Refusing: the disposable test cluster port is fixed at ${TEST_PORT} (HASM_TEST_PORT=${HASM_TEST_PORT} is not allowed)." >&2; exit 1
fi
TEST_HOST=127.0.0.1
TEST_ROOT="${HASM_TEST_ROOT:-/tmp/hasm-test}"
PG_BIN="${HASM_PG_BIN:-/usr/lib/postgresql/16/bin}"
DATA_DIR="${TEST_ROOT}/pgdata"
LOG_DIR="${TEST_ROOT}/logs"
SECRETS_DIR="${TEST_ROOT}/secrets"
ENV_FILE="${SECRETS_DIR}/test-env.sh"
SUPER_FILE="${SECRETS_DIR}/superuser.txt"
MARKER_FILE="${TEST_ROOT}/.hasm-disposable-test-cluster"   # written only by new-test-cluster.sh; destroy-test-cluster.sh refuses to delete anything without it
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
as_pg() { if [[ -n "${PG_RUN_AS}" ]]; then (cd / && runuser -u "${PG_RUN_AS}" -- "$@"); else (cd / && "$@"); fi; }
# Creates a directory owned by the server account (or the current user), mode 700.
make_pg_dir() { if [[ -n "${PG_RUN_AS}" ]]; then install -d -o "${PG_RUN_AS}" -m 700 "$1"; else install -d -m 700 "$1"; fi; }

assert_safe_locations() {
  [[ "${TEST_ROOT}" == /* ]] || die "HASM_TEST_ROOT must be an absolute path."
  [[ "${PG_BIN}" == /* && -x "${PG_BIN}/pg_ctl" && -x "${PG_BIN}/initdb" ]] || die "PostgreSQL binaries not found in ${PG_BIN} (set HASM_PG_BIN)."
  local root real
  root="${TEST_ROOT%/}"; [[ -n "${root}" ]] || die "HASM_TEST_ROOT must not be /."
  case "${root}" in
    /|/usr|/usr/*|/etc|/etc/*|/var/lib/postgresql|/var/lib/postgresql/*|/var/lib/pgsql|/var/lib/pgsql/*|/opt/postgresql*|/bin|/sbin|/lib|/boot|/root|"${HOME:-/nonexistent-home}") die "Refusing: ${root} looks like a system, home or permanent PostgreSQL location." ;;
  esac
  real="$(readlink -m "${root}")"
  case "${real}" in
    "${REPO_ROOT}"|"${REPO_ROOT}"/*) die "Refusing: the test root must be OUTSIDE the repository (${REPO_ROOT})." ;;
  esac
}
assert_cluster_present() { [[ -f "${DATA_DIR}/PG_VERSION" && -f "${MARKER_FILE}" ]] || die "No test cluster at ${DATA_DIR}; run new-test-cluster.sh first."; }
cluster_running() { as_pg "${PG_BIN}/pg_ctl" -D "${DATA_DIR}" status >/dev/null 2>&1; }
port_listening() { (exec 3<>"/dev/tcp/${TEST_HOST}/${TEST_PORT}") >/dev/null 2>&1; }
new_secret() { od -An -N32 -tx1 /dev/urandom | tr -d ' \n'; }   # 256 random bits as 64 hex characters
