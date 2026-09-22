# syntax=docker/dockerfile:1
# Build context: backend/  (docker build -f backend/Dockerfile backend)

ARG DOTNET_VERSION=10.0

# ---- build: restore, publish the API, and produce a self-contained EF Core migration bundle ----
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY Directory.Build.props MailClient.slnx ./
COPY src/MailClient.Domain/MailClient.Domain.csproj src/MailClient.Domain/
COPY src/MailClient.Application/MailClient.Application.csproj src/MailClient.Application/
COPY src/MailClient.Infrastructure/MailClient.Infrastructure.csproj src/MailClient.Infrastructure/
COPY src/MailClient.Api/MailClient.Api.csproj src/MailClient.Api/
RUN dotnet restore src/MailClient.Api/MailClient.Api.csproj --runtime linux-x64

COPY src/ src/

RUN dotnet tool install --global dotnet-ef --version 10.0.* \
    && export PATH="$PATH:/root/.dotnet/tools" \
    && dotnet ef migrations bundle \
       --project src/MailClient.Infrastructure/MailClient.Infrastructure.csproj \
       --startup-project src/MailClient.Api/MailClient.Api.csproj \
       --configuration Release \
       --self-contained \
       --runtime linux-x64 \
       --output /app/efbundle/efbundle

RUN dotnet publish src/MailClient.Api/MailClient.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ---- migrator: applies pending EF Core migrations, then exits. No SDK, no ASP.NET. ----
# Run with: docker run mailclient-migrator --connection "Host=...;Port=...;Database=...;Username=...;Password=..."
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION} AS migrator
WORKDIR /app
COPY --from=build /app/efbundle/efbundle ./efbundle
ENTRYPOINT ["./efbundle"]

# ---- runtime: the API itself ----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --system --no-create-home --home-dir /app --shell /usr/sbin/nologin mailclient \
    && mkdir -p /app/data/attachments /app/data/protection-keys /app/logs \
    && chown -R mailclient:mailclient /app

COPY --from=build --chown=mailclient:mailclient /app/publish .

USER mailclient
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "MailClient.Api.dll"]
