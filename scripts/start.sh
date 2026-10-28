#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

echo "=== [PAD Lab 3] Pornire simultană a întregului cluster distribuit ==="
docker compose up -d --build

echo "=== Așteptare inițializare Gateway (:8000) ==="
until curl -sf http://localhost:8000/health >/dev/null 2>&1; do
  sleep 1
done

echo "=== Așteptare înregistrare și Health Check în Consul (:8500) pentru cele 3 noduri API ==="
for _ in $(seq 1 40); do
  passing=$(curl -sf "http://localhost:8500/v1/health/service/warehouse-node?passing=true" | grep -o '"ServiceID":"api-' | wc -l || true)
  if [ "${passing}" -ge 3 ]; then
    break
  fi
  sleep 1
done

echo ""
echo "Clusterul este activ:"
echo "  - API Gateway (Ocelot):      http://localhost:8000"
echo "  - Consul Service Discovery:  http://localhost:8500"
echo "  - Mongo Express UI Shard 0:  http://localhost:8081"
echo "  - Mongo Express UI Shard 1:  http://localhost:8082"
echo "  - Mongo Express UI Shard 2:  http://localhost:8083"
