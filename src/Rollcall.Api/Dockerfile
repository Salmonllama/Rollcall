# SPDX-License-Identifier: GPL-3.0-or-later
# Build from the repository root: docker build -f src/Rollcall.Api/Dockerfile .

FROM --platform=$BUILDPLATFORM node:22-bookworm-slim AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
# The page imports its logo and favicon from docs/brand (../docs/brand relative to /web).
COPY docs/brand/ /docs/brand/
RUN npm run build

# The SDK stage runs natively and cross-compiles, so multi-arch builds don't emulate the build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/Rollcall.Api/Rollcall.Api.csproj -c Release -a "$TARGETARCH" -o /app
COPY --from=web /web/dist /app/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Mount point for the rollcall-config volume; owned by the app user so a new volume is writable.
RUN mkdir -p /var/lib/rollcall && chown "$APP_UID" /var/lib/rollcall
USER $APP_UID
ENTRYPOINT ["dotnet", "Rollcall.Api.dll"]
