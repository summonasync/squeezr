#!/usr/bin/env bash
# One-command installer for sqzrlite on machines without a Squeezr checkout:
#   curl -fsSL <raw-url>/scripts/install-release-sqzrlite.sh | bash
# Resolves the latest published release (or the tag given via --version) the
# same way scripts/install-release.sh does for the full CLI, downloads the
# sqzrlite tar.gz asset, unpacks it into ~/apps/sqzrlite/<tag> and repoints the
# sqzrlite symlink in ~/.local/bin.
#
# sqzrlite has no services, no daemons and no model downloads, so this script
# has no Docker hints, no autostart rewiring and no watchd legs - download,
# unpack, link, done.
#
# Usage: install-release-sqzrlite.sh [--version <tag>] [--force]
# Env:   SQZRLITE_RELEASES_BASE - release API repo base (default https://api.github.com/repos/summonasync/squeezr)
#        SQZRLITE_INSTALL_DIR   - versioned install store (default: ~/apps/sqzrlite)
#        SQZRLITE_BIN_DIR       - symlink directory (default: ~/.local/bin)
set -euo pipefail

repo_api="${SQZRLITE_RELEASES_BASE:-https://api.github.com/repos/summonasync/squeezr}"
repo_api="${repo_api%/}"
releases_page='https://github.com/summonasync/squeezr/releases'

usage() {
  cat <<'USAGE'
Usage: install-release-sqzrlite.sh [--version <tag>] [--force]

Installs the sqzrlite lite CLI from a published release into
$SQZRLITE_INSTALL_DIR/<tag> (default ~/apps/sqzrlite) and repoints the
sqzrlite symlink in $SQZRLITE_BIN_DIR (default ~/.local/bin).

Options:
  --version <tag>  install this release tag instead of latest (e.g. v1.0.0)
  --force          re-download even if the tag is already installed
  -h, --help       show this help
USAGE
}

die() { echo "install-release-sqzrlite: $1" >&2; exit 1; }
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
    die "native Windows detected - build sqzrlite.exe from source with scripts/publish-sqzrlite.sh --rid win-x64."
    ;;
  *) die "unsupported OS $(uname -s)" ;;
esac

case "$(uname -m)" in
  x86_64) rid='linux-x64' ;;
  aarch64|arm64)
    die "no linux-arm64 builds are published (linux-x64 only); on arm64 hardware build from source with scripts/install-sqzrlite.sh"
    ;;
  *) die "unsupported architecture $(uname -m)" ;;
esac

install_dir="${SQZRLITE_INSTALL_DIR:-$HOME/apps/sqzrlite}"
bin_dir="${SQZRLITE_BIN_DIR:-$HOME/.local/bin}"
bin_dir="${bin_dir%/}"
mkdir -p "$install_dir" "$bin_dir"
install_dir="$(cd "$install_dir" && pwd)"

fetch_json() {
  local url=$1 out=$2 code
  code=$(curl -sS -o "$out" -w '%{http_code}' --max-time 30 "$url") || code=000
  case "$code" in
    200) ;;
    404) die "release not found at $url - published releases are listed on $releases_page" ;;
    *) die "release API returned HTTP $code for $url" ;;
  esac
}

json="$(mktemp)"
staging=''
cleanup() {
  [[ -n "${staging:-}" ]] && rm -rf "$staging"
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
asset_url=$(tr -d '\n' < "$json" \
  | grep -o '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | sed 's/.*"\(http[^"]*\)"$/\1/' \
  | grep -E -- "sqzrlite-$rid[.]tar[.]gz$" \
  | head -n 1)
[[ -n "$asset_url" ]] || die "release $tag has no sqzrlite-$rid tar.gz asset - check $releases_page"

target="$install_dir/$tag"
archive_name="${asset_url##*/}"

if [[ -x "$target/sqzrlite" && "$force" -eq 0 ]]; then
  step "$tag already installed at $target - refreshing symlink only (--force re-downloads)"
else
  staging="$(mktemp -d "$install_dir/.download.XXXXXX")"
  step "Downloading $archive_name"
  curl -fSL --retry 2 -o "$staging/$archive_name" "$asset_url"
  tar -tzf "$staging/$archive_name" > /dev/null \
    || die "downloaded archive failed the tar integrity check - try again or use --force"

  step "Unpacking into $target"
  mkdir "$staging/extract"
  tar -xzf "$staging/$archive_name" -C "$staging/extract"
  rm -rf "$target"
  mv "$staging/extract" "$target"
  chmod +x "$target/sqzrlite"
fi

ln -sfn "$target/sqzrlite" "$bin_dir/.sqzrlite.tmp.$$"
mv -f "$bin_dir/.sqzrlite.tmp.$$" "$bin_dir/sqzrlite"

path_note="Symlink: $bin_dir/sqzrlite -> $target/sqzrlite"
case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) path_note="$bin_dir is not on your PATH - add it to your shell profile, then open a NEW terminal." ;;
esac

cat <<SUMMARY

sqzrlite $tag installed.
  Binary : $target/sqzrlite
  $path_note
Verify:  $bin_dir/sqzrlite --version
SUMMARY
