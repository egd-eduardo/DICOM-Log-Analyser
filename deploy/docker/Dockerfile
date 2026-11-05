# syntax=docker/dockerfile:1
# DICOM Log Analyser, central mode. Base images pinned by tag and digest (verified 2026-11-04, .NET 10.0.14).
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/DicomLogAnalyser.Core/DicomLogAnalyser.Core.csproj src/DicomLogAnalyser.Core/packages.lock.json src/DicomLogAnalyser.Core/
COPY src/DicomLogAnalyser.Web/DicomLogAnalyser.Web.csproj src/DicomLogAnalyser.Web/packages.lock.json src/DicomLogAnalyser.Web/
RUN dotnet restore src/DicomLogAnalyser.Web/DicomLogAnalyser.Web.csproj --locked-mode
COPY src/ src/
RUN dotnet publish src/DicomLogAnalyser.Web/DicomLogAnalyser.Web.csproj -c Release --no-restore -o /app -p:ContainerBuild=true -p:UseAppHost=false \
 && mkdir -p /data /sources

# Chiseled runtime: no shell or package manager, runs as the non-root "app" user (uid 1654).
# The "-extra" variant contains ICU and time-zone data, needed to convert local log times (e.g. Europe/Lisbon).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8
ENV DLA_MODE=server \
    DLA_DATA_DIR=/data \
    DLA_PORT=8080 \
    DLA_SOURCE_ROOTS=/sources \
    DOTNET_gcServer=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app
COPY --from=build /app /app
COPY --from=build --chown=1654:1654 /data /data
COPY --from=build /sources /sources
USER 1654:1654
EXPOSE 8080
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 CMD ["dotnet", "/app/DicomLogAnalyser.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "/app/DicomLogAnalyser.dll"]
