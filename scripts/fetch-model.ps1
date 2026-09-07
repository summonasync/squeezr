#Requires -Version 5.1
<#
.SYNOPSIS
  Downloads the bge-code-v1 embedding model (Q4-quantized ONNX) and its
  tokenizer files into src\Squeezr.Cli\Models for bundling with the CLI.
.DESCRIPTION
  Idempotent: files whose SHA256 already matches are skipped. Every download
  is hash-verified against the pinned values below; a mismatch aborts with
  the offending file deleted.

  Source:  disisnoturbusiness/bge-code-v1-onnx @ 27c6fd89
           (4-bit block-32 weight-only MatMulNBits quantization of BAAI/bge-code-v1,
           validation report in the source repo: cosine_mean 0.9519 vs fp32)
.EXAMPLE
  pwsh -File scripts\fetch-model.ps1
#>
[CmdletBinding()]
param(
    [string]$ModelsDir
)

if ([string]::IsNullOrEmpty($ModelsDir)) {
    $ModelsDir = Join-Path $PSScriptRoot '..\src\Squeezr.Cli\Models'
}

$ErrorActionPreference = 'Stop'

$revision = '27c6fd890be9078373a45dcc5221e20fcf357553'
$baseUrl = "https://huggingface.co/disisnoturbusiness/bge-code-v1-onnx/resolve/$revision"

$files = @(
    @{ Source = 'model_q4.onnx';     Target = 'bge-code-v1.onnx';              Sha256 = '2e4ca36ddd82f37fa6d33187fc8a24dbdbee3374b38c406aec382c7c87cec2fa' },
    @{ Source = 'vocab.json';        Target = 'bge-code-v1\vocab.json';        Sha256 = 'ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910' },
    @{ Source = 'merges.txt';        Target = 'bge-code-v1\merges.txt';        Sha256 = '8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5' },
    @{ Source = 'added_tokens.json'; Target = 'bge-code-v1\added_tokens.json'; Sha256 = '59122dc49ad6df74c3b16a679c55cfd7cdf23760b50a4d14171d66363c5d4899' }
)

function Get-FileHash256([string]$path) {
    (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
}

function Save-File([string]$url, [string]$outFile) {
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & curl.exe -fsSL --retry 3 -o $outFile $url
        if ($LASTEXITCODE -ne 0) { throw "curl failed ($LASTEXITCODE) for $url" }
    } else {
        Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing
    }
}

New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null

foreach ($file in $files) {
    $target = Join-Path $ModelsDir $file.Target
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null

    if (Test-Path $target) {
        if ((Get-FileHash256 $target) -eq $file.Sha256) {
            Write-Host "skip  $($file.Target) (already present, hash matches)" -ForegroundColor DarkGray
            continue
        }
        Write-Host "hash mismatch for existing $($file.Target) - re-downloading" -ForegroundColor Yellow
        Remove-Item $target -Force
    }

    $url = "$baseUrl/$($file.Source)"
    $tmp = "$target.part"
    try {
        Write-Host "get   $($file.Target) <- $url" -ForegroundColor Cyan
        Save-File $url $tmp
        if ((Get-FileHash256 $tmp) -ne $file.Sha256) {
            throw "SHA256 mismatch for $($file.Source). Expected $($file.Sha256)."
        }
        Move-Item $tmp $target
        Write-Host "ok    $($file.Target)" -ForegroundColor Green
    } catch {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        throw
    }
}

Write-Host "Model ready in $ModelsDir" -ForegroundColor Green
