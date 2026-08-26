#!/bin/sh
set -eu

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

dotnet /app/api/Platform.Api.dll &
api_pid=$!

cleanup() {
  kill "$api_pid" 2>/dev/null || true
}

trap cleanup INT TERM

nginx -g 'daemon off;' &
nginx_pid=$!

wait "$api_pid" "$nginx_pid"
