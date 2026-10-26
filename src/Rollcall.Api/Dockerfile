# SPDX-License-Identifier: GPL-3.0-or-later
# Build from the repository root: docker build -f src/Rollcall.Api/Dockerfile .

FROM node:22-bookworm-slim AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
# The page imports its logo and favicon from docs/brand (../docs/brand relative to /web).
COPY docs/brand/ /docs/brand/
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/Rollcall.Api/Rollcall.Api.csproj -c Release -o /app
COPY --from=web /web/dist /app/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Rollcall.Api.dll"]
