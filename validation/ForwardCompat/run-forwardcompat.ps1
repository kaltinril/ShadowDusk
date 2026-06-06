#!/usr/bin/env pwsh
# Phase 35 Area A — forward-compat regression guard (re-runnable).
#
# Proves ShadowDusk's existing v10 GL .mgfx still loads + renders pixel-equivalent
# on a NEWER MonoGame (3.8.4.1) as on the product-pinned 3.8.2.1105, with the
# product unchanged. Renders three sets on this machine and pixel-compares them.
#
# Exit code: 0 = forward-compat holds; non-zero = a render failed or images diverged.
#
# Usage (from anywhere):
#   pwsh validation/ForwardCompat/run-forwardcompat.ps1
#   pwsh validation/ForwardCompat/run-forwardcompat.ps1 -Tolerance 4 -SkipBaseline
#
# Requires: a real GPU/DesktopGL context (this is a rung-4 render, like Phase 17/33/34),
#           Python with pillow + numpy for the pixel compare.

param(
    [int]$Tolerance = 4,
    [switch]$SkipBaseline
)

$ErrorActionPreference = "Stop"
$validationDir = Split-Path -Parent $PSScriptRoot   # ...\validation
$repoCandidate = Join-Path $validationDir "Candidate\Candidate.csproj"
$repoForward   = Join-Path $validationDir "ForwardCompat\ForwardCompat.csproj"
$repoBaseline  = Join-Path $validationDir "Baseline\Baseline.csproj"
$compare       = Join-Path $validationDir "compare_forwardcompat.py"

function Invoke-Render($csproj, $label) {
    Write-Host "==> rendering $label" -ForegroundColor Cyan
    & dotnet run --project $csproj -c Debug
    if ($LASTEXITCODE -ne 0) {
        throw "$label render harness reported failures (exit $LASTEXITCODE)"
    }
}

# 1. ShadowDusk v10 bytes on the product-pinned 3.8.2.1105 (the reference).
Invoke-Render $repoCandidate "Candidate (ShadowDusk v10 -> MonoGame 3.8.2.1105)"

# 2. The SAME ShadowDusk v10 bytes on the NEWER 3.8.4.1.
Invoke-Render $repoForward "ForwardCompat (ShadowDusk v10 -> MonoGame 3.8.4.1)"

# 3. (optional) mgfxc goldens on 3.8.2.1105, so the forward run is held to the
#    same bar as the original Phase 17 candidate-vs-mgfxc comparison.
$compareArgs = @("--tolerance", "$Tolerance")
if (-not $SkipBaseline) {
    Invoke-Render $repoBaseline "Baseline (mgfxc goldens -> MonoGame 3.8.2.1105)"
    $compareArgs += "--vs-baseline"
}

Write-Host "==> pixel compare" -ForegroundColor Cyan
& python $compare @compareArgs
$cmp = $LASTEXITCODE
if ($cmp -ne 0) {
    Write-Host "FORWARD-COMPAT REGRESSION: images diverged on the newer MonoGame." -ForegroundColor Red
    exit $cmp
}
Write-Host "FORWARD-COMPAT OK: v10 .mgfx renders identically on MonoGame 3.8.4.1." -ForegroundColor Green
exit 0
