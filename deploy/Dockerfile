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

# Server GC with a container-aware heap; rooms are CPU-bound on the tick path.
ENV DOTNET_gcServer=1 \
    DOTNET_GCHeapHardLimitPercent=50 \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && adduser --system --uid 1001 --group rooms
COPY --from=build /app/publish ./
USER 1001

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "Pix3.Rooms.Server.dll"]
