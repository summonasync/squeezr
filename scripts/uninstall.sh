#!/usr/bin/env bash
# Removes the Squeezr CLI installation for the current user (Linux).
# Deletes the install directory and the ~/.local/bin/squeezr symlink.
# The central store ~/.squeezr is NOT touched: it holds your workspaces
# (workspaces/<storeKey>/), embedding models (models/) and the managed
# infra compose file (infra/). Data survives the uninstall. If you really
# want it gone: run 'squeezr drop' per workspace and 'squeezr services down'
# first, then remove ~/.squeezr manually.
#
# Usage: scripts/uninstall.sh [install-dir]
set -euo pipefail

install_dir="${1:-$HOME/.local/share/squeezr}"
bin_link="$HOME/.local/bin/squeezr"
store_dir="$HOME/.squeezr"

rm -f "$bin_link"
if [[ -d "$install_dir" ]]; then
  rm -rf "$install_dir"
  echo "Removed $install_dir and the $bin_link symlink."
else
  echo "Symlink removed; $install_dir did not exist."
fi
if [[ -d "$store_dir" ]]; then
  echo "Note: the central store $store_dir (workspaces, models, infra) was left untouched."
  echo "      Remove it manually only if you no longer need the data: rm -rf $store_dir"
fi
