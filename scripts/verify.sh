#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
[[ -n ${FIRSTBLICK_TEST_POSTGRES:-} ]] || { echo 'FIRSTBLICK_TEST_POSTGRES is required for the real PostgreSQL verification suite.' >&2; exit 1; }
export FIRSTBLICK_REQUIRE_POSTGRES=true
fb_dotnet=${FIRSTBLICK_DOTNET:-dotnet}
"$fb_dotnet" restore FirstBlick.slnx --locked-mode
"$fb_dotnet" test FirstBlick.slnx -c Release --no-restore --disable-build-servers -m:1
cd web
npm ci --no-audit --no-fund
npm test
npm run build
