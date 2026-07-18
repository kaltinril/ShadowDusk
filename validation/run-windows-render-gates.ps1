#requires -Version 5.1
<#
.SYNOPSIS
  Run the Windows-GPU render-validation gates that CI CANNOT run, and fail loudly if any
  render diverges from the reference compiler.

.DESCRIPTION
  ShadowDusk's real product bar is "loads + renders like the reference compiler in the REAL
  engine" (CLAUDE.md -> "Validation render drivers are the real bar"). The OpenGL render gates
  now run in CI on Linux via Mesa llvmpipe (`.github/workflows/validation-render.yml`), but the
  DirectX / FNA / KNI-DirectX render proofs have NO headless software driver we can run on a
  GitHub runner (Mesa is GL-only; a verified headless D3D/WARP story does not exist yet). So
  those gates can only run on a real Windows box with a GPU - which means the DEVELOPER'S
  machine is the gate, and it must be run BEFORE a release (and before merging any change that
  touches shader output / transpilation / the MGFX-FNA writers / render state / matrix handling).

  This script is that gate, in one command. It runs each Windows-GPU render driver, checks its
  self-asserting exit code (every driver compares ShadowDusk's output against the reference
  compiler - mgfxc / fxc - and exits non-zero on any over-tolerance pixel), aggregates the
  results, and exits non-zero if ANY gate failed. A green run is the evidence a release needs
  that CI cannot provide.

  Gates (all default ON except FNA):
    * MonoGame WindowsDX corpus  - validation/BaselineDx + CandidateDx + compare_dx.py
                                   (ShadowDusk DX vs mgfxc DX golden, real MonoGame WindowsDX).
    * DX modern features (VTF)   - validation/DxModernFeatures (vkd3d vs fxc oracle, maxd 0).
    * KNI DirectX                - validation/KniWinFormsDX (ShadowDusk DX vs mgfxc, real KNI
                                   WinForms.DX11).
    * FNA fx_2_0 (-IncludeFna)   - validation/FnaValidation (vs fxc /T fx_2_0). OPT-IN because
                                   its restore-fna.ps1 clones the FNA source tree (heavy) and the
                                   oracle needs the Windows SDK fxc. Run it for any release that
                                   could affect the FNA target.
    * Vulkan (-IncludeVulkan)    - validation/CandidateVulkan (ShadowDusk's own Vulkan .mgfx
                                   loaded in real MonoGame DesktopVK). OPT-IN because DesktopVK
                                   needs a real Vulkan-capable GPU. NOTE: this asserts ShadowDusk's
                                   OWN output renders correctly, not a pixel-diff against real
                                   mgfxc's Vulkan oracle - that oracle currently crashes on this
                                   corpus due to a confirmed, separate MonoGame bug (SlotOffset
                                   arithmetic in VulkanShaderProfile.CreateShader; see
                                   plan/PHASE-32-appendix/vulkan-mgfx-format-spec.md). Run it for
                                   any release that could affect the Vulkan target.

  The OpenGL render gates are intentionally NOT here - CI already runs them (see
  validation-render.yml). Run them with `dotnet test` + that workflow, not this script.

.PARAMETER IncludeFna
  Also run the FNA fx_2_0 gate (validation/FnaValidation). Requires the FNA restore
  (validation/FnaValidation/restore-fna.ps1) and a Windows SDK fxc on PATH. Off by default.

.PARAMETER IncludeVulkan
  Also run the Vulkan gate (validation/CandidateVulkan + validation/BaselineVulkan +
  compare_vulkan.py). Requires a Vulkan-capable GPU. Off by default.

.PARAMETER SkipRestore
  Skip `tools/restore.ps1` (the vkd3d-shader native the DX/KNI-DX/FNA gates P/Invoke). Use only
  when you know the natives are already restored.

.EXAMPLE
  ./validation/run-windows-render-gates.ps1
  Run the core DX + KNI-DX render gates (the standard pre-release gate).

.EXAMPLE
  ./validation/run-windows-render-gates.ps1 -IncludeFna -IncludeVulkan
  Also validate the FNA fx_2_0 and Vulkan targets (for a release affecting either).
#>
[CmdletBinding()]
param(
    [switch]$IncludeFna,
    [switch]$IncludeVulkan,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "=== ShadowDusk Windows-GPU render gates (the CI-can't-run bar) ===" -ForegroundColor Cyan
Write-Host "repo: $repoRoot"
Write-Host "These gates render in a real DX11 / FNA / KNI-DX engine and compare to the reference"
Write-Host "compiler. CI has no headless driver for them, so this local run is the release gate.`n"

# Restore the vkd3d-shader native the DX/KNI-DX/FNA gates need (idempotent; SHA-256-verified).
if (-not $SkipRestore) {
    Write-Host "--- Restoring native tools (vkd3d-shader) ---" -ForegroundColor DarkCyan
    & (Join-Path $repoRoot 'tools/restore.ps1')
    Write-Host ""
}

# Run a native command and throw if it exits non-zero (native exits don't trip ErrorAction).
function Invoke-Checked {
    param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string[]]$Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$Exe $($Arguments -join ' ')' exited with code $LASTEXITCODE"
    }
}

$gates = [System.Collections.Generic.List[object]]::new()
$gates.Add(@{
    Name   = 'MonoGame WindowsDX corpus (ShadowDusk DX vs mgfxc golden)'
    Action = {
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/BaselineDx', '-c', 'Release')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/CandidateDx', '-c', 'Release')
        Invoke-Checked 'python' @('validation/compare_dx.py')
    }
})
$gates.Add(@{
    Name   = 'DX modern features / vertex texture fetch (vkd3d vs fxc oracle)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/DxModernFeatures', '-c', 'Release') }
})
$gates.Add(@{
    Name   = 'KNI DirectX (ShadowDusk DX vs mgfxc, real KNI WinForms.DX11)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/KniWinFormsDX', '-c', 'Release') }
})
if ($IncludeFna) {
    $gates.Add(@{
        Name   = 'FNA fx_2_0 (ShadowDusk .fxb vs fxc /T fx_2_0, real FNA)'
        Action = {
            $fnaRestore = Join-Path $repoRoot 'validation/FnaValidation/restore-fna.ps1'
            if (Test-Path $fnaRestore) { & $fnaRestore }
            Invoke-Checked 'dotnet' @('run', '--project', 'validation/FnaValidation', '-c', 'Release')
        }
    })
} else {
    Write-Host "NOTE: FNA gate not run (pass -IncludeFna). Required before an FNA-affecting release.`n" -ForegroundColor Yellow
}
if ($IncludeVulkan) {
    $gates.Add(@{
        Name   = 'Vulkan (ShadowDusk .mgfx, real MonoGame DesktopVK - own-output render proof, not a pixel-diff oracle)'
        Action = {
            Invoke-Checked 'dotnet' @('run', '--project', 'validation/CandidateVulkan', '-c', 'Release')
            # BaselineVulkan is best-effort: real mgfxc's own Vulkan output currently crashes on
            # this corpus (a confirmed, separate MonoGame bug), so its exit code is NOT part of
            # this gate's pass/fail signal. compare_vulkan.py only fails on a CANDIDATE problem.
            & dotnet run --project validation/BaselineVulkan -c Release
            Invoke-Checked 'python' @('validation/compare_vulkan.py')
        }
    })
} else {
    Write-Host "NOTE: Vulkan gate not run (pass -IncludeVulkan). Required before a Vulkan-affecting release.`n" -ForegroundColor Yellow
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($gate in $gates) {
    Write-Host "===== GATE: $($gate.Name) =====" -ForegroundColor Cyan
    $ok = $true
    $err = $null
    try {
        & $gate.Action
    } catch {
        $ok = $false
        $err = $_.Exception.Message
        Write-Host "GATE FAILED: $err" -ForegroundColor Red
    }
    $results.Add([pscustomobject]@{ Name = $gate.Name; Pass = $ok; Error = $err })
    Write-Host ""
}

Write-Host "================ SUMMARY ================" -ForegroundColor Cyan
$failed = 0
foreach ($r in $results) {
    if ($r.Pass) {
        Write-Host ("  [PASS] {0}" -f $r.Name) -ForegroundColor Green
    } else {
        $failed++
        Write-Host ("  [FAIL] {0}  ({1})" -f $r.Name, $r.Error) -ForegroundColor Red
    }
}
$passed = $results.Count - $failed
Write-Host ("`n{0}/{1} render gates passed." -f $passed, $results.Count)
if (-not $IncludeFna) {
    Write-Host "(FNA gate skipped - rerun with -IncludeFna before an FNA-affecting release.)" -ForegroundColor Yellow
}
if (-not $IncludeVulkan) {
    Write-Host "(Vulkan gate skipped - rerun with -IncludeVulkan before a Vulkan-affecting release.)" -ForegroundColor Yellow
}

if ($failed -gt 0) {
    Write-Host "`nRENDER GATE RED - do NOT release. A Windows-GPU render diverged from the reference compiler." -ForegroundColor Red
    exit 1
}
Write-Host "`nRENDER GATE GREEN - the Windows-GPU render proofs match the reference compiler." -ForegroundColor Green
exit 0
