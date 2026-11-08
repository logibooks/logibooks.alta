FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG VERSION=0.2.1
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY Logibooks.Alta.Service/Logibooks.Alta.Service.csproj Logibooks.Alta.Service/
RUN dotnet restore Logibooks.Alta.Service/Logibooks.Alta.Service.csproj --runtime linux-x64

COPY Logibooks.Alta.Service/ Logibooks.Alta.Service/
RUN dotnet publish Logibooks.Alta.Service/Logibooks.Alta.Service.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained false \
    --no-restore \
    --output /app/publish \
    -p:Version=${VERSION}

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final

ARG VERSION=0.2.1
LABEL org.opencontainers.image.title="Logibooks Alta Adapter" \
      org.opencontainers.image.version="${VERSION}"

WORKDIR /app
COPY --from=build /app/publish ./
ARG APP_UID=0
USER $APP_UID
ENTRYPOINT ["dotnet", "Logibooks.Alta.Adapter.dll"]

