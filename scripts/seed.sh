#!/usr/bin/env bash
set -euo pipefail

GATEWAY="${GATEWAY_URL:-http://localhost:8000}"

echo "=== [PAD Lab 3] Populare Data Warehouse prin Gateway (${GATEWAY}) ==="

post_json() {
  local id="$1" region="$2" category="$3" product="$4" qty="$5" price="$6"
  local headers
  headers=$(curl -sD - -o /dev/null -X POST "${GATEWAY}/api/sales" \
    -H "Content-Type: application/json" \
    -H "Accept: application/json" \
    -d "{\"id\":\"${id}\",\"region\":\"${region}\",\"category\":\"${category}\",\"product\":\"${product}\",\"quantity\":${qty},\"unitPrice\":${price}}")
  local served shard
  served=$(echo "$headers" | awk -F': ' 'tolower($1)=="x-served-by"{gsub("\r","",$2); print $2}')
  shard=$(echo "$headers" | awk -F': ' 'tolower($1)=="x-owner-shard"{gsub("\r","",$2); print $2}')
  echo "  [JSON] ${id} (${region} / ${product}) -> Gateway a trimis la ${served}, stocat pe Shard ${shard}"
}

post_xml() {
  local id="$1" region="$2" category="$3" product="$4" qty="$5" price="$6"
  local headers
  headers=$(curl -sD - -o /dev/null -X POST "${GATEWAY}/api/sales" \
    -H "Content-Type: application/xml" \
    -H "Accept: application/xml" \
    -d "<?xml version=\"1.0\" encoding=\"utf-8\"?><SaleRecord><Id>${id}</Id><Region>${region}</Region><Category>${category}</Category><Product>${product}</Product><Quantity>${qty}</Quantity><UnitPrice>${price}</UnitPrice></SaleRecord>")
  local served shard
  served=$(echo "$headers" | awk -F': ' 'tolower($1)=="x-served-by"{gsub("\r","",$2); print $2}')
  shard=$(echo "$headers" | awk -F': ' 'tolower($1)=="x-owner-shard"{gsub("\r","",$2); print $2}')
  echo "  [XML]  ${id} (${region} / ${product}) -> Gateway a trimis la ${served}, stocat pe Shard ${shard}"
}

post_json "sale-1001" "EU"   "Electronics" "Laptop Pro 16"       5  1800
post_json "sale-1002" "US"   "Electronics" "Monitor OLED 32"     8   950
post_json "sale-1003" "APAC" "Electronics" "NVMe Server Array"   2  4200
post_json "sale-1004" "EU"   "Software"    "BI Analytics Suite" 15   300
post_json "sale-1005" "US"   "Software"    "Cloud Backup Plan"  25   120
post_json "sale-1006" "APAC" "Furniture"   "Standing Desk"      10   550
post_json "sale-1007" "EU"   "Furniture"   "Ergonomic Chair"    12   410
post_json "sale-1008" "US"   "Hardware"    "Core Switch 10GbE"   3  2100

post_xml  "sale-1009" "APAC" "Software"    "ETL Enterprise"      6   850
post_xml  "sale-1010" "EU"   "Hardware"    "UPS Rackmount"       4  1350
post_xml  "sale-1011" "US"   "Furniture"   "Conference Table"    2  1900
post_xml  "sale-1012" "APAC" "Hardware"    "Firewall Appliance"  3  1600

echo ""
echo "=== Raport Agregat Data Warehouse (GET /api/sales/reports) ==="
curl -s "${GATEWAY}/api/sales/reports" -H "Accept: application/json"
echo ""
