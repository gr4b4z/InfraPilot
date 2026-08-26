# Azure Container Apps Deployment

This repository can be deployed as a single Azure Container App:

- Nginx serves the React/Vite frontend on port `8080`
- ASP.NET API runs inside the same container on `127.0.0.1:8081`
- Nginx proxies `/api`, `/agent`, and `/health` to the API
- all browser traffic stays same-origin

## Image

Build from the repository root:

```bash
docker build -t infrapilot .
```

## Local frontend behavior

The frontend loads `/config.json` at startup.

- local development uses [src/Platform.Web/public/config.json](/Users/sylwestergrabowski/dev/infraPilot/src/Platform.Web/public/config.json:1) with `http://localhost:5259`
- the production container generates `config.json` from environment variables at startup
- for same-origin deployments, leave `BACKEND_BASE_URL` empty
- for split-host deployments, set `BACKEND_BASE_URL` to the public backend origin
- installation-specific branding is also driven by environment variables:
  - `APP_NAME`
  - `APP_SUBTITLE`
  - `ASSISTANT_NAME`
  - `PAGE_TITLE`

## Container App configuration

Use one Container App with:

- external ingress enabled on port `8080`
- optional custom domain bound directly to that app
- scale replicas based on HTTP traffic as needed
- environment variables set per installation, for example:
  - `BACKEND_BASE_URL`
  - `APP_NAME`
  - `APP_SUBTITLE`
  - `ASSISTANT_NAME`
  - `PAGE_TITLE`

This keeps browser traffic same-origin and avoids frontend CORS setup, Front Door, and extra routing layers.
