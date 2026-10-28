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
# Free Render services have no pre-deploy command. This is an explicit,
# single-instance test-environment option; it is never enabled by default.
case "${ZULLO_STAGING_MIGRATE_ON_START:-false}" in
  false) ;;
  true)
    if [ "$ASPNETCORE_ENVIRONMENT" != "Staging" ] || [ "${Deployment__ExpectedDatabaseName:-}" != "zullo_staging" ]; then
      echo 'Startup migration is allowed only for Staging with expected database zullo_staging.' >&2
      exit 1
    fi
    credentials_path="${Firebase__CredentialsPath:-/app/firebase-key.json}"
    if [ ! -r "$credentials_path" ]; then
      echo 'Configure the Firebase Admin Secret File before enabling staging startup migration.' >&2
      exit 1
    fi
    echo 'Applying explicitly enabled migrations for isolated Staging before server startup.'
    /app/efbundle
    ;;
  *) echo 'ZULLO_STAGING_MIGRATE_ON_START must be true or false.' >&2; exit 1 ;;
esac
exec dotnet /app/Zullo.Api.dll
