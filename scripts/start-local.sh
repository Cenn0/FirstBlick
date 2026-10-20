#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
[[ -f .env ]] || { echo 'Run scripts/init-local.sh first.' >&2; exit 1; }
fb_start_failure() {
  local fb_exit=$?
  trap - ERR
  echo 'Local startup failed. Bounded service and health diagnostics follow.' >&2
  python3 scripts/container-diagnostics.py >&2 || true
  return "$fb_exit"
}
trap fb_start_failure ERR
docker compose build
docker compose up -d --wait --wait-timeout 180 db s3
docker compose run --rm storage-init
docker compose run --rm migrate
set -a
source .env
set +a
fb_existing_tenants=$(docker compose exec -T db psql -U firstblick -d firstblick -Atc 'SELECT count(*) FROM "Tenants"')
if [[ $fb_existing_tenants == 0 ]]; then
  docker compose run --rm -e Bootstrap__Email -e Bootstrap__Password -e Bootstrap__TenantName migrate --bootstrap
fi
docker compose up -d api worker
echo "FirstBlick is starting at http://localhost:${API_PORT:-8080}. Login credentials are in your private .env."
