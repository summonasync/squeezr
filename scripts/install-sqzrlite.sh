#!/usr/bin/env bash
# Builds and installs sqzrlite, the index-only lite CLI, for the current user
# (Linux). Publishes through scripts/publish-sqzrlite.sh (the pinned single-file
# shape), copies the binary with its native tree-sitter libraries and BuildHost
# folders into the install directory and symlinks it into ~/.local/bin.
#
# Unlike scripts/install.sh for the full Squeezr CLI there are NO services
# steps: sqzrlite never touches Docker, Qdrant, Ollama or embedding models -
# trigram + symbol indexing only, no external dependencies at all.
#
# Usage: scripts/install-sqzrlite.sh [install-dir]
set -euo pipefail

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "This installer is Linux-only (got $(uname -s)). Build on Windows with scripts/publish-sqzrlite.sh --rid win-x64." >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
install_dir="${1:-$HOME/.local/share/sqzrlite}"
bin_dir="$HOME/.local/bin"

case "$(uname -m)" in
  x86_64) rid='linux-x64' ;;
  aarch64|arm64) rid='linux-arm64' ;;
  *) echo "Unsupported architecture $(uname -m)." >&2; exit 1 ;;
esac

step() { echo "==> $1"; }

"$repo_root/scripts/publish-sqzrlite.sh" --rid "$rid"

step "Installing to $install_dir"
publish_dir="$repo_root/artifacts/publish-sqzrlite-$rid"
mkdir -p "$install_dir" "$bin_dir"
cp -R "$publish_dir"/. "$install_dir"/
chmod +x "$install_dir/sqzrlite"
ln -sf "$install_dir/sqzrlite" "$bin_dir/sqzrlite"

path_note="Symlink created: $bin_dir/sqzrlite"
case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) path_note="$bin_dir is not on your PATH - add it to your shell profile, then open a NEW terminal." ;;
esac

cat <<SUMMARY

sqzrlite installed.
  Binary : $install_dir/sqzrlite
  $path_note
Verify:  $bin_dir/sqzrlite --version
SUMMARY
