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
#   pwsh validation/ForwardCompat/run-forwardcompat.ps1 -Versions 3.8.2.1105,3.8.5 -Tolerance 4
#   pwsh validation/ForwardCompat/run-forwardcompat.ps1 -SkipBaseline
#
# THE MATRIX IS THE FULL PROVEN RANGE, NOT ONE ANCHOR VERSION. The default below is
# every MonoGame release whose Effect loader accepts MGFX v10, measured 2026-07-28 by
# running this harness against all stable DesktopGL releases:
#
#   3.8.0.1641  REJECTS - "This MGFX effect seems to be for a newer release of
#               MonoGame" (0/10). Its loader predates MGFX v10. This is the honest
#               floor-minus-one, not a defect.
#   3.8.1.263 .. 3.8.5   ALL LOAD + RENDER 10/10.
#
# So the first entry is the OLDEST SUPPORTED runtime (3.8.1.263), and it is the
# forward-compat reference every other cell must match pixel-for-pixel. Do not
# confuse it with Directory.Packages.props' MonoGame version - that is only the
# default the other GL harnesses render against, not a product commitment. The
# product commitment is the OUTPUT FORMAT (MGFX v10), and this matrix is what
# proves its range.
#
# Extending the matrix: append the NuGet version string to -Versions when a new
# MonoGame ships. Prepending below 3.8.1.263 will fail for the real reason above.
#
# Requires: a real GPU/DesktopGL context (rung-4 render, like Phase 17/33/34),
#           Python with pillow + numpy for the pixel compare.

param(
    [string[]]$Versions = @("3.8.1.263", "3.8.1.303", "3.8.2.1105", "3.8.3", "3.8.4", "3.8.4.1", "3.8.5"),
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

# The mgfxc goldens are rendered on the centrally-pinned MonoGame, so each version
# cell is held to the same bar as the original Phase 17 candidate-vs-mgfxc check.
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
