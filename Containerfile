# Moving to .NET 11 GA means re-pinning both digests (see docs/plan.md).
FROM mcr.microsoft.com/dotnet/sdk:11.0-resolute@sha256:3a9175b6c54b2f076817af1360f691c9994447b192faa48a1a1f112a516844c7 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY scripts/install-ci-tools.sh scripts/
# The script holds conftest's version and checksum, so its pin stays in one place.
RUN dotnet publish src/TfReview.Cli --configuration Release --output /app \
    && RUNNER_TEMP=/tools scripts/install-ci-tools.sh conftest

FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute@sha256:bae0269049d5061ab7f49995cc1acebf54b75471c096c4a1f874e27661d44045
ENV DOTNET_EnableDiagnostics=0
COPY --from=build /app /app
COPY --from=build /tools/ci-tools/conftest /usr/local/bin/conftest
# The base image's APP_UID, as a number so a host can tell it isn't root without resolving it.
USER 1654
ENTRYPOINT ["/app/tfreview"]
