#!/usr/bin/env bash
# HASM PostgreSQL integration tests on Linux (Claude Cloud / any disposable Linux box).
#
# Creates a FRESH, DISPOSABLE PostgreSQL 16 cluster on 127.0.0.1:5433, builds Release, runs the full
# tests/integration/Hasm.Database.IntegrationTests suite under the project's safety contract
# (PostgresClusterIdentity, docs/DEVELOPMENT.md), verifies the per-class expected counts with the
# repository's own tools/verify/pg-trx-check.js (zero failed, zero skipped), and ALWAYS tears the
# cluster, its data and its credentials down again - also after a failure or a signal.
#
# Usage:   scripts/test-pg/run-linux-pg-tests.sh            (from anywhere inside the repository)
# Options (environment, all optional):
#   HASM_PG_BIN       PostgreSQL 16 bin directory (default: /usr/lib/postgresql/16/bin)
#   HASM_PG_RESULTS   directory for the TRX result file (default: a private mktemp directory under ${TMPDIR:-/tmp}, printed at run time; contains no credentials)
#   HASM_PG_FILTER    optional `dotnet test --filter` expression. A filtered run is a DEBUG aid: the count check is skipped and the run exits 4.
#
# Exit codes: 0 all verified | 1 tests or count check failed | 2 setup/safety refusal or unexpected error | 4 filtered (unverified) run | 130/143 interrupted.
#
# Safety properties (each is enforced below, not just documented):
#   * Port 5433 only. If anything already listens on 5433 the script REFUSES; it never connects to or reuses an existing server.
#   * Our own cluster is proven before use (data_directory and port reported by the server equal ours); only then are roles created.
#   * Data directory: a fresh mktemp dir under /var/tmp, owned by the postgres OS user, mode 0700. Never the Claude scratchpad.
#   * Fresh random passwords and a fresh 256-bit cluster fingerprint per run; they exist only in this process' memory, in the
#     environment of the pg/dotnet child processes, and (for initdb only) in a 0600 file that is shredded immediately afterwards.
#   * Roles exactly as the CI job: hasm_test_admin (CREATEDB), hasm_test_roleadmin (CREATEROLE); no superuser/replication.
#   * HASM_TEST_PG_DISPOSABLE acknowledgement, marker database hasm_test_cluster_identity and fingerprint as in backend.yml.
#   * No tracked file is modified; no application code, safeguard, test or workflow is touched (restore/build only write the ignored bin/ and obj/ directories).
set -Eeuo pipefail
umask 077

PORT=5433                                   # == PostgresClusterIdentity.LocalTestPort
PGBIN="${HASM_PG_BIN:-/usr/lib/postgresql/16/bin}"
RESULTS_DIR="${HASM_PG_RESULTS:-}"          # default: a private mktemp directory (no predictable path)
MARKER_FILE=".hasm-test-cluster"            # present only in directories this script created
RUN_PREFIX="/var/tmp/hasm-pg-run."

die() { echo "ERROR: $*" >&2; exit 2; }
log() { echo "[hasm-pg] $*"; }

# Scrub inherited settings that could redirect psql or the tests to some other server: libpq (PG*), HASM test variables and application settings (Hasm__*).
# Everything the run needs is set explicitly below.
while IFS= read -r v; do unset "$v"; done < <(compgen -e | grep -E '^(PG[A-Z0-9_]*|PSQLRC|HASM_TEST_PG_[A-Z_]*|Hasm__.*)$' || true)

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
[ -f "$REPO/src/backend/Hasm.slnx" ] || die "cannot locate the repository root from $0"
cd "$REPO"

# ---------------------------------------------------------------- prerequisites
for t in dotnet node openssl psql shred flock stat find mktemp pkill sleep su; do command -v "$t" >/dev/null || die "required tool missing: $t"; done
for b in initdb pg_ctl postgres; do [ -x "$PGBIN/$b" ] || die "$PGBIN/$b not found (PostgreSQL 16 server binaries are required; this script never installs them)"; done
[ "$("$PGBIN/postgres" --version | sed -E 's/[^0-9]*([0-9]+).*/\1/')" = "16" ] || die "PostgreSQL 16 is required"
[ -f tools/verify/pg-trx-check.js ] || die "tools/verify/pg-trx-check.js not found"

# Run PostgreSQL server programs as the postgres OS user (the server refuses to run as root).
if [ "$(id -u)" = "0" ]; then
  id postgres >/dev/null 2>&1 || die "OS user 'postgres' does not exist"
  PGOWNER=postgres
  pgrun() { su postgres -s /bin/bash -c "$(printf '%q ' "$@")" 9>&-; }   # 9>&-: the lock descriptor must never reach the postmaster
else
  PGOWNER="$(id -un)"
  pgrun() { "$@" 9>&-; }
fi

# One run at a time.
# The lock is taken on the /var/tmp directory itself: no lock file is created or truncated, so a planted symlink cannot redirect a write.
exec 9</var/tmp
flock -n 9 || die "another run of this script is active"

# Stop a cluster ONLY IF the PID in its postmaster.pid is alive and its command line names exactly this data directory
# (a recycled PID of an unrelated process, e.g. a host PostgreSQL, is never signalled). Returns 0 when nothing of ours is left running.
cluster_pid() {            # prints the verified postmaster PID of data directory $1, or nothing
  local pidfile="$1/postmaster.pid" pid
  [ -f "$pidfile" ] && [ ! -L "$pidfile" ] || return 0
  pid="$(head -1 "$pidfile" 2>/dev/null || true)"
  [[ "$pid" =~ ^[0-9]+$ ]] || return 0
  [ -r "/proc/$pid/cmdline" ] || return 0
  if tr '\0' ' ' < "/proc/$pid/cmdline" | grep -qF -- "-D $1"; then echo "$pid"; fi
}
stop_cluster() {           # $1 = data directory
  local pid; pid="$(cluster_pid "$1")"
  [ -n "$pid" ] || return 0
  pgrun "$PGBIN/pg_ctl" -D "$1" -m immediate -w stop >/dev/null 2>&1 || true
  pid="$(cluster_pid "$1")"
  if [ -n "$pid" ]; then sleep 2; pid="$(cluster_pid "$1")"; fi
  if [ -n "$pid" ]; then kill -KILL "$pid" 2>/dev/null || true; sleep 1; fi
  pid="$(cluster_pid "$1")"
  [ -z "$pid" ]
}

# ---------------------------------------------------------------- cleanup (always)
WORK=""
TEST_PID=""
cleanup() {
  local rc=$?
  trap '' INT TERM HUP      # a second signal must not interrupt the teardown
  trap - EXIT
  set +e
  # Stop a still-running test process (signal path) before the database goes away.
  [ -n "$TEST_PID" ] && { pkill -TERM -P "$TEST_PID" 2>/dev/null; kill -TERM "$TEST_PID" 2>/dev/null; sleep 2; pkill -KILL -P "$TEST_PID" 2>/dev/null; kill -KILL "$TEST_PID" 2>/dev/null; }
  if [ -n "$WORK" ] && [ -f "$WORK/$MARKER_FILE" ]; then
    log "tearing down the disposable cluster"
    if ! stop_cluster "$WORK/data"; then
      echo "WARNING: the disposable postmaster could not be stopped; leaving $WORK in place" >&2
      exit 2
    fi
    # Credentials: overwrite and unlink every file in the secrets area, then remove the whole run directory.
    find "$WORK" -maxdepth 2 -type f \( -name '*.pw' -o -name '*.env' \) -exec shred -u {} + 2>/dev/null
    rm -rf "$WORK"
    [ -e "$WORK" ] && { echo "WARNING: could not remove $WORK" >&2; rc=$((rc==0?2:rc)); }
  fi
  case "$rc" in 0|1|2|4|130|143) ;; *) rc=2;; esac     # unexpected tool exit codes (e.g. psql's 3) must not collide with documented ones
  unset PGPASSWORD HASM_TEST_PG_ADMIN_CONNECTION HASM_TEST_PG_ROLE_ADMIN_CONNECTION HASM_TEST_PG_CLUSTER_FINGERPRINT SUPW ADMPW ROLEPW FP
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM HUP

# ---------------------------------------------------------------- stale runs of THIS script (idempotency)
# Only directories carrying our marker file are ever touched; their own postmaster is stopped through its data directory (signal only, no connection).
for d in "$RUN_PREFIX"*; do
  [ -e "$d" ] || continue
  # Only a real directory (never a symlink) that this tool created: owned by the cluster OS user, mode 700, with a regular marker file and a real data directory.
  if [ -L "$d" ] || [ ! -d "$d" ] || [ -L "$d/$MARKER_FILE" ] || [ ! -f "$d/$MARKER_FILE" ] || [ -L "$d/data" ] \
     || [ "$(stat -c '%U %a' "$d")" != "$PGOWNER 700" ]; then
    echo "WARNING: leaving $d untouched (does not look like a directory created by this script)" >&2
    continue
  fi
  log "removing stale disposable cluster directory $d"
  stop_cluster "$d/data" || { echo "WARNING: could not stop the cluster in $d; leaving it untouched" >&2; continue; }
  find "$d" -maxdepth 2 -type f -name '*.pw' -exec shred -u {} + 2>/dev/null || true
  rm -rf "$d"
done

# ---------------------------------------------------------------- port 5433 must be free (never reuse or touch anything else)
# Fail-closed ownership test: try to BIND 127.0.0.1:$PORT ourselves (nothing is connected to, nothing is sent to whoever may own the port).
# Bind succeeds only when no listener holds the port (an existing 127.0.0.1 or wildcard listener makes it fail). /proc/net/tcp is not used because
# some sandboxes do not expose it, which would make a listener invisible (fail-open). Any outcome other than a clean bind-and-release refuses.
node -e '
const s = require("net").createServer();
s.once("error", () => process.exit(1));
s.listen({ port: Number(process.argv[1]), host: "127.0.0.1", exclusive: true }, () => s.close(() => process.exit(0)));
' "$PORT" 9>&- || die "port $PORT is not free (or cannot be tested); refusing (this script only ever creates its own cluster)"

# ---------------------------------------------------------------- fresh directory and credentials
WORK="$(mktemp -d "${RUN_PREFIX}XXXXXXXX")"
touch "$WORK/$MARKER_FILE"
case "$WORK" in /var/tmp/hasm-pg-run.*) ;; *) die "unexpected work directory";; esac
chown "$PGOWNER":"$(id -gn "$PGOWNER")" "$WORK"; chmod 700 "$WORK"
mkdir "$WORK/sock" "$WORK/data"; chown "$PGOWNER" "$WORK/sock" "$WORK/data"; chmod 700 "$WORK/data"

SUPW="$(openssl rand -hex 24)"; ADMPW="$(openssl rand -hex 24)"; ROLEPW="$(openssl rand -hex 24)"; FP="$(openssl rand -hex 32)"
printf '%s' "$SUPW" > "$WORK/su.pw"; chown "$PGOWNER" "$WORK/su.pw"; chmod 600 "$WORK/su.pw"

# ---------------------------------------------------------------- initialise and start OUR cluster
log "creating a fresh PostgreSQL 16 cluster in $WORK/data (port $PORT, loopback only)"
pgrun "$PGBIN/initdb" -D "$WORK/data" -U ci_bootstrap --pwfile="$WORK/su.pw" --auth=scram-sha-256 -E UTF8 >/dev/null
shred -u "$WORK/su.pw"
pgrun "$PGBIN/pg_ctl" -D "$WORK/data" -w -l "$WORK/server.log" \
  -o "-p $PORT -c listen_addresses=127.0.0.1 -c unix_socket_directories=$WORK/sock -c fsync=off -c full_page_writes=off" start >/dev/null

export PGPASSWORD="$SUPW"
# Socket inside our own run directory (never TCP, never another server); -X ignores any psqlrc; no lock fd; secrets never appear in argv (read with \getenv from the child environment).
psqlx() { psql -X -h "$WORK/sock" -p "$PORT" -U ci_bootstrap -v ON_ERROR_STOP=1 -q -At "$@" 9>&-; }

# Belt and braces: prove that the server we reach IS the cluster we just created (data directory + port), before creating anything in it.
[ "$(psqlx -d postgres -c "SELECT current_setting('data_directory')")" = "$WORK/data" ] || die "the server on port $PORT is not the cluster this script created"
[ "$(psqlx -d postgres -c "SELECT current_setting('port')")" = "$PORT" ] || die "unexpected server port"

# ---------------------------------------------------------------- roles, identity marker, inventory (mirrors .github/workflows/backend.yml)
HASM_S_PW="$ADMPW" HASM_S_RPW="$ROLEPW" psqlx -d postgres <<'SQL'
\getenv pw HASM_S_PW
\getenv rpw HASM_S_RPW
CREATE ROLE hasm_test_admin LOGIN CREATEDB NOSUPERUSER NOCREATEROLE NOREPLICATION PASSWORD :'pw';
CREATE ROLE hasm_test_roleadmin LOGIN CREATEROLE NOSUPERUSER NOCREATEDB NOREPLICATION PASSWORD :'rpw';
CREATE DATABASE hasm_test_cluster_identity;
SQL
HASM_S_FP="$FP" psqlx -d hasm_test_cluster_identity <<'SQL'
\getenv fp HASM_S_FP
CREATE TABLE cluster_identity(fingerprint text NOT NULL, purpose text NOT NULL);
INSERT INTO cluster_identity VALUES (:'fp', 'hasm-disposable-test-cluster');
REVOKE ALL ON cluster_identity FROM PUBLIC;
GRANT SELECT ON cluster_identity TO hasm_test_admin, hasm_test_roleadmin;
SQL
INVENTORY="$(psqlx -d postgres -c "SELECT datname FROM pg_database ORDER BY 1" | tr '\n' ' ')"
[ "$INVENTORY" = "hasm_test_cluster_identity postgres template0 template1 " ] || die "unexpected database inventory: $INVENTORY"
[ "$(psqlx -d postgres -c "SELECT count(*) FROM pg_roles WHERE rolsuper AND rolname LIKE 'hasm_test_%'")" = "0" ] || die "a test role is superuser"
unset PGPASSWORD

# ---------------------------------------------------------------- build (Release) and test
if [ -z "$RESULTS_DIR" ]; then
  RESULTS_DIR="$(mktemp -d "${TMPDIR:-/tmp}/hasm-pg-results.XXXXXXXX")"
else
  [ ! -L "$RESULTS_DIR" ] || die "HASM_PG_RESULTS must not be a symbolic link"
  mkdir -p "$RESULTS_DIR"
fi
chmod 700 "$RESULTS_DIR"; rm -f "$RESULTS_DIR/pg-integration.trx"
log "results directory: $RESULTS_DIR"
log "restore + Release build"
dotnet restore src/backend/Hasm.slnx >/dev/null 9>&-
dotnet build src/backend/Hasm.slnx --no-restore --configuration Release -v quiet -nologo 9>&- 2>&1 | tail -3

TEST_ARGS=(tests/integration/Hasm.Database.IntegrationTests --no-build --configuration Release
           --logger "trx;LogFileName=pg-integration.trx" --results-directory "$RESULTS_DIR")
[ -n "${HASM_PG_FILTER:-}" ] && TEST_ARGS+=(--filter "$HASM_PG_FILTER")

log "running the integration suite (HASM_REQUIRE_PG_TESTS=1, HASM_REQUIRE_PG_ROLE_TESTS=1)"
TEST_RC=0
# env -u removes any inherited CI flags so only the documented local path (port 5433) can open the gate.
env -u GITHUB_ACTIONS -u GITHUB_RUN_ID -u RUNNER_ENVIRONMENT -u HASM_CI_PG_DIR \
  HASM_REQUIRE_PG_TESTS=1 HASM_REQUIRE_PG_ROLE_TESTS=1 \
  HASM_TEST_PG_DISPOSABLE=yes-this-server-is-disposable \
  HASM_TEST_PG_CLUSTER_FINGERPRINT="$FP" \
  HASM_TEST_PG_ADMIN_CONNECTION="Host=127.0.0.1;Port=$PORT;Username=hasm_test_admin;Password=$ADMPW;Database=postgres;SSL Mode=Disable" \
  HASM_TEST_PG_ROLE_ADMIN_CONNECTION="Host=127.0.0.1;Port=$PORT;Username=hasm_test_roleadmin;Password=$ROLEPW;Database=postgres;SSL Mode=Disable" \
  dotnet test "${TEST_ARGS[@]}" 9>&- &
TEST_PID=$!
# Waiting on a background child (instead of running it in the foreground) lets INT/TERM reach the trap immediately.
wait "$TEST_PID" || TEST_RC=$?
TEST_PID=""

# ---------------------------------------------------------------- verification
TRX="$RESULTS_DIR/pg-integration.trx"
[ -f "$TRX" ] || { echo "FAILED: no TRX result file was produced" >&2; exit 1; }
COUNTERS="$(grep -o '<Counters [^>]*>' "$TRX" | head -1)"
num() { sed -nE "s/.*[ ]$1=\"([0-9]+)\".*/\1/p" <<<"$COUNTERS"; }
TOTAL="$(num total)"; PASSED="$(num passed)"; FAILED="$(num failed)"; EXECUTED="$(num executed)"
SKIPPED=$(( ${TOTAL:-0} - ${EXECUTED:-0} ))
log "TRX counters: total=${TOTAL:-?} executed=${EXECUTED:-?} passed=${PASSED:-?} failed=${FAILED:-?} not-executed(skipped)=$SKIPPED"

if [ -n "${HASM_PG_FILTER:-}" ]; then
  log "FILTERED run: the per-class count check is skipped; this is NOT a verification (exit 4)"
  if [ "$TEST_RC" = "0" ]; then exit 4; else exit 1; fi
fi
[ "$TEST_RC" = "0" ] || { echo "FAILED: dotnet test exited with $TEST_RC" >&2; exit 1; }
if [ "${FAILED:-1}" != "0" ] || [ "$SKIPPED" != "0" ] || [ "${PASSED:-0}" != "${TOTAL:-x}" ]; then echo "FAILED: failed/skipped tests present" >&2; exit 1; fi
# Repository's own per-class expected-count registry (counts may only rise; skipped/missing/unregistered classes fail).
node tools/verify/pg-trx-check.js "$TRX" .github/workflows/backend.yml || { echo "FAILED: expected-count check" >&2; exit 1; }
log "VERIFIED: all expected PostgreSQL integration tests ran and passed, none skipped"
