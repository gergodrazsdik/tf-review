# Moving to .NET 11 GA means re-pinning both .NET digests (see docs/plan.md).
FROM mcr.microsoft.com/dotnet/sdk:11.0-resolute@sha256:2794448a07118a6eaf00f29ab5d2bf44928517ce5ff86505a63a69230374f599 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY prompts/ prompts/
RUN dotnet publish src/TfReview.Cli --configuration Release --output /app

# conftest v0.71.1 from source, because its release binary has high and critical findings from
# go1.27.1, x/net, go-jose and klauspost/compress, and no release fixes them yet. Go back to the release binary from
# scripts/install-ci-tools.sh when one does (see docs/plan.md).
FROM docker.io/library/golang:1.27.4-bookworm@sha256:ce59b41415e2524ae99926fe1f37ce48ba8c194dcbd2a56a65436cdcefa2bd34 AS conftest
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
FROM docker.io/library/golang:1.27.4-bookworm@sha256:ce59b41415e2524ae99926fe1f37ce48ba8c194dcbd2a56a65436cdcefa2bd34 AS infracost
WORKDIR /infracost
ENV CGO_ENABLED=0 GOTOOLCHAIN=local
RUN git init --quiet \
    && git fetch --quiet --depth 1 https://github.com/infracost/cli 6771a36b9c34a3fc1d147d74e65bb1823c945c92 \
    && git checkout --quiet FETCH_HEAD \
    && go get golang.org/x/net@v0.60.0 \
    && go build -trimpath -ldflags "-s -w -X github.com/infracost/cli/version.Version=2.21.0-patched" -o /out/infracost .
SHELL ["/bin/bash", "-euo", "pipefail", "-c"]
RUN for pin in \
      "infracost-parser-terraform-plan 0.0.44 7207ea4cd4c567ee02e708e3d2afabb1ded9741e2b819a652c092678c55fe297" \
      "infracost-provider-azure 0.0.28 f22234d0e2c1c75aa8365dc2605563c96c75bd783e93b79b283c028adecf9aec"; do \
      read -r name version sum <<<"$pin"; \
      curl -fsSL -o data.tar.gz "https://releases.infracost.io/$name/linux/amd64/v$version/data.tar.gz"; \
      sha256sum -c - <<<"$sum  data.tar.gz"; \
      tar -xzf data.tar.gz "$name"; \
      install -D -m 755 "$name" "/out/plugins/$name"; \
    done

# Checkov and its own Python, pinned by tools/checkov/uv.lock. The paths match the final image, because a
# virtual environment finds its Python by absolute path.
FROM ghcr.io/astral-sh/uv:0.13.10-debian-slim@sha256:8b48445a9a356d395bc87d0cc15d6a65e91e67424562f6de9fdcd6205ac3f040 AS checkov
WORKDIR /tools/checkov
COPY tools/checkov/ ./
ENV UV_PYTHON_INSTALL_DIR=/opt/checkov/python UV_PROJECT_ENVIRONMENT=/opt/checkov/venv UV_LINK_MODE=copy
RUN uv sync --frozen --no-install-project --managed-python --python 3.13

FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute@sha256:ca396ece3f2a2e62c7a03f1d7d710b8bc7e8c9dea5597d091178d9f5bfca13f0
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
