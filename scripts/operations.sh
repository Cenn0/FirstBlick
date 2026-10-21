#!/usr/bin/env bash
set -euo pipefail
fb_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
if [[ ${FIRSTBLICK_OPS_CONTAINER:-false} == true ]]; then
  cd -- "$fb_root"
  exec docker compose --profile operations run --rm --no-deps operations "$@"
fi
fb_dotnet=${FIRSTBLICK_DOTNET:-dotnet}
exec "$fb_dotnet" run --project "$fb_root/tools/FirstBlick.Operations/FirstBlick.Operations.csproj" -c Release --no-launch-profile -- "$@"
