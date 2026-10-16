FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src

COPY InfraPilot.slnx ./
COPY src/Platform.Api/Platform.Api.csproj src/Platform.Api/
RUN dotnet restore src/Platform.Api/Platform.Api.csproj

COPY . .
RUN dotnet publish src/Platform.Api/Platform.Api.csproj -c Release -o /app/api /p:UseAppHost=false

FROM node:26-alpine AS web-build
WORKDIR /app

ARG APP_VERSION=dev
ENV APP_VERSION=$APP_VERSION

COPY src/Platform.Web/package.json src/Platform.Web/package-lock.json ./
RUN npm ci

COPY src/Platform.Web/ ./
RUN npm run build:docker

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends nginx \
    && rm -rf /var/lib/apt/lists/* \
    && rm -f /etc/nginx/sites-enabled/default

COPY infra/nginx-single.conf /etc/nginx/conf.d/default.conf
COPY infra/start-single-container.sh /start.sh
RUN chmod +x /start.sh

COPY --from=api-build /app/api /app/api
COPY --from=web-build /app/dist /usr/share/nginx/html
COPY catalog /app/catalog
COPY guides /app/guides
COPY knowledge /app/knowledge
COPY playbooks /app/playbooks

ENV ASPNETCORE_ENVIRONMENT=Production
# Loopback only — nginx is the container's one public listener. The base image sets
# ASPNETCORE_HTTP_PORTS=8080, which URLS overrides anyway; clearing it stops the API warning about that
# at every start.
ENV ASPNETCORE_URLS=http://127.0.0.1:8081
ENV ASPNETCORE_HTTP_PORTS=
ENV CatalogPath=/app/catalog
ENV GUIDES_PATH=/app/guides
ENV KNOWLEDGE_PATH=/app/knowledge
ENV PLAYBOOKS_PATH=/app/playbooks
ENV BACKEND_BASE_URL=
ENV APP_NAME=InfraPilot
ENV APP_SUBTITLE="Infrastructure Portal"
ENV ASSISTANT_NAME="InfraPilot Assistant"
ENV PAGE_TITLE="InfraPilot | Infrastructure Portal"
# MSAL is configured at runtime via /config.json (see start-single-container.sh).
# Empty defaults disable MSAL and fall back to the dev user; override at deploy
# time with -e AZURE_CLIENT_ID=... -e AZURE_TENANT_ID=... or equivalent.
ENV AZURE_CLIENT_ID=
ENV AZURE_TENANT_ID=
# Logging. The API starts from /app, which makes /app its content root, so the appsettings.json
# published to /app/api is never loaded — this image is configured from the environment alone (hence
# CatalogPath above). These mirror the "Logging" section of appsettings.json; without them every
# category logs at Information, every SQL statement and token validation included. SingleLine puts
# each entry on one console line, i.e. one Log Analytics row; it only takes effect with the formatter
# named explicitly.
ENV Logging__LogLevel__Default=Information
ENV Logging__LogLevel__Microsoft.AspNetCore=Warning
ENV Logging__LogLevel__Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware=Information
ENV Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Warning
ENV Logging__LogLevel__Microsoft.IdentityModel=Warning
ENV Logging__Console__FormatterName=simple
ENV Logging__Console__FormatterOptions__SingleLine=true

EXPOSE 8080

ENTRYPOINT ["/start.sh"]
