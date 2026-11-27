#!/usr/bin/env bash
# Reproducible publish of sqzrlite, the index-only lite CLI (Squeezr.Lite).
# Produces one self-contained single-file binary plus the native tree-sitter
# libraries and the MSBuildWorkspace BuildHost folders it loads from its own
# directory.
#
# The pinned shape is NO trim and NO NativeAOT. Both were measured (2026-11-27,
# dotnet 10.0.301, linux-x64) and both hard-fail on the lite dependency graph:
#
#   PublishAot=true
#     warning IL3050 on Spectre.Console.Cli's CommandApp ctor ("relies on
#     reflection. Use during trimming and AOT compilation is not supported").
#     The produced binary aborted on EVERY invocation, including --version:
#     Spectre.Console.Cli.CommandRuntimeException: Could not get settings type
#     for command of type 'Squeezr.Lite.Commands.InitCommand' (exit 134).
#     The ILC log also proved the semantic layer dead before runtime:
#     "Method 'MSBuildWorkspace.OpenProjectAsync/OpenSolutionAsync' will always
#     throw because: Failed to load assembly 'Microsoft.Build.Framework'" -
#     the Microsoft.Build packages are ExcludeAssets=runtime and are resolved
#     at run time by MSBuildLocator, which NativeAOT cannot do.
#
#   PublishTrimmed=true
#     Same runtime death (CommandRuntimeException, exit 134) with warning
#     IL2104 "Assembly 'Spectre.Console.Cli' produced trim warnings": the
#     trimmer removes the command settings types Spectre resolves by
#     reflection. LiteDB and the reflection-based System.Text.Json writers in
#     Lite sit on the same graph.
#
# ReadyToRun stays on: it is the startup win without any of the above risk.
# Compression is OFF by default. Measured on the T5 box (load ~2-4, 32 cores):
# compressed 95.6 MB, map cold 0.55-0.57 s, serve-to-first-answer 0.65 s;
# uncompressed 218 MB, map cold 0.17-0.18 s, serve-to-first-answer 0.26 s.
# sqzrlite is invoked per call by agents, so startup wins; pass --compress
# when download size matters more than every-call latency.
#
# Usage: scripts/publish-sqzrlite.sh [--rid <rid>] [--compress] [--pack]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid=''
compress=false
pack=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) [[ $# -ge 2 ]] || { echo "publish-sqzrlite: --rid requires an argument" >&2; exit 1; }; rid="$2"; shift 2 ;;
    --compress) compress=true; shift ;;
    --pack) pack=true; shift ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "publish-sqzrlite: unknown option: $1" >&2; exit 1 ;;
  esac
done

if [[ -z "$rid" ]]; then
  case "$(uname -m)" in
    x86_64) rid='linux-x64' ;;
    aarch64|arm64) rid='linux-arm64' ;;
    *) echo "Unsupported architecture $(uname -m); pass --rid explicitly." >&2; exit 1 ;;
  esac
fi

publish_dir="$repo_root/artifacts/publish-sqzrlite-$rid"
step() { echo "==> $1"; }

step "Publishing self-contained $rid single-file build (R2R, no trim, no AOT)"
rm -rf "$publish_dir"
compress_flag=false
$compress && compress_flag=true
dotnet publish "$repo_root/src/Squeezr.Lite/Squeezr.Lite.csproj" \
  -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishReadyToRun=true \
  -p:EnableCompressionInSingleFile=$compress_flag \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$publish_dir"

exe_name='sqzrlite'
[[ "$rid" == win-* ]] && exe_name='sqzrlite.exe'
[[ -x "$publish_dir/$exe_name" ]] || { echo "publish produced no $exe_name in $publish_dir" >&2; exit 1; }

if $pack; then
  archive="$repo_root/artifacts/sqzrlite-$rid.tar.gz"
  step "Packing $archive"
  tar -czf "$archive" -C "$publish_dir" .
  echo "Packed: $archive"
fi

echo "Published: $publish_dir"
ls -la "$publish_dir/$exe_name" | awk '{print "  binary:", $5, "bytes"}'
