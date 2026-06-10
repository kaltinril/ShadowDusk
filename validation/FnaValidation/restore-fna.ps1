# restore-fna.ps1 — restore the FNA validation harness's external dependencies
# (idempotent: each step is skipped when its output is already present).
#
#   1. FNA source (the real consumer runtime) — cloned at a PINNED release tag with
#      submodules (SDL3-CS, FNA3D + MojoShader, FAudio, ...). FNA 24.01+ uses SDL3.
#   2. fnalibs (native FNA3D/SDL3/FAudio/theorafile for win-x64) — since 2025 these are
#      distributed as the 'fnalibs' artifact of the FNA-XNA/fnalibs-dailies CI workflow
#      (the old https://fna.flibitijibibo.com/archive/fnalibs.tar.bz2 URL is gone, 404).
#      Downloading a GitHub Actions artifact requires an authenticated `gh` CLI.
#
# Everything lands under ./external/ which is gitignored (validation/.gitignore):
# restored, never committed.

[CmdletBinding()]
param(
    # The FNA release tag to pin. 26.06 = latest release at harness creation (2026-06).
    [string] $FnaTag = '26.06',

    # Optional explicit fnalibs-dailies run id. Default: the most recent successful
    # 'CI' run on main. (Artifacts expire after ~90 days, so a fixed id would rot;
    # the harness run that produced the Phase 39 rung-3/4 evidence used run 27180614567.)
    [string] $FnalibsRunId = ''
)

$ErrorActionPreference = 'Stop'
$external = Join-Path $PSScriptRoot 'external'
New-Item -ItemType Directory -Force $external | Out-Null

# ---------------------------------------------------------------------------
# 1. FNA @ $FnaTag
# ---------------------------------------------------------------------------
$fnaDir = Join-Path $external 'FNA'
if (Test-Path (Join-Path $fnaDir 'FNA.Core.csproj')) {
    Write-Host "[restore-fna] FNA already present at $fnaDir (skipping clone)"
} else {
    Write-Host "[restore-fna] cloning FNA @ $FnaTag ..."
    git clone --recursive --depth 1 --branch $FnaTag https://github.com/FNA-XNA/FNA $fnaDir
    if ($LASTEXITCODE -ne 0) { throw "git clone of FNA @ $FnaTag failed" }
}

# ---------------------------------------------------------------------------
# 2. fnalibs (win-x64 natives) from FNA-XNA/fnalibs-dailies
# ---------------------------------------------------------------------------
$fnalibsDir = Join-Path $external 'fnalibs'
if (Test-Path (Join-Path $fnalibsDir 'x64\FNA3D.dll')) {
    Write-Host "[restore-fna] fnalibs already present at $fnalibsDir (skipping download)"
} else {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "fnalibs download needs the GitHub CLI ('gh', authenticated) — fnalibs are CI artifacts of FNA-XNA/fnalibs-dailies."
    }
    if ([string]::IsNullOrEmpty($FnalibsRunId)) {
        Write-Host "[restore-fna] resolving latest successful fnalibs-dailies CI run ..."
        $FnalibsRunId = gh run list --repo FNA-XNA/fnalibs-dailies --workflow CI --branch main `
            --status success --limit 1 --json databaseId --jq '.[0].databaseId'
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($FnalibsRunId)) {
            throw 'could not resolve a successful fnalibs-dailies CI run via gh'
        }
    }
    Write-Host "[restore-fna] downloading 'fnalibs' artifact from fnalibs-dailies run $FnalibsRunId ..."
    gh run download $FnalibsRunId --repo FNA-XNA/fnalibs-dailies --name fnalibs --dir $fnalibsDir
    if ($LASTEXITCODE -ne 0) { throw "gh run download failed for run $FnalibsRunId" }
    if (-not (Test-Path (Join-Path $fnalibsDir 'x64\FNA3D.dll'))) {
        throw "fnalibs artifact did not contain x64\FNA3D.dll — layout changed upstream?"
    }
}

Write-Host "[restore-fna] done. Build & run with:  dotnet run -c Release  (from validation/FnaValidation)"
