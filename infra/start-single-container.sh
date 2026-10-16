#!/bin/bash
# bash rather than sh: the process supervision at the bottom needs `wait -n -p` (bash 5.1+), and the
# image's /bin/sh is dash.
set -euo pipefail

json_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

backend_base_url="$(json_escape "${BACKEND_BASE_URL:-}")"
app_name="$(json_escape "${APP_NAME:-InfraPilot}")"
app_subtitle="$(json_escape "${APP_SUBTITLE:-Infrastructure Portal}")"
assistant_name="$(json_escape "${ASSISTANT_NAME:-InfraPilot Assistant}")"
page_title="$(json_escape "${PAGE_TITLE:-InfraPilot | Infrastructure Portal}")"
# MSAL is configured at container start so one image can target multiple tenants
# without rebuilding. Empty values disable MSAL and fall back to the dev user.
azure_client_id="$(json_escape "${AZURE_CLIENT_ID:-}")"
azure_tenant_id="$(json_escape "${AZURE_TENANT_ID:-}")"

cat > /usr/share/nginx/html/config.json <<EOF
{
  "backendBaseUrl": "$backend_base_url",
  "appName": "$app_name",
  "appSubtitle": "$app_subtitle",
  "assistantName": "$assistant_name",
  "pageTitle": "$page_title",
  "azureClientId": "$azure_client_id",
  "azureTenantId": "$azure_tenant_id"
}
EOF

# The container runs two processes, the API and nginx in front of it, and neither is any use without
# the other. Waiting for both kept nginx up serving 502s after the kernel OOM-killed the API, until
# the liveness probe gave up on it. So supervise them: when either exits on its own, stop the other
# and exit non-zero, and Container Apps restarts the container straight away.
dotnet /app/api/Platform.Api.dll &
api_pid=$!

nginx -g 'daemon off;' &
nginx_pid=$!

# SIGTERM/SIGINT (a new revision, scale-in, `docker stop`) is a requested shutdown: stop both
# gracefully. SIGQUIT is nginx's graceful quit (finish in-flight requests); SIGTERM runs the API's
# host shutdown.
stop_gracefully() {
  kill -QUIT "$nginx_pid" 2>/dev/null || true
  kill -TERM "$api_pid" 2>/dev/null || true
}
trap stop_gracefully INT TERM

# Waits until no child is left. A trapped signal interrupts `wait` without a process having exited
# (no pid comes back), so that just goes round again; 127 with no pid means nothing is left to wait for.
wait_all() {
  local pid rc
  while :; do
    rc=0
    wait -n -p pid || rc=$?
    if [ -z "${pid:-}" ] && [ "$rc" -eq 127 ]; then
      return
    fi
  done
}

status=0
wait -n -p exited_pid "$api_pid" "$nginx_pid" || status=$?

if [ -n "${exited_pid:-}" ]; then
  if [ "$exited_pid" = "$api_pid" ]; then
    echo "start.sh: the API exited with status $status; stopping nginx" >&2
    kill -TERM "$nginx_pid" 2>/dev/null || true
  else
    echo "start.sh: nginx exited with status $status; stopping the API" >&2
    kill -TERM "$api_pid" 2>/dev/null || true
  fi
  wait_all
  exit $(( status == 0 ? 1 : status ))
fi

# No process exited, so the wait was interrupted by SIGTERM/SIGINT and the trap has signalled both.
wait_all
