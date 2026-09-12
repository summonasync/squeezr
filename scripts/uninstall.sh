#!/usr/bin/env bash
# Removes the Squeezr CLI installation for the current user (Linux).
# Deletes the install directory and the ~/.local/bin/squeezr symlink.
# Workspaces (.squeezr folders inside your projects) are NOT touched -
# use `squeezr drop` per workspace before uninstalling if you want them gone.
#
# Usage: scripts/uninstall.sh [install-dir]
set -euo pipefail

install_dir="${1:-$HOME/.local/share/squeezr}"
bin_link="$HOME/.local/bin/squeezr"

rm -f "$bin_link"
if [[ -d "$install_dir" ]]; then
  rm -rf "$install_dir"
  echo "Removed $install_dir and the $bin_link symlink."
else
  echo "Symlink removed; $install_dir did not exist."
fi
