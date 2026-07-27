# syntax=docker/dockerfile:1
# Build context is the repository root:  docker build -f deploy/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project graph only, so source edits don't invalidate the restore layer.
COPY Directory.Build.props Pix3.Rooms.slnx ./
COPY src/Pix3.Rooms.Protocol/Pix3.Rooms.Protocol.csproj src/Pix3.Rooms.Protocol/
COPY src/Pix3.Rooms.Server/Pix3.Rooms.Server.csproj     src/Pix3.Rooms.Server/
COPY tests/Pix3.Rooms.Tests/Pix3.Rooms.Tests.csproj     tests/Pix3.Rooms.Tests/
COPY tools/Pix3.Rooms.LoadGen/Pix3.Rooms.LoadGen.csproj tools/Pix3.Rooms.LoadGen/
RUN dotnet restore src/Pix3.Rooms.Server/Pix3.Rooms.Server.csproj

COPY src/ src/
RUN dotnet publish src/Pix3.Rooms.Server/Pix3.Rooms.Server.csproj \
        -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Server GC (also set in runtimeconfig via Directory.Build.props). The heap limit is left to the
# container memory limit, which .NET reads by itself: GCHeapHardLimitPercent is on the rejected
# list in docs/architecture.md.
ENV DOTNET_gcServer=1 \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production

# curl is only here for HEALTHCHECK. The non-root user is NOT created by hand: the .NET base
# images already ship one (`app`, UID in $APP_UID) and the Ubuntu rootfs they use has no
# `adduser`, so rolling our own fails at build time.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./
USER $APP_UID

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "Pix3.Rooms.Server.dll"]
