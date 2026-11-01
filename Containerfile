# Moving to .NET 11 GA means re-pinning both .NET digests (see docs/plan.md).
FROM mcr.microsoft.com/dotnet/sdk:11.0-resolute@sha256:3a9175b6c54b2f076817af1360f691c9994447b192faa48a1a1f112a516844c7 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/TfReview.Cli --configuration Release --output /app

# conftest v0.71.1 from source, because its release binary has high and critical findings from
# go1.27.1, x/net and go-jose, and no release fixes them yet. Go back to the release binary from
# scripts/install-ci-tools.sh when one does (see docs/plan.md).
FROM docker.io/library/golang:1.27.3-bookworm@sha256:d9b6a1beb9474adc2a1ce842573639b44b2a0dd49b6669f51d3821baf9ec37c9 AS conftest
WORKDIR /conftest
ENV CGO_ENABLED=0 GOTOOLCHAIN=local
RUN git init --quiet \
    && git fetch --quiet --depth 1 https://github.com/open-policy-agent/conftest 8f7ac015cb51d81992f2ffbe2b0c1451fd05d1c5 \
    && git checkout --quiet FETCH_HEAD \
    && go get golang.org/x/net@v0.60.0 github.com/go-jose/go-jose/v4@v4.1.6 \
    && go build -trimpath -ldflags "-s -w -X github.com/open-policy-agent/conftest/internal/version.Version=0.71.1-patched" -o /out/conftest .

FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute@sha256:bae0269049d5061ab7f49995cc1acebf54b75471c096c4a1f874e27661d44045
ENV DOTNET_EnableDiagnostics=0
COPY --from=build /app /app
COPY --from=conftest /out/conftest /usr/local/bin/conftest
# The base image's APP_UID, as a number so a host can tell it isn't root without resolving it.
USER 1654
ENTRYPOINT ["/app/tfreview"]
