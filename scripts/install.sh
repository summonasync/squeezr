#!/usr/bin/env bash
# Builds and installs the Squeezr CLI for the current user (Linux).
# Publishes a self-contained single-file build, copies it to the install
# directory and symlinks it into ~/.local/bin. Checks for Docker (needed
# from milestone M4 for Qdrant) and prints setup hints when missing.
#
# Usage: scripts/install.sh [install-dir]
# Env:   SQUEEZR_RID - override the runtime identifier (default: auto-detect).
set -euo pipefail

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "This installer is Linux-only (got $(uname -s)). On Windows use scripts/install.ps1." >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
install_dir="${1:-$HOME/.local/share/squeezr}"
bin_dir="$HOME/.local/bin"
publish_dir="$repo_root/artifacts/publish-linux"

if [[ -n "${SQUEEZR_RID:-}" ]]; then
  rid="$SQUEEZR_RID"
else
  case "$(uname -m)" in
    x86_64) rid='linux-x64' ;;
    aarch64|arm64) rid='linux-arm64' ;;
    *) echo "Unsupported architecture $(uname -m); set SQUEEZR_RID manually." >&2; exit 1 ;;
  esac
fi

step() { echo "==> $1"; }

step "Publishing self-contained $rid single-file build"
dotnet publish "$repo_root/src/Squeezr.Cli/Squeezr.Cli.csproj" \
  -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$publish_dir"

step "Installing to $install_dir"
mkdir -p "$install_dir" "$bin_dir"
# MSBuildWorkspace ships out-of-proc BuildHost folders next to the exe; they are required for M3 semantic indexing.
cp -R "$publish_dir"/. "$install_dir"/
chmod +x "$install_dir/squeezr"
ln -sf "$install_dir/squeezr" "$bin_dir/squeezr"

path_note="Symlink created: $bin_dir/squeezr"
case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) path_note="$bin_dir is not on your PATH - add it to your shell profile, then open a NEW terminal." ;;
esac

if command -v docker >/dev/null 2>&1; then
  step 'Docker detected - Qdrant (from M4) will be available'
else
  cat <<'HINT' >&2
[!] Docker not found. Squeezr works without it (trigram + symbol layers),
    but semantic search (milestone M4+) needs Qdrant in Docker.
    Install Docker Engine: https://docs.docker.com/engine/install/
HINT
fi

cat <<SUMMARY

Squeezr installed.
  Binary : $install_dir/squeezr
  $path_note
Verify:  $bin_dir/squeezr --help
SUMMARY
