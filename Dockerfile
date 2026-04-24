FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src

COPY InfraPilot.slnx ./
COPY src/Platform.Api/Platform.Api.csproj src/Platform.Api/
RUN dotnet restore src/Platform.Api/Platform.Api.csproj

COPY . .
RUN dotnet publish src/Platform.Api/Platform.Api.csproj -c Release -o /app/api /p:UseAppHost=false

FROM node:25-alpine AS web-build
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

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://127.0.0.1:8081
ENV CatalogPath=/app/catalog
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

EXPOSE 8080

ENTRYPOINT ["/start.sh"]
