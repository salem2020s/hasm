#!/usr/bin/env bash
# Starts the disposable test cluster (127.0.0.1:5433 only). Not a service; no unix sockets.
. "$(dirname "${BASH_SOURCE[0]}")/common.sh"
assert_safe_locations
assert_cluster_present
take_lock
if cluster_running; then echo "Test cluster already running."; exit 0; fi
port_listening && die "Something already listens on ${TEST_HOST}:${TEST_PORT} and it is not this cluster; refusing."
# The listen address and port are also forced on the command line, so an edited postgresql.conf cannot move the server.
as_pg "${PG_BIN}/pg_ctl" -D "${DATA_DIR}" -l "${LOG_DIR}/postgres.log" -w -t 60 \
  -o "-c listen_addresses=${TEST_HOST} -c port=${TEST_PORT} -c unix_socket_directories=''" start >/dev/null
for _ in $(seq 1 60); do "${PG_BIN}/pg_isready" -h "${TEST_HOST}" -p "${TEST_PORT}" >/dev/null 2>&1 && { echo "Test cluster started on ${TEST_HOST}:${TEST_PORT}."; exit 0; }; sleep 0.5; done
die "Test cluster did not become ready; see ${LOG_DIR}/postgres.log."
