# Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
# All rights reserved.
# This file is a part of Logibooks Alta adapter

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY Logibooks.Alta.DevTool/Logibooks.Alta.DevTool.csproj Logibooks.Alta.DevTool/
RUN dotnet restore Logibooks.Alta.DevTool/Logibooks.Alta.DevTool.csproj
COPY Logibooks.Alta.DevTool/ Logibooks.Alta.DevTool/
RUN dotnet publish Logibooks.Alta.DevTool/Logibooks.Alta.DevTool.csproj -c Release --no-restore -p:UseAppHost=false -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/ ./
ENTRYPOINT ["dotnet", "Logibooks.Alta.DevTool.dll"]
