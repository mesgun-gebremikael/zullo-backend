#!/bin/sh
set -eu
if [ "${1:-}" = "migrate" ]; then
  exec /app/efbundle
fi
if [ "$#" -gt 0 ]; then
  exec "$@"
fi
service_port="${PORT:-10000}"
case "$service_port" in
  ''|*[!0-9]*) echo 'PORT must be an integer between 1024 and 65535.' >&2; exit 1 ;;
esac
if [ "$service_port" -lt 1024 ] || [ "$service_port" -gt 65535 ]; then
  echo 'PORT must be an integer between 1024 and 65535.' >&2
  exit 1
fi
: "${ConnectionStrings__Default:?Set ConnectionStrings__Default in the server environment.}"
: "${Jwt__Key:?Set a separate random Jwt__Key for this environment.}"
export ASPNETCORE_URLS="http://0.0.0.0:$service_port"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Staging}"
exec dotnet /app/Zullo.Api.dll
