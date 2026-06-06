#!/usr/bin/env pwsh
# Phase 35 Area A — forward-compat VERSION-MATRIX regression guard (re-runnable).
#
# Renders the SM3 corpus from ShadowDusk's UNCHANGED v10 .mgfx output on EACH
# MonoGame version in the matrix, then proves:
#   1. forward-compat — every version is pixel-identical to the floor (3.8.2.1105),
#   2. fidelity       — every version is within tolerance of the mgfxc goldens.
# The product is never changed: the newer MonoGame is pulled in per-run via
# -p:ForwardCompatMonoGameVersion=<v> (VersionOverride); the pin stays 3.8.2.1105.
#
# Exit code: 0 = matrix holds; non-zero = a render failed or images diverged.
#
# Usage (from anywhere):
#   pwsh validation/ForwardCompat/run-forwardcompat.ps1
#   pwsh validation/ForwardCompat/run-forwardcompat.ps1 -Versions 3.8.2.1105,3.8.4.1 -Tolerance 4
#   pwsh validation/ForwardCompat/run-forwardcompat.ps1 -SkipBaseline
#
# Extending the matrix: add the NuGet version string to -Versions (e.g. a future
# 3.8.5 stable). The first entry is the forward-compat reference floor and MUST
# stay 3.8.2.1105 (the product's compat promise).
#
# Requires: a real GPU/DesktopGL context (rung-4 render, like Phase 17/33/34),
#           Python with pillow + numpy for the pixel compare.

param(
    [string[]]$Versions = @("3.8.2.1105", "3.8.4.1"),
    [int]$Tolerance = 4,
    [switch]$SkipBaseline
)

$ErrorActionPreference = "Stop"
$validationDir = Split-Path -Parent $PSScriptRoot   # ...\validation
$repoMatrix   = Join-Path $validationDir "ForwardCompat\ForwardCompat.csproj"
$repoBaseline = Join-Path $validationDir "Baseline\Baseline.csproj"
$compare      = Join-Path $validationDir "compare_forwardcompat.py"

# Render one matrix cell: build+run the matrix project against a specific MonoGame
# version and tag the run so its PNGs land in output/versionmatrix/<version>/.
function Invoke-MatrixCell($version) {
    Write-Host "==> rendering matrix cell: ShadowDusk v10 -> MonoGame $version" -ForegroundColor Cyan
    $env:MATRIX_VERSION_LABEL = $version
    try {
        & dotnet run --project $repoMatrix -c Debug -p:ForwardCompatMonoGameVersion=$version
        if ($LASTEXITCODE -ne 0) {
            throw "matrix cell $version reported failures (exit $LASTEXITCODE)"
        }
    }
    finally {
        Remove-Item Env:\MATRIX_VERSION_LABEL -ErrorAction SilentlyContinue
    }
}

foreach ($v in $Versions) {
    Invoke-MatrixCell $v
}

# mgfxc goldens on 3.8.2.1105, so each version cell is held to the same bar as the
# original Phase 17 candidate-vs-mgfxc comparison.
$compareArgs = @("--versions") + $Versions + @("--tolerance", "$Tolerance")
if (-not $SkipBaseline) {
    Write-Host "==> rendering Baseline (mgfxc goldens -> MonoGame 3.8.2.1105)" -ForegroundColor Cyan
    & dotnet run --project $repoBaseline -c Debug
    if ($LASTEXITCODE -ne 0) { throw "Baseline render harness reported failures (exit $LASTEXITCODE)" }
    $compareArgs += "--vs-baseline"
}

Write-Host "==> pixel compare (version matrix)" -ForegroundColor Cyan
& python $compare @compareArgs
$cmp = $LASTEXITCODE
if ($cmp -ne 0) {
    Write-Host "VERSION-MATRIX REGRESSION: renders diverged across MonoGame versions." -ForegroundColor Red
    exit $cmp
}
Write-Host "VERSION-MATRIX OK: v10 .mgfx renders identically across $($Versions -join ', ')." -ForegroundColor Green
exit 0
