# Moving to .NET 11 GA means re-pinning both .NET digests (see docs/plan.md).
FROM mcr.microsoft.com/dotnet/sdk:11.0-resolute@sha256:3a9175b6c54b2f076817af1360f691c9994447b192faa48a1a1f112a516844c7 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY prompts/ prompts/
RUN dotnet publish src/TfReview.Cli --configuration Release --output /app

# conftest v0.71.1 from source, because its release binary has high and critical findings from
# go1.27.1, x/net, go-jose and klauspost/compress, and no release fixes them yet. Go back to the release binary from
# scripts/install-ci-tools.sh when one does (see docs/plan.md).
FROM docker.io/library/golang:1.27.3-bookworm@sha256:d9b6a1beb9474adc2a1ce842573639b44b2a0dd49b6669f51d3821baf9ec37c9 AS conftest
WORKDIR /conftest
ENV CGO_ENABLED=0 GOTOOLCHAIN=local
RUN git init --quiet \
    && git fetch --quiet --depth 1 https://github.com/open-policy-agent/conftest 8f7ac015cb51d81992f2ffbe2b0c1451fd05d1c5 \
    && git checkout --quiet FETCH_HEAD \
    && go get golang.org/x/net@v0.60.0 github.com/go-jose/go-jose/v4@v4.1.6 github.com/klauspost/compress@v1.20.2 \
    && go build -trimpath -ldflags "-s -w -X github.com/open-policy-agent/conftest/internal/version.Version=0.71.1-patched" -o /out/conftest .

# The Infracost CLI from source for the same reason, and its plugins, which are closed source and have no
# signatures, so they're pinned by checksum. INFRACOST_CLI_PLUGIN_DIR below stops anything being fetched
# at run time. Update the versions and checksums together with the setup doc (see docs/plan.md).
FROM docker.io/library/golang:1.27.3-bookworm@sha256:d9b6a1beb9474adc2a1ce842573639b44b2a0dd49b6669f51d3821baf9ec37c9 AS infracost
WORKDIR /infracost
ENV CGO_ENABLED=0 GOTOOLCHAIN=local
RUN git init --quiet \
    && git fetch --quiet --depth 1 https://github.com/infracost/cli 22564928d350dfead14063f7a15b97ae138a3a06 \
    && git checkout --quiet FETCH_HEAD \
    && go get golang.org/x/net@v0.60.0 \
    && go build -trimpath -ldflags "-s -w -X github.com/infracost/cli/version.Version=2.18.3-patched" -o /out/infracost .
SHELL ["/bin/bash", "-euo", "pipefail", "-c"]
RUN for pin in \
      "infracost-parser-terraform-plan 0.0.37 61a7c6363a04bc8270265b80c36fc4c87e18d6758bee564ad9116734012cbd8e" \
      "infracost-provider-azure 0.0.27 9b40cc689b52c1668fbb8bb650214f7ca26fbb6a4681bd733b4943ae1cedbc94"; do \
      read -r name version sum <<<"$pin"; \
      curl -fsSL -o data.tar.gz "https://releases.infracost.io/$name/linux/amd64/v$version/data.tar.gz"; \
      sha256sum -c - <<<"$sum  data.tar.gz"; \
      tar -xzf data.tar.gz "$name"; \
      install -D -m 755 "$name" "/out/plugins/$name"; \
    done

# Checkov and its own Python, pinned by tools/checkov/uv.lock. The paths match the final image, because a
# virtual environment finds its Python by absolute path.
FROM ghcr.io/astral-sh/uv:0.13.6-debian-slim@sha256:caa9f629fcb575ba2ed6ee160ec51e82e8444f6bdc3fc05c855b2636a5511eb8 AS checkov
WORKDIR /tools/checkov
COPY tools/checkov/ ./
ENV UV_PYTHON_INSTALL_DIR=/opt/checkov/python UV_PROJECT_ENVIRONMENT=/opt/checkov/venv UV_LINK_MODE=copy
RUN uv sync --frozen --no-install-project --managed-python --python 3.13

FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute@sha256:de8d70c2b944fb4d6e2cbc14c8de808d1ebce348ea5b84b224efd64cd63398f2
ENV DOTNET_EnableDiagnostics=0 \
    INFRACOST_CLI_PLUGIN_DIR=/opt/infracost/plugins \
    INFRACOST_SKIP_UPDATE_CHECK=true \
    INFRACOST_SKIP_AGENT_CHECK=true \
    PATH=/opt/checkov/venv/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    PYTHONDONTWRITEBYTECODE=1
COPY --from=build /app /app
COPY --from=conftest /out/conftest /usr/local/bin/conftest
COPY --from=infracost /out/infracost /usr/local/bin/infracost
COPY --from=infracost /out/plugins /opt/infracost/plugins
COPY --from=checkov /opt/checkov /opt/checkov
# The base image's APP_UID, as a number so a host can tell it isn't root without resolving it.
USER 1654
ENTRYPOINT ["/app/tfreview"]
