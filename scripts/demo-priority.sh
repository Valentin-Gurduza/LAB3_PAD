#!/usr/bin/env bash
set -euo pipefail

GATEWAY="${GATEWAY_URL:-http://localhost:8000}"

echo "=== [PAD Lab 3] Demonstrare Coadă cu Prioritate la Gateway (MaxConcurrent=2) ==="
echo "Trimitem primele 2 cereri (ocupă cele 2 sloturi timp de ~600ms), apoi în coadă: LOW, LOW, NORMAL, HIGH, HIGH..."

fire_req() {
  local label="$1" prio="$2" delay="$3"
  local resp
  resp=$(curl -sD - -o /dev/null "${GATEWAY}/api/sales/reports" \
    -H "X-Priority: ${prio}" \
    -H "X-Simulate-Fail-Node: *" \
    -H "X-Simulate-Delay-Ms: ${delay}")
  local applied wait_ms
  applied=$(echo "$resp" | awk -F': ' 'tolower($1)=="x-priority-applied"{gsub("\r","",$2); print $2}')
  wait_ms=$(echo "$resp" | awk -F': ' 'tolower($1)=="x-queue-wait-ms"{gsub("\r","",$2); print $2}')
  printf "[%s] Terminat: %-10s | X-Priority-Applied=%-6s | Așteptare în coadă=%sms\n" "$(date +%H:%M:%S.%3N)" "$label" "$applied" "$wait_ms"
}

fire_req "slot-1" "normal" 600 &
fire_req "slot-2" "normal" 600 &
sleep 0.08

fire_req "queued-low-1"  "low"    350 &
fire_req "queued-low-2"  "low"    350 &
sleep 0.04
fire_req "queued-norm-1" "normal" 350 &
sleep 0.04
fire_req "queued-high-1" "high"   350 &
fire_req "queued-high-2" "high"   350 &

wait
echo "Observație: Cererile HIGH au fost extrase și finalizate ÎNAINTEA celor NORMAL și LOW aflate deja în așteptare."
