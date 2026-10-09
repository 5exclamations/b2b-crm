#!/usr/bin/env bash
# Runs the .NET SDK inside Docker (used for development on machines without a local SDK).
# Usage: tools/dn.sh <dotnet args>    e.g. tools/dn.sh test
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
docker volume inspect crm-nuget >/dev/null 2>&1 || docker volume create crm-nuget >/dev/null
docker network inspect crm-net >/dev/null 2>&1 || docker network create crm-net >/dev/null
exec docker run --rm --user root \
  -v "$ROOT":/src -w /src -v crm-nuget:/nuget \
  -e NUGET_PACKAGES=/nuget -e DOTNET_CLI_HOME=/tmp -e DOTNET_NOLOGO=1 \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e CRM_TEST_CONNECTION="${CRM_TEST_CONNECTION:-}" \
  --network crm-net mcr.microsoft.com/dotnet/sdk:10.0 dotnet "$@"
