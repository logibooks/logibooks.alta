# Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
# All rights reserved.
# This file is a part of Logibooks Alta adapter

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY Logibooks.Alta.Service/Logibooks.Alta.Service.csproj Logibooks.Alta.Service/
RUN dotnet restore Logibooks.Alta.Service/Logibooks.Alta.Service.csproj
COPY Logibooks.Alta.Service/ Logibooks.Alta.Service/
RUN dotnet publish Logibooks.Alta.Service/Logibooks.Alta.Service.csproj -c Debug --no-restore -p:UseAppHost=false -o /app

FROM base AS final
COPY --from=build /app/ ./
ENTRYPOINT ["dotnet", "Logibooks.Alta.Adapter.dll"]
