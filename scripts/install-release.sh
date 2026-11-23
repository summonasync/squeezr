#!/usr/bin/env bash
# One-command installer for machines without a Squeezr checkout:
#   curl -fsSL <raw-url>/scripts/install-release.sh | bash
# Resolves the latest published release (or the tag given via
# --version), downloads the linux-x64 tar.gz, unpacks it into
# ~/apps/squeezr/<tag> and repoints the squeezr symlink in ~/.local/bin.
# When the account has watchd autostart installed, the freshly unpacked build
# is restamped and the autostart entry is rewired onto it and restarted, so an
# upgrade never leaves the supervisor running the previous tag.
# No sudo - everything stays in user-writable directories. To build from
# a source checkout instead, use scripts/install.sh.
#
# Usage: install-release.sh [--version <tag>] [--force]
# Env:   SQUEEZR_RELEASES_BASE - release API repo base (default https://api.github.com/repos/summonasync/squeezr)
#        SQUEEZR_INSTALL_DIR   - versioned install store (default: ~/apps/squeezr)
#        SQUEEZR_BIN_DIR       - symlink directory (default: ~/.local/bin)
set -euo pipefail

repo_api="${SQUEEZR_RELEASES_BASE:-}"
releases_page='https://github.com/summonasync/squeezr/releases'
if [[ -n "$repo_api" ]]; then
  repo_api="${repo_api%/}"
else
  repo_api='https://api.github.com/repos/summonasync/squeezr'
fi

usage() {
  cat <<'USAGE'
Usage: install-release.sh [--version <tag>] [--force]

Installs the Squeezr CLI from a published release into
$SQUEEZR_INSTALL_DIR/<tag> (default ~/apps/squeezr) and repoints the
squeezr symlink in $SQUEEZR_BIN_DIR (default ~/.local/bin).

When watchd autostart is installed for this account, the new build is
restamped and the entry is rewired onto it and restarted; the machine-readable
line 'autostart=<outcome>' closes that step.

Options:
  --version <tag>  install this release tag instead of latest (e.g. v1.0.0)
  --force          re-download even if the tag is already installed
  -h, --help       show this help

Environment:
  SQUEEZR_RELEASES_BASE release API repo base (default https://api.github.com/repos/summonasync/squeezr)
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

uname_s="$(uname -s)"
case "$uname_s" in
  Linux) ;;
  MINGW*|MSYS*|CYGWIN*)
    die "native Windows detected - releases are Linux-only. Build from source with scripts/install.ps1 from the repository."
    ;;
  *) die "unsupported OS $uname_s - this installer covers Linux only" ;;
esac

case "$(uname -m)" in
  x86_64) rid='linux-x64' ;;
  aarch64|arm64)
    die "no linux-arm64 builds are published (linux-x64 only); on arm64 hardware build from source with scripts/install.sh"
    ;;
  *) die "unsupported architecture $(uname -m)" ;;
esac

install_dir="${SQUEEZR_INSTALL_DIR:-$HOME/apps/squeezr}"
bin_dir="${SQUEEZR_BIN_DIR:-$HOME/.local/bin}"
bin_dir="${bin_dir%/}"
mkdir -p "$install_dir" "$bin_dir"
install_dir="$(cd "$install_dir" && pwd)"

# The supervisor identity the rewiring restarts. WatchdUnitGenerator owns both names
# (DefaultUnitName, LaunchAgentUnitName); this installer only names the entry, it never builds its
# text - generation and quoting stay in the CLI, which is the thing being upgraded.
systemd_unit='squeezr-watchd.service'
launch_agent_label='dev.summonasync.squeezr-watchd'
# Set by a restart leg that hands the switch-over to the platform instead of performing it. A flag, not
# a borrowed exit code: the exit code belongs to systemctl and could answer anything, so the caller
# cannot tell a sentinel from a real status - the flag is the only honest signal of a deferred leg.
restart_deferred=0

fetch_json() {
  local url=$1 out=$2 code
  code=$(curl -sS -o "$out" -w '%{http_code}' --max-time 30 "$url") || code=000
  case "$code" in
    200) ;;
    404) die "release not found at $url - published releases are listed on $releases_page" ;;
    *) die "release API returned HTTP $code for $url" ;;
  esac
}

# A published tar carries the archived mtimes, and the autostart entry names the tag directory that
# was current when it got installed, so an upgrade through this script would otherwise leave the
# supervisor on the previous build forever: restamping gives the drift detector something to see, and
# asking the NEW binary to re-register is what actually moves ExecStart into this tag. The restart
# hands the fleet over now rather than at the next logon - the ruling on 110-08 is that an upgrade
# restarts the supervisor, systemd's control group takes the children with it, and the new watchd
# raises them again on the new code.
sync_autostart() {
  local new_bin=$1 report status
  if [[ "$unpacked" -eq 0 ]]; then
    echo "  Autostart : skipped, this run installed no new build"
    echo "autostart=skipped:no-new-build"
    return
  fi

  if ! touch "$new_bin"; then
    echo "install-release: warning: could not restamp $new_bin - the running supervisor will not read this build as newer." >&2
    echo "  Autostart : skipped, the new binary kept its archived mtime"
    echo "autostart=skipped:touch-failed"
    return
  fi

  # The report is read off stdout, not off the exit code: three of the four verdicts share exit 2 and
  # a silent report means the verb did not answer at all, which is not an invitation to guess.
  report="$("$new_bin" watchd installed 2>/dev/null)" || true
  status=$(sed -n 's/^status=//p' <<<"$report" | head -n 1)
  case "$status" in
    not-installed)
      echo "  Autostart : skipped: autostart not installed"
      echo "autostart=skipped:autostart-not-installed"
      ;;
    installed | installed-but-binary-missing | unit-failed | present-but-not-enabled)
      rewire_autostart "$new_bin"
      ;;
    *)
      echo "install-release: note: the autostart probe answered 'status=${status:-nothing}' - the entry is left pointed where it was." >&2
      echo "  Autostart : touched, entry untouched (the platform gave no verdict)"
      echo "autostart=touched"
      ;;
  esac
}

rewire_autostart() {
  local new_bin=$1
  step "Rewiring watchd autostart onto the $tag build"
  if ! "$new_bin" watchd install; then
    echo "install-release: warning: the entry was not rewritten - autostart still starts the previous build." >&2
    echo "install-release:          fix it by hand: $new_bin watchd install" >&2
    echo "autostart=skipped:install-failed"
    return
  fi
  if restart_supervisor; then
    if [[ "$restart_deferred" -eq 1 ]]; then
      echo "autostart=rewritten+restart-deferred"
    else
      echo "  Autostart : entry rewritten onto $new_bin, supervisor restarted"
      echo "autostart=rewritten+restarted"
    fi
  else
    echo "install-release: warning: the entry is rewritten but the supervisor was not restarted - it keeps running the previous build until you say otherwise." >&2
    echo "install-release:          restart it by hand: systemctl --user restart $systemd_unit" >&2
    echo "install-release:          if systemd parked the unit, clear it first: systemctl --user reset-failed $systemd_unit" >&2
    echo "autostart=rewired+restart-failed"
  fi
}

restart_supervisor() {
  case "$uname_s" in
    Linux) systemctl --user restart "$systemd_unit" ;;
    Darwin) launchctl kickstart -k "gui/$(id -u)/$launch_agent_label" ;;
    # No release artifact reaches this arm today: a rewritten scheduled task simply waits for the next
    # logon, and killing the instance that is running now is not this script's call to make. The flag is
    # what lets the caller say deferred instead of claiming a restart this leg never performed.
    *)
      echo "  Autostart : task rewritten, the new binary is picked up at next logon"
      restart_deferred=1
      return 0
      ;;
  esac
}

json="$(mktemp)"
staging=''
incoming=''
link_tmp=''
unpacked=0
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
  unpacked=1
fi

step 'Switching squeezr symlink'
link_tmp="$bin_dir/.squeezr.tmp.$$"
ln -sfn "$target/squeezr" "$link_tmp"
mv -T "$link_tmp" "$bin_dir/squeezr"
link_tmp=''

sync_autostart "$target/squeezr"

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
