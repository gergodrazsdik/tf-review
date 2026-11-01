#!/usr/bin/env bash
# Installs the CI lint tools named as arguments, each pinned by version and SHA-256.
# Same file in tf-review, tf-review-demo and tf-standards: change all three together.
set -euo pipefail

dest="${RUNNER_TEMP:-/tmp}/ci-tools"
mkdir -p "$dest"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# install <member in archive> <url> <sha256>
install_tool() {
  local member=$1 url=$2 sum=$3 file
  file="$work/${url##*/}"
  curl -fsSL -o "$file" "$url"
  echo "$sum  $file" | sha256sum -c --quiet -
  case $file in
    *.tar.gz) tar -xzf "$file" -C "$work" "$member" ;;
    *.zip) unzip -oq "$file" "$member" -d "$work" ;;
    *) ;; # a bare binary: the download is already named $member
  esac
  install -m755 "$work/$member" "$dest/${member%%_*}"
}

gh_url=https://github.com
for tool in "$@"; do
  case $tool in
    just)
      install_tool just \
        "$gh_url/casey/just/releases/download/1.59.0/just-1.59.0-x86_64-unknown-linux-musl.tar.gz" \
        409044617b6f06517c1035e55ca38c8c856ff8b650e41bc2aa1ef82d5903a23e
      ;;
    actionlint)
      install_tool actionlint \
        "$gh_url/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz" \
        8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8
      ;;
    pinact)
      install_tool pinact \
        "$gh_url/suzuki-shunsuke/pinact/releases/download/v5.0.2/pinact_linux_amd64.tar.gz" \
        7f003d8f51e984c2264d2009d430e3e0e87ae404a0e2dc2a9a277b0f731bf5b7
      ;;
    gitleaks)
      install_tool gitleaks \
        "$gh_url/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz" \
        551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb
      ;;
    tofu)
      install_tool tofu \
        "$gh_url/opentofu/opentofu/releases/download/v1.13.2/tofu_1.13.2_linux_amd64.zip" \
        723bebb0df8411daff1e5fc3862e28ff5581fe5634087f4c29a9aa41d78208b5
      ;;
    tflint)
      install_tool tflint \
        "$gh_url/terraform-linters/tflint/releases/download/v0.65.0/tflint_linux_amd64.zip" \
        d4c545ed2e29d6338604b586f34f544f380f70f243aa72f7b484713835eaec61
      ;;
    conftest)
      install_tool conftest \
        "$gh_url/open-policy-agent/conftest/releases/download/v0.71.1/conftest_0.71.1_Linux_x86_64.tar.gz" \
        c3f6b2a753bd56e377a1e51a95ae3e057f926f2e9139f7d5b5dfbbaf810f7985
      ;;
    opa)
      install_tool opa_linux_amd64_static \
        "$gh_url/open-policy-agent/opa/releases/download/v1.22.0/opa_linux_amd64_static" \
        791790254c598a942eb140959bcec9a47f844535a632fd01d11d870ba2a47047
      ;;
    *)
      echo "unknown tool: $tool" >&2
      exit 2
      ;;
  esac
done

if [ -n "${GITHUB_PATH:-}" ]; then
  echo "$dest" >>"$GITHUB_PATH"
fi
