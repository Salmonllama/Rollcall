# SPDX-License-Identifier: GPL-3.0-or-later
# Build from the repository root: docker build -f src/Rollcall.Worker/Dockerfile .
# PowerShell and the Exchange Online module are added once spike S1 settles how (see ADR 0002).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/Rollcall.Worker/Rollcall.Worker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Rollcall.Worker.dll"]
