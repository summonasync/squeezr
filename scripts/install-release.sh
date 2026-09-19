#!/usr/bin/env bash
# One-command installer for machines without a Squeezr checkout:
#   curl -fsSL <raw-url>/scripts/install-release.sh | bash
# Resolves the latest published Gitea release (or the tag given via
# --version), downloads the linux-x64 tar.gz, unpacks it into
# ~/apps/squeezr/<tag> and repoints the squeezr symlink in ~/.local/bin.
# No sudo - everything stays in user-writable directories. To build from
# a source checkout instead, use scripts/install.sh.
#
# Usage: install-release.sh [--version <tag>] [--force]
# Env:   SQUEEZR_GITEA       - Gitea base URL (default: http://REDACTED-LAN-HOST:3000)
#        SQUEEZR_INSTALL_DIR - versioned install store (default: ~/apps/squeezr)
#        SQUEEZR_BIN_DIR     - symlink directory (default: ~/.local/bin)
set -euo pipefail

gitea="${SQUEEZR_GITEA:-http://REDACTED-LAN-HOST:3000}"
repo_api="$gitea/api/v1/repos/REDACTED-FORGE-NS/Squeezr"
releases_page="$gitea/REDACTED-FORGE-NS/Squeezr/releases"

usage() {
  cat <<'USAGE'
Usage: install-release.sh [--version <tag>] [--force]

Installs the Squeezr CLI from a published Gitea release into
$SQUEEZR_INSTALL_DIR/<tag> (default ~/apps/squeezr) and repoints the
squeezr symlink in $SQUEEZR_BIN_DIR (default ~/.local/bin).

Options:
  --version <tag>  install this release tag instead of latest (e.g. v1.0.0)
  --force          re-download even if the tag is already installed
  -h, --help       show this help

Environment:
  SQUEEZR_GITEA        Gitea base URL (default http://REDACTED-LAN-HOST:3000)
  SQUEEZR_INSTALL_DIR  versioned install store (default ~/apps/squeezr)
  SQUEEZR_BIN_DIR      symlink location (default ~/.local/bin)
USAGE
}

die() { echo "install-release: $1" >&2; exit 1; }
step() { echo "==> $1"; }

requested_tag=''
force=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      [[ $# -ge 2 ]] || die "--version requires a tag argument"
      requested_tag="$2"
      shift 2
      ;;
    --force) force=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown option: $1" ;;
  esac
done

case "$(uname -s)" in
  Linux) ;;
  MINGW*|MSYS*|CYGWIN*)
    die "native Windows detected - this installer is Linux-only. Download squeezr-<version>-win-x64.tar.gz from $releases_page, extract it with the built-in tar (Windows 10+) and put the folder on your PATH."
    ;;
  *) die "unsupported OS $(uname -s) - this installer covers Linux only" ;;
esac

case "$(uname -m)" in
  x86_64) rid='linux-x64' ;;
  aarch64|arm64)
    die "no linux-arm64 builds are published (linux-x64 and win-x64 only); on arm64 hardware build from source with scripts/install.sh"
    ;;
  *) die "unsupported architecture $(uname -m)" ;;
esac

install_dir="${SQUEEZR_INSTALL_DIR:-$HOME/apps/squeezr}"
bin_dir="${SQUEEZR_BIN_DIR:-$HOME/.local/bin}"
bin_dir="${bin_dir%/}"
mkdir -p "$install_dir" "$bin_dir"
install_dir="$(cd "$install_dir" && pwd)"

fetch_json() {
  local url=$1 out=$2 code
  code=$(curl -sS -o "$out" -w '%{http_code}' --max-time 30 "$url") || code=000
  case "$code" in
    200) ;;
    404) die "release not found at $url - published releases are listed on $releases_page" ;;
    *) die "Gitea API returned HTTP $code for $url" ;;
  esac
}

json="$(mktemp)"
staging=''
incoming=''
link_tmp=''
cleanup() {
  [[ -n "${staging:-}" ]] && rm -rf "$staging"
  [[ -n "${incoming:-}" ]] && rm -rf "$incoming"
  [[ -n "${link_tmp:-}" ]] && rm -f "$link_tmp"
  rm -f "$json"
}
trap cleanup EXIT

if [[ -n "$requested_tag" ]]; then
  [[ "$requested_tag" == v* ]] || requested_tag="v$requested_tag"
  step "Resolving release $requested_tag"
  fetch_json "$repo_api/releases/tags/$requested_tag" "$json"
else
  step 'Resolving latest release'
  fetch_json "$repo_api/releases/latest" "$json"
fi

tag=$(sed -n 's/.*"tag_name"[^:]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$json" | head -n 1)
[[ "$tag" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "refusing suspicious tag_name '$tag' from the release response"
asset_pattern="-${rid}[.]tar[.]gz$"
asset_url=$(tr -d '\n' < "$json" \
  | grep -o '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | sed 's/.*"\(http[^"]*\)"$/\1/' \
  | grep -E -- "$asset_pattern" \
  | head -n 1)
[[ -n "$asset_url" ]] || die "release $tag has no ${rid} tar.gz asset - check $releases_page"

target="$install_dir/$tag"
archive_name="${asset_url##*/}"

if [[ -x "$target/squeezr" && "$force" -eq 0 ]]; then
  step "$tag already installed at $target - refreshing symlink only (--force re-downloads)"
else
  # Staging lives inside install_dir so the final move stays on one
  # filesystem; mktemp under /tmp could force a cross-device copy.
  staging="$(mktemp -d "$install_dir/.download.XXXXXX")"
  step "Downloading $archive_name"
  curl -fSL --retry 2 -o "$staging/$archive_name" "$asset_url"
  tar -tzf "$staging/$archive_name" > /dev/null \
    || die "downloaded archive failed the tar integrity check - try again or use --force"

  step "Unpacking into $target"
  mkdir "$staging/extract"
  tar -xzf "$staging/$archive_name" -C "$staging/extract"
  if [[ ! -x "$staging/extract/squeezr-$rid/squeezr" ]]; then
    ls -la "$staging/extract" "$staging/extract/squeezr-$rid" >&2 || true
    die "archive layout changed - expected a squeezr-$rid/squeezr binary inside $archive_name"
  fi
  chmod +x "$staging/extract/squeezr-$rid/squeezr"
  # Park the unpacked tree beside the target first: the destructive part of
  # the swap becomes a back-to-back rm+rename, and a hard crash mid-window
  # leaves .incoming.$$ recoverable instead of wiping the only copy.
  incoming="$install_dir/.incoming.$$"
  rm -rf "$incoming"
  mv "$staging/extract/squeezr-$rid" "$incoming"
  rm -rf "$target"
  mv "$incoming" "$target"
  incoming=''
fi

step 'Switching squeezr symlink'
link_tmp="$bin_dir/.squeezr.tmp.$$"
ln -sfn "$target/squeezr" "$link_tmp"
mv -T "$link_tmp" "$bin_dir/squeezr"
link_tmp=''

echo
echo "Squeezr installed."
echo "  Tag      : $tag"
echo "  Location : $target"
case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) echo "  Note     : $bin_dir is not on your PATH - add it to your shell profile, then open a NEW terminal." ;;
esac
if installed_version="$("$bin_dir/squeezr" --version)"; then
  echo "  Version  : $installed_version"
else
  echo "  Note     : $bin_dir/squeezr failed to run on this machine (check glibc); the install itself completed."
fi
