FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src

# Publish for the one runtime the final image actually runs on. Without a RID, `dotnet publish` ships
# every runtimes/<rid>/ folder its packages carry — Windows and macOS MSAL brokers, a 25 MB Windows
# attestation .pdb, x86/arm64 SNI copies — none of which a Linux container ever loads. That was ~60 MB
# of the old image. TARGETARCH is set by BuildKit from --platform (the release workflow builds each
# architecture natively, so it is always the host's own).
ARG TARGETARCH
RUN case "$TARGETARCH" in \
      amd64) echo linux-musl-x64 > /tmp/rid ;; \
      arm64) echo linux-musl-arm64 > /tmp/rid ;; \
      *) echo "Unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; \
    esac

COPY InfraPilot.slnx ./
COPY src/Platform.Api/Platform.Api.csproj src/Platform.Api/
RUN dotnet restore src/Platform.Api/Platform.Api.csproj -r "$(cat /tmp/rid)"

COPY . .
# Framework-dependent (the aspnet base image supplies the runtime), single RID, English resources only:
# the satellite assemblies for 12 other UI languages are never selected because the API runs with the
# container's invariant/English UI culture.
RUN dotnet publish src/Platform.Api/Platform.Api.csproj -c Release -o /app/api \
      -r "$(cat /tmp/rid)" --self-contained false --no-restore \
      -p:UseAppHost=false -p:SatelliteResourceLanguages=en \
    # libmsalruntime.so (36 MB) is the Linux MSAL *broker* — the desktop-login integration that
    # Microsoft.Data.SqlClient drags in via Microsoft.Identity.Client.Broker. It is only dlopen'ed for
    # `Authentication=Active Directory Interactive`, which a headless API never uses. It is also
    # glibc-only, so it could not be loaded on this Alpine (musl) image anyway.
    && rm -f /app/api/libmsalruntime.so

FROM node:26-alpine AS web-build
WORKDIR /app

ARG APP_VERSION=dev
ENV APP_VERSION=$APP_VERSION

COPY src/Platform.Web/package.json src/Platform.Web/package-lock.json ./
RUN npm ci

COPY src/Platform.Web/ ./
RUN npm run build:docker

# Alpine rather than Debian: the runtime image is half the size and nginx comes from apk as a ~2 MB
# package instead of an apt transaction that pulled in tens of megabytes of dependencies.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

# The .NET Alpine images ship without ICU or tzdata and run in invariant-globalization mode. The
# Debian image had both, and the API depends on them: analytics buckets call
# TimeZoneInfo.FindSystemTimeZoneById (silently falling back to UTC when zoneinfo is missing), and
# culture-aware comparison/sorting of user-facing names would turn ordinal. Installing them (~35 MB)
# and switching invariant mode off keeps runtime behaviour identical to the old image. krb5-libs is
# the GSSAPI provider that System.Net.Security.Native dlopens at load; without it every start logs
# "Error loading shared library libgssapi_krb5.so.2" (Debian's runtime-deps include it).
#
# Alpine's nginx.conf includes conf.d/ at the *main* level and http.d/ inside the http block, so a
# `server {}` file has to live in http.d/. The package's own default.conf (port 80) is removed so it
# cannot shadow ours.
RUN apk add --no-cache nginx icu-libs icu-data-full tzdata krb5-libs \
    && rm -f /etc/nginx/http.d/default.conf
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY infra/nginx-single.conf /etc/nginx/http.d/default.conf
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
