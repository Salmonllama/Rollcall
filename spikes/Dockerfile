# SPDX-License-Identifier: GPL-3.0-or-later
# Throwaway spike image: Linux + PowerShell 7 on .NET 10, the same runtime family as the worker.
# The .NET SDK image ships pwsh; the production worker will host PowerShell in-process instead (ADR 0002).
FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /spike
COPY *.ps1 ./
ENTRYPOINT ["pwsh", "-NoLogo", "-NoProfile", "-File"]
