#requires -Version 5.1
<#
.SYNOPSIS
  Run the Windows-GPU render-validation gates that CI CANNOT run, and fail loudly if any
  render diverges from the reference compiler.

.DESCRIPTION
  ShadowDusk's real product bar is "loads + renders like the reference compiler in the REAL
  engine" (CLAUDE.md -> "Validation render drivers are the real bar"). The OpenGL render gates
  now run in CI on Linux via Mesa llvmpipe (`.github/workflows/validation-render.yml`), but the
  DirectX / DirectX12 / FNA / KNI-DirectX render proofs have NO headless software driver we can
  run on a GitHub runner (Mesa is GL-only; a verified headless D3D/WARP story does not exist
  yet). So those gates can only run on a real Windows box with a DX12-capable GPU (Vulkan-capable
  too, unless -SkipVulkan) - which means the DEVELOPER'S
  machine is the gate, and it must be run BEFORE a release (and before merging any change that
  touches shader output / transpilation / the MGFX-FNA writers / render state / matrix handling).

  This script is that gate, in one command. It runs each Windows-GPU render driver, checks its
  self-asserting exit code (every driver compares ShadowDusk's output against the reference
  compiler - mgfxc / fxc - and exits non-zero on any over-tolerance pixel), aggregates the
  results, and exits non-zero if ANY gate failed. A green run is the evidence a release needs
  that CI cannot provide.

  Gates (all default ON except FNA). Each entry below is only the what-and-versus; per-gate
  history and evidence detail lives in docs/validation-matrix.md section 6.
    * MonoGame WindowsDX corpus  - validation/BaselineDx + CandidateDx + compare_dx.py
                                   (ShadowDusk DX vs mgfxc DX golden, real MonoGame WindowsDX).
    * DX modern features (VTF)   - validation/DxModernFeatures (vkd3d vs fxc oracle, maxd 0).
    * DX Apos.Shapes gallery     - validation/VsDrivenDx -- apos: the full 30-cell Apos.Shapes
                                   ShapeBatch gallery through the real NuGet package; d3dcompiler_47
                                   arm vs the real mgfxc DirectX_11 golden, vkd3d arm vs the
                                   package's embedded (itself vkd3d-compiled) effect - both tol 0.
    * DirectX12 PS corpus        - validation/BaselineDx12 + CandidateDx12 + compare_dx12.py
                                   (ShadowDusk DX12 vs a real mgfxc DirectX_12 golden, real
                                   MonoGame 3.8.5 WindowsDX12, maxd 0).
    * DirectX12 VS-driven + Apos.Shapes gallery - validation/VsDrivenDx12 (+ `-- apos`): the VS rig
                                   vs the real mgfxc DirectX_12 golden (maxd 0); the gallery vs the
                                   same local golden at tol 1 (2/30 cells at 1/255 - an open,
                                   not-yet-root-caused follow-up).
    * KNI DirectX                - validation/KniWinFormsDX (ShadowDusk DX vs mgfxc, real KNI
                                   WinForms.DX11).
    * KNI OpenGL desktop         - validation/Baseline + Candidate + KniDesktopGL + compare_kni.py
                                   (ShadowDusk v10 vs mgfxc + MonoGame, real KNI SDL2.GL - the
                                   real-KNI-runtime GL proof CI's llvmpipe lane does not cover).
    * KNI OpenGL VS-driven       - validation/KniVsDriven (issue #70 matrix/POSITION rig,
                                   in-process compare).
    * Apos.Shapes GL render-proof - validation/VsDriven -- apos: a single shape plus a needle-thin
                                   ellipse vs the mgfxc OpenGL golden, tol 2/255. Uses the older
                                   apos-shapes.fx pin - the current revision's mgfxc GL compile is
                                   a confirmed MojoShader bug (see AposShapesRenderer's remarks).
    * Apos.Shapes GL gallery     - validation/VsDriven -- apos-gallery: the 30-cell ShapeBatch
                                   gallery through ShadowDusk's GL compile only (no trustworthy GL
                                   oracle exists); asserts all 30 shapes render visible content.
    * ANGLE D3D11 derivatives    - validation/AngleDerivativeProbe (issue #136: the emitted
                                   fragment control-flow shapes keep dFdx/dFdy alive on the
                                   real browser WebGL backend; headless Edge/Chrome).
    * FNA fx_2_0 (-IncludeFna)   - validation/FnaValidation (vs fxc /T fx_2_0). OPT-IN because
                                   its restore-fna.ps1 clones the FNA source tree (heavy) and the
                                   oracle needs the Windows SDK fxc. Run it for any release that
                                   could affect the FNA target.
    * Vulkan PS corpus           - validation/CandidateVulkan (ShadowDusk's OWN output rendered on
                                   real MonoGame DesktopVK; not an mgfxc diff - mgfxc's output is
                                   unloadable for this corpus, a confirmed MonoGame SlotOffset bug).
    * Vulkan VS-driven + gallery - validation/VsDrivenVulkan (+ `-- apos`): a NON-IDENTITY
                                   asymmetric transform pixel-diffed vs the mgfxc 3.8.5 golden
                                   (maxd 0), plus the 30-cell ShapeBatch gallery (maxd 0).

  Both Vulkan gates are DEFAULT-ON (issue #145: a Vulkan-affecting change must not depend on
  someone remembering a switch). Pass -SkipVulkan only on a box with no Vulkan-capable GPU.

  The in-process MonoGame OpenGL render gates (StateFidelity / CbufferModel /
  TextureBreadthValidation) are intentionally NOT here - CI already runs them (see
  validation-render.yml). Run them with `dotnet test` + that workflow, not this script.

.PARAMETER IncludeFna
  Also run the FNA fx_2_0 gate (validation/FnaValidation). Requires the FNA restore
  (validation/FnaValidation/restore-fna.ps1) and a Windows SDK fxc on PATH. Off by default.

.PARAMETER IncludeVulkan
  Retained for compatibility. The Vulkan gates are default-ON as of issue #145, so this
  switch changes nothing; passing it is harmless.

.PARAMETER SkipVulkan
  Skip both Vulkan gates. Use ONLY on a machine with no Vulkan-capable GPU - a
  Vulkan-affecting change is not validated without them.

.PARAMETER SkipRestore
  Skip `tools/restore.ps1` (the vkd3d-shader native the DX/KNI-DX/FNA gates P/Invoke). Use only
  when you know the natives are already restored.

.EXAMPLE
  ./validation/run-windows-render-gates.ps1
  Run the core DX + KNI-DX + Vulkan render gates (the standard pre-release gate).

.EXAMPLE
  ./validation/run-windows-render-gates.ps1 -IncludeFna
  Also validate the FNA fx_2_0 target (for a release affecting FNA).
#>
[CmdletBinding()]
param(
    [switch]$IncludeFna,
    [switch]$IncludeVulkan,
    [switch]$SkipVulkan,
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
    Name   = 'DX Apos.Shapes (Phase 51 A3: ShadowDusk vs mgfxc DirectX_11 golden, real MonoGame WindowsDX)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenDx', '-c', 'Release', '--', 'apos') }
})
$gates.Add(@{
    Name   = 'DX12 WindowsDX12 corpus, pixel-only (Phase 54: ShadowDusk vs REAL mgfxc DirectX_12 golden, real MonoGame 3.8.5 WindowsDX12)'
    Action = {
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/BaselineDx12', '-c', 'Release')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/CandidateDx12', '-c', 'Release')
        Invoke-Checked 'python' @('validation/compare_dx12.py')
    }
})
$gates.Add(@{
    Name   = 'DX12 VS-driven + Apos.Shapes (Phase 54: ShadowDusk custom-VS DX12 effect vs REAL mgfxc DirectX_12 golden, real MonoGame 3.8.5 WindowsDX12)'
    Action = {
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenDx12', '-c', 'Release')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenDx12', '-c', 'Release', '--', 'apos')
    }
})
$gates.Add(@{
    Name   = 'KNI DirectX (ShadowDusk DX vs mgfxc, real KNI WinForms.DX11)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/KniWinFormsDX', '-c', 'Release') }
})
# KNI OpenGL gates: CI's llvmpipe lane covers the in-process MonoGame GL gates, but the
# real-KNI-runtime GL proofs (SDL2.GL desktop + the VS-driven issue-#70 rig) have no CI
# driver and previously relied on someone REMEMBERING to run them for GL-affecting
# changes. Default ON here so a GL render regression cannot slip through this script.
$gates.Add(@{
    Name   = 'KNI OpenGL desktop (ShadowDusk v10 vs mgfxc + MonoGame, real KNI SDL2.GL)'
    Action = {
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/Baseline', '-c', 'Release')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/Candidate', '-c', 'Release')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/KniDesktopGL', '-c', 'Release')
        Invoke-Checked 'python' @('validation/compare_kni.py')
    }
})
$gates.Add(@{
    Name   = 'KNI OpenGL VS-driven (issue #70 matrix/POSITION rig, real KNI SDL2.GL, in-process compare)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/KniVsDriven', '-c', 'Release') }
})
$gates.Add(@{
    Name   = 'Apos.Shapes GL render-proof (Phase 51 A3, real MonoGame DesktopGL vs mgfxc OpenGL golden)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDriven', '-c', 'Release', '--', 'apos') }
})
$gates.Add(@{
    Name   = 'Apos.Shapes GL full shape-gallery (Phase 55, real ShapeBatch, candidate-only visibility check)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDriven', '-c', 'Release', '--', 'apos-gallery') }
})
# ANGLE D3D11 derivative-shape probe (issue #136): renders the emitted fragment
# control-flow shapes in headless Edge/Chrome forced onto ANGLE Direct3D11 (the WebGL
# backend of every Windows browser) and asserts gradient ops stay alive in ShadowDusk's
# unwrapped shape. Shape-level, seconds to run, and the ONLY gate that sees the browser
# backend, so it is default ON.
$gates.Add(@{
    Name   = 'ANGLE D3D11 derivative shapes (issue #136 probe, headless Edge/Chrome)'
    Action = { Invoke-Checked 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'validation/AngleDerivativeProbe/run-angle-probe.ps1') }
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
if (-not $SkipVulkan) {
    $gates.Add(@{
        Name   = 'Vulkan PS corpus (ShadowDusk .mgfx, real MonoGame DesktopVK - own-output render proof)'
        Action = {
            Invoke-Checked 'dotnet' @('run', '--project', 'validation/CandidateVulkan', '-c', 'Release')
            # BaselineVulkan is best-effort: real mgfxc's own Vulkan output crashes on THIS corpus
            # (its SlotOffset arithmetic underflows for auto-numbered resources - a confirmed,
            # separate MonoGame bug), so its exit code is NOT part of this gate's pass/fail signal.
            # compare_vulkan.py only fails on a CANDIDATE problem. The VS-driven gate below DOES
            # have a working mgfxc oracle, because its fixture uses explicit registers.
            & dotnet run --project validation/BaselineVulkan -c Release
            Invoke-Checked 'python' @('validation/compare_vulkan.py')
        }
    })
    $gates.Add(@{
        Name   = 'Vulkan VS-driven + Apos.Shapes #145 reproducer (ShadowDusk vs mgfxc 3.8.5 goldens, real MonoGame DesktopVK)'
        Action = {
            # Issue #145: the PS-only corpus above could not see a vertex shader OR a matrix, which
            # is why row-major matrix packing shipped and every VS-driven Vulkan effect rendered
            # nothing. This gate renders a non-identity ASYMMETRIC transform and pixel-diffs against
            # the real mgfxc golden - a genuine reference-compiler oracle on Vulkan.
            Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenVulkan', '-c', 'Release')
            # Phase 2 is a SEPARATE process on purpose: DesktopVK does not survive a second
            # GraphicsDevice in one process. This one renders the issue-#145 reproducer itself -
            # upstream Apos.Shapes at its current revision - against the mgfxc golden.
            Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenVulkan', '-c', 'Release', '--', 'apos')
        }
    })
} else {
    Write-Host "NOTE: Vulkan gates SKIPPED by -SkipVulkan. They are default-ON; only skip them on a box with no Vulkan GPU.`n" -ForegroundColor Yellow
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
if ($SkipVulkan) {
    Write-Host "(Vulkan gates skipped by -SkipVulkan - rerun without it before a Vulkan-affecting release.)" -ForegroundColor Yellow
}

if ($failed -gt 0) {
    Write-Host "`nRENDER GATE RED - do NOT release. A Windows-GPU render diverged from the reference compiler." -ForegroundColor Red
    exit 1
}
Write-Host "`nRENDER GATE GREEN - the Windows-GPU render proofs match the reference compiler." -ForegroundColor Green
exit 0
