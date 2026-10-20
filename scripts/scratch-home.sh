#!/usr/bin/env bash
# Pins SQUEEZR_HOME to a fresh temp dir for a scratch squeezr harness and removes it when the harness
# exits. Source it from ANY scratch flow that creates workspaces — a repo script or a throwaway probe
# project outside the repo — before its first `squeezr init` / `squeezr add`:
#
#   source /path/to/scratch-home.sh || exit 1
#   squeezr init
#
# Install your own EXIT trap BEFORE sourcing: this helper chains a trap that was already set, while a
# trap installed after sourcing silently replaces our cleanup and the scratch home stays forever.
#
# Harnesses that manage SQUEEZR_HOME themselves can run the fail-loud check instead (exit 1 with the
# fix in the message when the run would touch the live home):
#
#   /path/to/scratch-home.sh --assert
#
# Why pin: every store path resolves through the central registry ($SQUEEZR_HOME/workspaces.json), so an
# unpinned probe registers its temp root in the live registry and the row outlives the directory it
# points at — 2026-10-03 one unpinned probe took workspaces.json from 5 live rows to 126 / 121 stale.
#
# Why also check the directory the run sits in: the pin alone is not enough. WorkspaceStore.ResolveUpward
# climbs from the canonical cwd and migrates every ancestor holding a .squeezr directory or a
# squeezr.workspace.json manifest into SqueezrHome.Resolve() — while the override is active that is the
# pinned temp home, so the live store becomes the payload this helper's EXIT cleanup deletes. That is how
# ~/.squeezr was lost on 2026-10-19. The check walks the whole ancestor chain rather than comparing the
# cwd against $HOME, because a store in any nearer ancestor is the same weapon and a symlinked spelling of
# one directory is one directory to the product. Resolve climbs twice: first from the canonical cwd and,
# when that chain names no workspace, again from CLAUDE_PROJECT_DIR when it is set and exists. A guard
# mirroring only the first climb lets a run with a clean cwd chain migrate the store sitting above the
# project directory, so both starts are walked, in the product's order. Feature 071 is the product-side
# guard; until it ships this helper refuses the combination.

_squeezr_scratch_canonical() {
  local resolved
  resolved="$(cd -- "$1" 2>/dev/null && pwd -P)" || resolved="$1"
  printf '%s\n' "$resolved"
}

# What SqueezrHome.Resolve() answers for this process: the override when one is set, else $HOME/.squeezr.
# An unset HOME resolves to a root-level candidate no walk can match, so the function stays total instead
# of holding on the caller's promise that HOME is non-empty.
_squeezr_scratch_central_store() {
  if [[ -n "${SQUEEZR_HOME:-}" ]]; then
    _squeezr_scratch_canonical "$SQUEEZR_HOME"
  else
    local home="${HOME:-}"
    _squeezr_scratch_canonical "${home%/}/.squeezr"
  fi
}

# The HasLegacyLayout walk, mirrored on the shell side: the run root itself and every ancestor above it.
# Prints the first directory holding a store the product would consider legacy, and stays silent when the
# chain is clean. <central> is the store that will be central for this run: pass nothing where the pin is
# about to replace it with a fresh temp dir, because then no store that already exists can be it.
_squeezr_scratch_store_ancestor() {
  local dir="$1" central="${2:-}" parent store
  while :; do
    if [[ -f "$dir/squeezr.workspace.json" ]]; then
      printf '%s\n' "$dir"
      return 0
    fi
    if [[ -d "$dir/.squeezr" ]]; then
      store="$(_squeezr_scratch_canonical "$dir/.squeezr")"
      if [[ "$store" != "$central" ]]; then
        printf '%s\n' "$dir"
        return 0
      fi
    fi
    parent="$(dirname -- "$dir")"
    [[ "$parent" != "$dir" ]] || return 1
    dir="$parent"
  done
}

# Both entry points decide with one predicate; only the store they compare against differs. An empty HOME
# is a refusal and not a pass: without it the live store cannot be named, which is exactly the blind spot
# that let the 2026-10-19 run through. A run root that cannot be named is a refusal for the same reason —
# an empty pwd would make the walk blind instead of loud. The two climbs are checked in Resolve's order
# and the second one only fires when the first chain came back clean, which is when the product uses it.
_squeezr_scratch_guard_run_root() {
  local reason='' cwd cpd ancestor
  if [[ -z "${HOME:-}" ]]; then
    reason='HOME is unset or empty, so the live central store cannot be named'
  else
    cwd="$(pwd -P 2>/dev/null)"
    if [[ -z "$cwd" ]]; then
      reason='the run root cannot be resolved, so no store on its ancestor chain can be named'
    elif ancestor="$(_squeezr_scratch_store_ancestor "$cwd" "${1:-}")"; then
      reason="'$cwd' sits under a directory holding a live store: $ancestor"
    elif [[ -n "${CLAUDE_PROJECT_DIR:-}" && -d "${CLAUDE_PROJECT_DIR:-}" ]]; then
      cpd="$(_squeezr_scratch_canonical "${CLAUDE_PROJECT_DIR:-}")"
      if ancestor="$(_squeezr_scratch_store_ancestor "$cpd" "${1:-}")"; then
        reason="'$cpd' (CLAUDE_PROJECT_DIR) sits under a directory holding a live store: $ancestor"
      fi
    fi
  fi

  if [[ -n "$reason" ]]; then
    echo "scratch-home.sh: refusing — $reason." >&2
    echo "A pinned SQUEEZR_HOME makes squeezr migrate what it finds there into the pinned home, and the" >&2
    echo "EXIT cleanup in this file then deletes the moved live store — the loss of ~/.squeezr on 2026-10-19." >&2
    echo "Fix: no .squeezr/ or squeezr.workspace.json anywhere on the ancestor chain of the run's root, or" >&2
    echo "on the ancestor chain of \$CLAUDE_PROJECT_DIR when it is set; run scratch flows from a root" >&2
    echo "outside \$HOME until the store-relocation guard ships (071)." >&2
    return 1
  fi
}

squeezr_scratch_assert() {
  if [[ -z "${SQUEEZR_HOME:-}" ]]; then
    echo "scratch-home.sh: SQUEEZR_HOME is unset; a scratch run would write \$HOME/.squeezr." >&2
    echo "Fix: source scratch-home.sh before the first 'squeezr init'/'squeezr add'," >&2
    echo "or export SQUEEZR_HOME=\"\$(mktemp -d)\" yourself." >&2
    return 1
  fi

  if [[ -n "${HOME:-}" ]]; then
    local live_home pinned
    live_home="$(_squeezr_scratch_canonical "${HOME%/}/.squeezr")"
    pinned="$(_squeezr_scratch_canonical "$SQUEEZR_HOME")"
    if [[ "$pinned" == "$live_home" ]]; then
      echo "scratch-home.sh: SQUEEZR_HOME points at the live home ($live_home)." >&2
      echo "Fix: source scratch-home.sh (pins a fresh temp home and cleans it on exit)" >&2
      echo "or export SQUEEZR_HOME=\"\$(mktemp -d)\" before the scratch run." >&2
      return 1
    fi
  fi

  _squeezr_scratch_guard_run_root "$(_squeezr_scratch_central_store)" || return 1
}

# Only when this file is the one bash started: sourced into a harness, $1 is the harness's own first
# argument, and a harness invoked as `bash harness.sh --assert` would run the check instead of pinning —
# exiting 0 with its body never executed, which reads as a green verification that verified nothing.
if [[ "${1:-}" == "--assert" && "${BASH_SOURCE[0]}" == "$0" ]]; then
  squeezr_scratch_assert
  exit $?
fi

if [[ -n "${SQUEEZR_SCRATCH_HOME:-}" ]]; then
  if [[ "${SQUEEZR_HOME:-}" == "$SQUEEZR_SCRATCH_HOME" && -d "$SQUEEZR_SCRATCH_HOME" ]]; then
    return 0
  fi
  echo "scratch-home.sh: SQUEEZR_SCRATCH_HOME is set but SQUEEZR_HOME does not match it; re-pinning." >&2
fi

# A failed pin has to be fatal for a harness: `return 1` from a sourced file leaves the caller running
# with no pin at all, and that is the shape that wrote into the live home. An interactive shell gets the
# same refusal with its terminal intact.
if ! _squeezr_scratch_guard_run_root; then
  if [[ $- != *i* ]]; then
    exit 1
  fi
  return 1
fi

SQUEEZR_SCRATCH_HOME="$(mktemp -d "${TMPDIR:-/tmp}/squeezr-scratch-home.XXXXXX")" || {
  echo "scratch-home.sh: mktemp -d failed, nothing was pinned; continuing would write the live home." >&2
  echo "Fix: check that TMPDIR (${TMPDIR:-/tmp}) is writable, or export SQUEEZR_HOME=\"\$(mktemp -d)\"." >&2
  if [[ $- != *i* ]]; then
    exit 1
  fi
  return 1
}
export SQUEEZR_HOME="$SQUEEZR_SCRATCH_HOME"
export SQUEEZR_SCRATCH_HOME

# Frozen here and never read back from the exported variable: a caller that repoints SQUEEZR_SCRATCH_HOME
# must not be able to aim this removal at a directory the helper did not create.
_SQUEEZR_SCRATCH_FROZEN="$SQUEEZR_SCRATCH_HOME"

_squeezr_scratch_remove_home() {
  local target="$_SQUEEZR_SCRATCH_FROZEN"
  case "$target" in
    */squeezr-scratch-home.??????) ;;
    *)
      echo "scratch-home.sh: cleanup skipped — '$_SQUEEZR_SCRATCH_FROZEN' is not a mktemp scratch home." >&2
      return 0
      ;;
  esac
  if [[ -n "${HOME:-}" && ( "$target" == "${HOME%/}" || "$target" == "${HOME%/}/"* ) ]]; then
    echo "scratch-home.sh: cleanup skipped — the scratch home is inside \$HOME, leaving it for inspection." >&2
    return 0
  fi
  if [[ -d "$target" ]]; then
    rm -rf "$target"
  fi
}

# The chained handler is kept as text and run at top level. trap -p prints the body wrapped in outer
# single quotes (inner ones escaped as '\''); assigning through eval is what turns that printout back
# into the original text, and evaluating the result inside a function would replace the caller's $1..$9
# with the function's (empty) arguments, silently killing any handler that reads them.
_SQUEEZR_SCRATCH_PRIOR=''
_SQUEEZR_SCRATCH_SPEC="$(trap -p EXIT)"
if [[ -n "$_SQUEEZR_SCRATCH_SPEC" ]]; then
  _SQUEEZR_SCRATCH_CMD="${_SQUEEZR_SCRATCH_SPEC#trap -- }"
  _SQUEEZR_SCRATCH_CMD="${_SQUEEZR_SCRATCH_CMD% EXIT}"
  eval "_SQUEEZR_SCRATCH_PRIOR=$_SQUEEZR_SCRATCH_CMD"
fi

if [[ -n "$_SQUEEZR_SCRATCH_PRIOR" ]]; then
  # Caller first: a handler that archives out of the scratch home must still see it. If the caller's
  # handler exits, only this temp cleanup is skipped — the smaller loss of the two orders.
  trap 'eval "$_SQUEEZR_SCRATCH_PRIOR"
_squeezr_scratch_remove_home' EXIT
else
  trap _squeezr_scratch_remove_home EXIT
fi
