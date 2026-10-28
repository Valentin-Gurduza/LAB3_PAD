#!/usr/bin/env bash
set -euo pipefail

init_rs() {
  local rs_name="$1"
  local h1="$2"
  local h2="$3"
  local h3="$4"

  echo "[mongo-rs-init] Waiting for members of ${rs_name} (${h1}, ${h2}, ${h3})..."
  for host in "$h1" "$h2" "$h3"; do
    until mongosh --quiet --host "${host}:27017" --eval "db.adminCommand('ping').ok" >/dev/null 2>&1; do
      sleep 1
    done
  done

  echo "[mongo-rs-init] Initializing replica set ${rs_name}..."
  mongosh --quiet --host "${h1}:27017" --eval "
    try {
      const st = rs.status();
      if (st.ok === 1) {
        print('${rs_name} already initialized');
      }
    } catch (e) {
      rs.initiate({
        _id: '${rs_name}',
        settings: { electionTimeoutMillis: 2000, heartbeatIntervalMillis: 1000 },
        members: [
          { _id: 0, host: '${h1}:27017', priority: 2 },
          { _id: 1, host: '${h2}:27017', priority: 1 },
          { _id: 2, host: '${h3}:27017', priority: 1 }
        ]
      });
    }
  "

  echo "[mongo-rs-init] Waiting for PRIMARY election on ${rs_name}..."
  for _ in $(seq 1 30); do
    for host in "$h1" "$h2" "$h3"; do
      if mongosh --quiet --host "${host}:27017" --eval "quit(db.hello().isWritablePrimary ? 0 : 1)" >/dev/null 2>&1; then
        echo "[mongo-rs-init] ${rs_name} PRIMARY is ${host}"
        return 0
      fi
    done
    sleep 1
  done

  echo "[mongo-rs-init] ERROR: ${rs_name} failed to elect a PRIMARY" >&2
  return 1
}

init_rs "rs0" "mongo-s0-1" "mongo-s0-2" "mongo-s0-3"
init_rs "rs1" "mongo-s1-1" "mongo-s1-2" "mongo-s1-3"
init_rs "rs2" "mongo-s2-1" "mongo-s2-2" "mongo-s2-3"

echo "[mongo-rs-init] All 3 shards (rs0, rs1, rs2) initialized with 1 PRIMARY + 2 SECONDARIES."
