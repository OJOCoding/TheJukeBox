# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Manifests first, so a source-only change does not invalidate the restore layer.
#
# Directory.Packages.props is not optional here: central package management means the
# project files carry no versions, and a restore without it fails with NU1015.
#
# .editorconfig is not optional either, and its absence was a much stranger failure. It
# carries `dotnet_analyzer_diagnostic.severity = none` for **/Migrations/*.cs, because EF
# generates that code and it does not satisfy rules written for hand-written code. Without the
# file in the context the suppression does not exist, CA1861 fires on every generated
# migration, and TreatWarningsAsErrors turns it into a failed image build -- while
# `dotnet build` on the same commit passes, because there the file is simply present. The image
# was the only place that ever compiled without it.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/TheJukeBox/TheJukeBox.csproj src/TheJukeBox/

RUN dotnet restore src/TheJukeBox/TheJukeBox.csproj

COPY src/ src/

# The test project is deliberately not copied. Tests run in CI against the full solution;
# building them here would drag the xUnit graph into an image that never runs them.
# TreatWarningsAsErrors is on, so this also fails the image build on any new warning.
RUN dotnet publish src/TheJukeBox/TheJukeBox.csproj \
      --configuration Release \
      --no-restore \
      --output /app

# ---------------------------------------------------------------------------
# Runtime
# ---------------------------------------------------------------------------
# Chiseled: no shell, no package manager, no busybox. A much smaller attack surface, and
# it runs as a non-root user by default. The trade-off is that you cannot `docker exec` a
# shell into it, and a HEALTHCHECK cannot call curl -- see the note further down.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime

# Kestrel binds here. Plain HTTP on purpose: TLS belongs to the reverse proxy in front,
# which is also what sets the X-Forwarded-* headers the app reads to build its OAuth
# redirect URI correctly.
ENV ASPNETCORE_HTTP_PORTS=8080

# Everything mutable lives in one directory: the SQLite database, the Data Protection key
# ring, and pre-migration backups. "Back up your JukeBox" is "copy this folder".
#
# Losing the key ring is not cosmetic: it makes every stored Google refresh token
# undecryptable and forces all users to reconnect their Drive.
ENV Storage__DataPath=/data

WORKDIR /app
COPY --from=build /app .

# APP_UID is provided by the base image (64198). Declared before VOLUME so the mount
# point is owned correctly.
USER $APP_UID

VOLUME /data

EXPOSE 8080

# No HEALTHCHECK instruction. A chiseled image has no shell and no curl, so the usual
# `CMD curl -f http://localhost:8080/api/health` cannot run. The endpoint exists and is
# anonymous -- probe it from outside the container instead: a compose healthcheck on a
# sidecar, a Kubernetes httpGet probe, or your reverse proxy's own check.

ENTRYPOINT ["dotnet", "TheJukeBox.dll"]
