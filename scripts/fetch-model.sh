#!/usr/bin/env bash
# Downloads the bge-code-v1 embedding model (Q4-quantized ONNX) and its
# tokenizer files into src/Squeezr.Cli/Models for bundling with the CLI.
# Idempotent: files whose SHA256 already matches are skipped. Every download
# is hash-verified against the pinned values below; a mismatch aborts with
# the offending file deleted.
#
# Source:  disisnoturbusiness/bge-code-v1-onnx @ 27c6fd89
#          (4-bit block-32 weight-only MatMulNBits quantization of BAAI/bge-code-v1,
#          validation report in the source repo: cosine_mean 0.9519 vs fp32)
#
# Usage: scripts/fetch-model.sh [models-dir]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
models_dir="${1:-$repo_root/src/Squeezr.Cli/Models}"

revision='27c6fd890be9078373a45dcc5221e20fcf357553'
base_url="https://huggingface.co/disisnoturbusiness/bge-code-v1-onnx/resolve/$revision"

files=(
  'model_q4.onnx|bge-code-v1.onnx|2e4ca36ddd82f37fa6d33187fc8a24dbdbee3374b38c406aec382c7c87cec2fa'
  'vocab.json|bge-code-v1/vocab.json|ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910'
  'merges.txt|bge-code-v1/merges.txt|8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5'
  'added_tokens.json|bge-code-v1/added_tokens.json|59122dc49ad6df74c3b16a679c55cfd7cdf23760b50a4d14171d66363c5d4899'
)

sha256_of() { sha256sum "$1" | cut -d' ' -f1; }

mkdir -p "$models_dir"

for entry in "${files[@]}"; do
  IFS='|' read -r source target sha256 <<< "$entry"
  out="$models_dir/$target"
  mkdir -p "$(dirname "$out")"

  if [[ -f "$out" ]]; then
    if [[ "$(sha256_of "$out")" == "$sha256" ]]; then
      echo "skip  $target (already present, hash matches)"
      continue
    fi
    echo "hash mismatch for existing $target - re-downloading"
    rm -f "$out"
  fi

  url="$base_url/$source"
  tmp="$out.part"
  echo "get   $target <- $url"
  if ! curl -fsSL --retry 3 -o "$tmp" "$url"; then
    rm -f "$tmp"
    echo "curl failed for $url" >&2
    exit 1
  fi
  actual="$(sha256_of "$tmp")"
  if [[ "$actual" != "$sha256" ]]; then
    rm -f "$tmp"
    echo "SHA256 mismatch for $source. Expected $sha256, got $actual." >&2
    exit 1
  fi
  mv "$tmp" "$out"
  echo "ok    $target"
done

echo "Model ready in $models_dir"
