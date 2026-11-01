#requires -Version 5.1
<#
.SYNOPSIS
  Run the Windows-GPU render-validation gates that CI CANNOT run, and fail loudly if any
  render diverges from the reference compiler.

.DESCRIPTION
  ShadowDusk's real product bar is "loads + renders like the reference compiler in the REAL
  engine" (CLAUDE.md -> "The pre-merge bar has TWO halves"). The OpenGL render gates
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
                                   (ShadowDusk DX vs mgfxc DX golden, real MonoGame WindowsDX),
                                   once on the default vkd3d backend and once with
                                   `CandidateDx -- d3dcompiler` (the d3dcompiler_47 option).
    * DX modern features (VTF)   - validation/DxModernFeatures (vkd3d vs fxc oracle, maxd 0), plus
                                   its legacy-register, half-globals and legacy texture-slot arms
                                   (vs the committed mgfxc DirectX_11 goldens, maxd 0), and arm
                                   "legacy-lod": tex2Dlod (VS and PS), tex2Dbias and tex2Dproj in
                                   legacy sampler syntax vs the mgfxc 3.8.4.1 DirectX_11 golden,
                                   four positive controls (maxd 0).
    * DX Apos.Shapes gallery     - validation/VsDrivenDx -- apos: the full 30-cell Apos.Shapes
                                   ShapeBatch gallery through the real NuGet package; d3dcompiler_47
                                   arm vs the real mgfxc DirectX_11 golden, vkd3d arm vs the
                                   package's embedded (itself vkd3d-compiled) effect - both tol 0.
                                   Also runs in CI on WARP (issue #254), with texarr, samparr and
                                   the default VS-rig mode; this is the real-GPU run.
    * DX resource arrays         - validation/VsDrivenDx -- texarr / -- samparr (issues #339/#340):
                                   Texture2D Tex[2] must reflect mgfxc's table (one `Tex`
                                   parameter, one record at the base slot) on both DXBC backends
                                   and render like the mgfxc 3.8.4.1 DirectX_11 golden; a
                                   SamplerState array must be refused with SD0224, as mgfxc
                                   refuses it.
    * ShaderToy .glsl route (DX) - validation/ShaderToyRouteDx: converts GradientToy.glsl in
                                   process, compiles the converted .fx for DirectX_11, and
                                   pixel-diffs it against mgfxc's own build of the SAME .fx on
                                   real MonoGame WindowsDX. The DirectX arm of the GL gate CI
                                   runs; it only became possible in Phase 51 A10, when the
                                   converter started emitting a DirectX-valid profile header
                                   (mgfxc refuses vs_3_0 for /Profile:DirectX_11).
                                   Also runs in CI on WARP (issue #254); this is the real-GPU run.
    * DirectX12 PS corpus       - validation/BaselineDx12 + CandidateDx12 + compare_dx12.py
                                   (ShadowDusk DX12 vs a real mgfxc DirectX_12 golden, real
                                   MonoGame 3.8.5 WindowsDX12, maxd 0).
    * DirectX12 VS-driven + Apos.Shapes gallery - validation/VsDrivenDx12 (+ `-- apos`): the VS rig
                                   vs the real mgfxc DirectX_12 golden (maxd 0); the gallery vs the
                                   same local golden at tol 1 (11 pixels of 402,984 at 1/255,
                                   root-caused 2026-07-31 to the pinned DXC build - ours is
                                   dxcoob 1.7.2212.40, the golden's is MonoGame 3.8.5's bundled
                                   dxcoob 1.8.2505.32 - NOT a ShadowDusk defect; see
                                   docs/validation-matrix.md section 7).
                                   Plus `-- texarr` (issue #324): Texture2D Tex[2] vs the real
                                   mgfxc 3.8.5 golden - same parameter table (one `Tex` bound to
                                   slot 0), same picture, element [1] really read.
                                   Plus `-- legacy-lod`: tex2Dlod (VS and PS), tex2Dbias, tex2Dproj
                                   vs mgfxc 3.8.5's DirectX_12 build of the modern twin (maxd 0).
    * KNI DirectX                - validation/KniWinFormsDX (ShadowDusk DX vs mgfxc, real KNI
                                   WinForms.DX11), plus the legacy texture-slot rows.
    * KNI OpenGL desktop         - validation/Baseline + Candidate + KniDesktopGL + compare_kni.py
                                   (ShadowDusk v10 vs mgfxc + MonoGame, real KNI SDL2.GL - the
                                   real-KNI-runtime GL proof CI's llvmpipe lane does not cover).
    * KNI OpenGL VS-driven       - validation/KniVsDriven (issue #70 matrix/POSITION rig,
                                   in-process compare).
    * Skia vs KNI (SkSL)         - validation/SkiaVsKni (issue #369, Phase 62 Area D2): XnaFiddle's
                                   texture-only shaders through the SkSL converter in real
                                   SkiaSharp vs the same .fx's OpenGL build in real KNI SDL2.GL,
                                   tol 2/255, three positive controls. Image fidelity, not an
                                   mgfxc diff (Skia has no reference compiler). CI runs it on
                                   llvmpipe too; this run is its GPU-GL-driver KNI arm.
    * Initializer defaults       - validation/InitializerDefaults{Gl,Dx,KniGl,KniDx,Dx12,Vulkan}
                                   (owner directive 2026-10-28): `float R = 1;` (and a float4, a
                                   float4x4, a float4[2], plus float2x2 / int / bool off GL) is the
                                   default in the real engine with NO Set call, for every MGFX and
                                   KNIFX output. Each driver has a no-initializer control that must
                                   render black, and the GL ones render the real mgfxc OpenGL build
                                   (zeros) as a second control. Dx12 and Vulkan are MonoGame 3.8.5.
                                   KniDx and KniGl also open a Reach device: KNIFX ps/vs_4_0_level_9_1
                                   must load (recorded SM 2.0); 9_3, 4_0 (and 3_0 on GL) refused.
    * GL vertex semantics        - validation/VertexSemantics{Gl,KniGl}: a custom VertexDeclaration
                                   with BINORMAL / TANGENT / BLENDINDICES (Byte4) / BLENDWEIGHT 0+1 /
                                   PSIZE / FOG / DEPTH / TEXCOORD3 / COLOR, each element shown alone
                                   through the effect's `Pick`, plus a float3x3 TEXCOORD2
                                   interpolant; vs the mgfxc OpenGL build (maxd <= 1) and the
                                   element's CPU value, MGFX v10 on MonoGame, v10 + KNIFX on KNI;
                                   plus 30 matrix-interpolant packing fixtures whose stages declare
                                   the interpolant differently (column_major and row_major, 3x3 /
                                   4x3 / 3x4 / 2x2). Controls that must diverge: a relabelled
                                   BINORMAL0 record, two row_major fixtures packed by columns, a split float1x4 in one register.
                                   The non-square packing cases are an explicit list a strict GL
                                   driver (Mesa/Apple/ANGLE, from GL_RENDERER) must refuse
                                   (EXPECTED-REFUSED, SD0403); on a desktop GPU driver they must
                                   render and match like the rest, so this run is where they do.
    * KNI EffectTest             - validation/KniEffectTest{KniGl,KniDx,Gl,Dx,Dx12,Vulkan}: every test
                                   of KNI's own Tests/Framework.Graphics/EffectTest.cs (checked against
                                   XNA by KNI's author) in the real engine, on KNI's vendored test
                                   shaders plus five companions. GATE rows must pass, KNOWN-FAIL rows
                                   (listed ShadowDusk bugs) must still fail, INFO rows are printed with
                                   their reason, up to three positive controls must be caught (a
                                   render control runs only where that render is gated). The KNI ones
                                   run against nkast 4.2.9001 AND 4.3.9001; Vulkan is in that block.
    * Non-square matrix Set      - validation/NonSquareMatrix{Dx,Vulkan} (issue #423): float4x3,
                                   float3x2, float4x3[2] and trailing-matrix parameters Set from C#
                                   land in the right registers, vs MonoGame's own DirectX semantics
                                   (the committed real mgfxc 3.8.5 builds render alongside). The
                                   Vulkan one is in the Vulkan block.
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
    * FNA fx_2_0 (-IncludeFna)   - validation/FnaValidation (vs fxc /T fx_2_0), which since Phase 64
                                   also loads every row's candidate .fxb through a real FNA
                                   ContentManager.Load<Effect> from an XnbWriter-written .xnb (maxd 0
                                   vs the raw-bytes arm, within tolerance of the oracle). OPT-IN
                                   because its restore-fna.ps1 clones the FNA source tree (heavy) and
                                   needs an authenticated gh for the fnalibs natives. Run it for any
                                   release that could affect the FNA target or the .xnb writer.
    * FNA initializer defaults   - FnaValidation -- defaults D3D11 / OpenGL (-IncludeFna), owner
                                   directive 2026-10-28: matrix and array initializer defaults
                                   survive to real FNA (getters + unset render equal fxc fx_2_0),
                                   on both FNA3D drivers, with stripped-initializer controls.
    * Slang DX12 / Vulkan / FNA  - validation/SlangFullCorpusDx12, validation/SlangFullCorpusVulkan
                                   (Vulkan block) and FnaValidation -- slang (-IncludeFna), issue
                                   #230: the 21-shader real-slangc corpus through SlangCompiler vs
                                   the reference compiler (mgfxc DirectX_12 / Vulkan, fxc fx_2_0) on
                                   the assembled .fx, real Effect load + render, tol 4/255, two
                                   positive controls that must diverge.
    * Slang corpus               - validation/SlangCorpus (Phase 61, the free subset frontend):
                                   every corpus .slang accepted by the real pinned slangc (a
                                   test-time oracle, downloaded on demand), and the procedural
                                   subset pixel-identical vs slangc's own HLSL emission.
    * Slang full corpus (DX11)   - validation/SlangFullCorpus (Phase 66 A7, the ShadowDusk.Slang
                                   real-slangc route): 21 shaders x 4 targets compile, the
                                   procedural subset pixel-diffed vs slangc's raw HLSL, and every
                                   shader loaded + rendered in a real MonoGame WindowsDX Effect.
                                   Gates 1 + 3 also run in CI on WARP (issue #254); this is the
                                   real-GPU run, and the only Windows run of gate 2.
    * Slang textured GL          - validation/SlangTexturedGl (issue #252): the real-slangc
                                   textured shaders in real DesktopGL with the texture left to
                                   SpriteBatch's unit 0, the .mgfx sampler table checked, CPU-math
                                   render, Invert vs the mgfxc golden.
    * Vulkan PS corpus           - validation/CandidateVulkan (ShadowDusk's OWN output rendered on
                                   real MonoGame DesktopVK; not an mgfxc diff - mgfxc's output is
                                   unloadable for this corpus, a confirmed MonoGame SlotOffset bug).
    * Vulkan VS-driven + gallery - validation/VsDrivenVulkan (+ `-- apos`): a NON-IDENTITY
                                   asymmetric transform pixel-diffed vs the mgfxc 3.8.5 golden
                                   (maxd 0), plus the 30-cell ShapeBatch gallery (maxd 0), plus
                                   `-- texarr` (issue #324, expect-diagnostic, no device): a
                                   texture array must be refused with SD0221, since mgfxc 3.8.5's
                                   own Vulkan output for it binds nothing, plus `-- legacy-lod`
                                   (tex2Dlod/tex2Dbias/tex2Dproj vs mgfxc 3.8.5's Vulkan build of
                                   the modern twin, maxd 0, four positive controls).
                                   Plus `-- regslots`: explicit register slots (a lone
                                   register(t1), a t0+t2 gap, a Texture2DArray on t1, register spaces,
                                   dead samplers, Load-only, helper/macro pairings, one texture through
                                   two samplers, and one texture through a different sampler per
                                   technique or per stage) vs their mgfxc 3.8.5 goldens through
                                   SpriteBatch, maxd 0 + a known colour.
    * XNB direct writer          - validation/XnbContentLoad: a ShadowDusk-written .xnb through a
                                   real MonoGame WindowsDX Content.Load<Effect> vs stock mgcb's
                                   (pixel-identical), plus the envelope assertions (Phase 60).
    * XNB on MonoGame DesktopGL  - validation/XnbContentLoadGl: the same driver source on the
                                   DesktopGL runtime (/platform:DesktopGL reference, OpenGL
                                   candidate), the most common consumer (Phase 64).
    * XNB on KNI (two lines)     - validation/KniXnbContentLoad, built against nkast 4.2.9001 AND
                                   4.3.9001: real KNI Content.Load<Effect> on the v10 + KNIFX
                                   .xnb vs the mgcb payload (maxd 0); pins that stock mgcb output
                                   is rejected on 4.2 (the reader-name fact behind the XNA-4.0
                                   manifest) and loads on 4.3 (Phase 64).
    * MGCB plugin (Phase 29)     - validation/MgcbPlugin: NOT a render gate. Drives a real
                                   `dotnet mgcb` content build through the /reference:'d
                                   ShadowDusk plugin and asserts the .mgfx inside the .xnb is
                                   byte-for-byte the ShadowDuskCLI binary's, that the .xnb
                                   envelope matches MGCB's own stock build, and that the payload
                                   differs from stock (i.e. ShadowDusk really compiled it). It is
                                   here because `dotnet test` has no dotnet-mgcb; no GPU needed.
                                   Phase 63: two MGCB versions (3.8.4.1 + 3.8.5, which renumbered
                                   TargetPlatform: Web/DesktopVK/WindowsDX12 arms) and a decoy
                                   dxcompiler.dll on the child PATH (pinned-DXC + dxil guard).
                                   Issue #274: on DesktopVK/WindowsDX12 (MGFX v11) the per-shader
                                   source-file string must be the stock build's `<unknown>`, never
                                   the build machine's path; the payload is the CLI's with only
                                   that string replaced; a second-directory build is byte-identical.
    * Content Builder (3.8.5)    - validation/ContentBuilder: a REAL MonoGame 3.8.5 ContentBuilder
                                   subclass builds the fixtures through the stock pair AND through
                                   ShadowDusk.ContentPipeline's pair (the library shape, Phase 63),
                                   asserts payload == ShadowDuskCLI and envelope == stock (plus,
                                   on its DesktopVK/WindowsDX12 passes, the issue-#274 MGFX v11
                                   source-file string checks and a second Builder run from another
                                   source directory), then
                                   Content.Load<Effect>s both on MonoGame 3.8.5 WindowsDX and
                                   requires pixel-identical renders. Also the only run of the
                                   Builder's unguarded GetTypes() scan over our real graph.

  The Vulkan block (CandidateVulkan, InitializerDefaultsVulkan, NonSquareMatrixVulkan,
  KniEffectTestVulkan, VsDrivenVulkan in every mode, SlangFullCorpusVulkan) is DEFAULT-ON
  (issue #145: a Vulkan-affecting change must not depend on someone remembering a switch). Pass
  -SkipVulkan only on a box with no Vulkan-capable GPU. CI also runs all of it but
  SlangFullCorpusVulkan (validation-render.yml, Mesa lavapipe + the Khronos validation layer),
  but that is a CPU driver; this local run is still the only GPU-driver Vulkan render.

  The in-process MonoGame OpenGL render gates (StateFidelity / CbufferModel /
  TextureBreadthValidation / ReservedWordGl / SamplerPairsGl / SamplerRegisterOrderGl /
  DeferredSpriteMrtGl / ShaderToyRouteGl / RaylibRoute) are intentionally NOT here - CI already runs them
  (see validation-render.yml). Run them with `dotnet test` + that workflow, not this script.

.PARAMETER IncludeFna
  Also run the FNA fx_2_0 gate (validation/FnaValidation). Requires the FNA restore
  (validation/FnaValidation/restore-fna.ps1) and a Windows SDK fxc on PATH. Off by default.

.PARAMETER IncludeVulkan
  Retained for compatibility. The Vulkan gates are default-ON as of issue #145, so this
  switch changes nothing; passing it is harmless.

.PARAMETER SkipVulkan
  Skip the Vulkan block. Use ONLY on a machine with no Vulkan-capable GPU - a
  Vulkan-affecting change is not validated without them.

.PARAMETER NewerMonoGameOnly
  Run ONLY the newer-MonoGame block (Phase 69): the drivers rebuilt against each release in
  `$newerMonoGame` with -p:MonoGameVersion. For taking on a new MonoGame release; the release gate
  is always the full run, which includes this block.

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
    [switch]$SkipRestore,
    [switch]$NewerMonoGameOnly
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
        # The same corpus through the public d3dcompiler_47 oracle backend (CompilerOptions.DxbcBackend),
        # whose scheduled instruction stream must not drive the DirectX 11 texture slots.
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/CandidateDx', '-c', 'Release', '--', 'd3dcompiler')
        Invoke-Checked 'python' @('validation/compare_dx.py')
    }
})
$gates.Add(@{
    Name   = 'DX modern features / vertex texture fetch (vkd3d vs fxc oracle) + legacy sampler register and texture-slot arms vs mgfxc'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/DxModernFeatures', '-c', 'Release') }
})
$gates.Add(@{
    Name   = 'DX Apos.Shapes (Phase 51 A3: ShadowDusk vs mgfxc DirectX_11 golden, real MonoGame WindowsDX)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenDx', '-c', 'Release', '--', 'apos') }
})
$gates.Add(@{
    Name   = 'DX resource arrays (issues #339/#340: texture array table + render vs mgfxc 3.8.4.1 DirectX_11 golden; sampler array refused with SD0224), real MonoGame WindowsDX'
    Action = {
        # Issue #339: Texture2D Tex[2] must reflect mgfxc's table (ONE `Tex` parameter, ONE record
        # at the array's base slot) on both DXBC backends, load with Parameters["Tex"] present, and
        # render like the golden; element [1] through GraphicsDevice.Textures[1] is reported.
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenDx', '-c', 'Release', '--', 'texarr')
        # Issue #340: SamplerState Samplers[2] must FAIL with SD0224 at its declaration on DirectX
        # 11 and 12, as real mgfxc refuses it on every profile in its own parser.
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenDx', '-c', 'Release', '--', 'samparr')
    }
})
$gates.Add(@{
    Name   = 'ShaderToy .glsl route on DX (Phase 51 A5/A10: converted .fx vs REAL mgfxc DirectX_11 golden, real MonoGame WindowsDX)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/ShaderToyRouteDx', '-c', 'Release') }
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
        # Issue #324: an ARRAY of textures (Texture2D Tex[2]) vs the real mgfxc 3.8.5 golden: the
        # parameter table must equal mgfxc's (one Tex parameter bound to slot 0), both arms must
        # load and draw the same picture, and element [1], reachable only through
        # GraphicsDevice.Textures[1], must really be read (CPU expectation (cat + green) / 2).
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenDx12', '-c', 'Release', '--', 'texarr')
        # tex2Dlod (vertex AND pixel stage), tex2Dbias, tex2Dproj in legacy sampler syntax vs
        # mgfxc 3.8.5's DirectX_12 build of the modern twin (mgfxc 3.8.5 has no DX12 reference
        # for the legacy spelling), maxd 0, four positive controls.
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenDx12', '-c', 'Release', '--', 'legacy-lod')
    }
})
$gates.Add(@{
    Name   = 'KNI DirectX (ShadowDusk DX vs mgfxc, real KNI WinForms.DX11, incl. legacy texture slots)'
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
# Issue #369 (Phase 62 Area D2): the same XnaFiddle .fx through the SkSL converter in real
# SkiaSharp vs its OpenGL build in real KNI SDL2.GL (Gum authors on KNI and loads in Skia).
# CI runs it on llvmpipe; here the KNI arm runs on the GPU's GL driver. SHADOWDUSK_REQUIRE_GL
# turns a missing GL context into a failure instead of the driver's skip.
$gates.Add(@{
    Name   = 'Skia vs KNI (issue #369: XnaFiddle shaders via SkSL in real SkiaSharp vs OpenGL in real KNI SDL2.GL, tol 2/255)'
    Action = {
        $env:SHADOWDUSK_REQUIRE_GL = '1'
        try { Invoke-Checked 'dotnet' @('run', '--project', 'validation/SkiaVsKni', '-c', 'Release') }
        finally { Remove-Item Env:SHADOWDUSK_REQUIRE_GL -ErrorAction SilentlyContinue }
    }
})
# Global-initializer defaults (`float R = 1;`), owner directive 2026-10-28: the value is the default
# in EVERY MGFX/KNIFX output, proven in the real engine with no Set call. Five runtimes here (the
# Vulkan one is in the Vulkan block); each has a no-initializer control and the GL ones also render
# the real mgfxc OpenGL build, which stores zeros.
foreach ($drv in @(
    @('InitializerDefaultsGl',    'MonoGame DesktopGL'),
    @('InitializerDefaultsDx',    'MonoGame WindowsDX'),
    @('InitializerDefaultsKniGl', 'KNI SDL2.GL, MGFX v10 and KNIFX, plus the KNIFX shader model (incl. SM 2.0/3.0) on a Reach device'),
    @('InitializerDefaultsKniDx', 'KNI WinForms.DX11, MGFX v10 and KNIFX, plus the KNIFX shader model on a Reach device and KNIFX SM 2.0/3.0 vs KNI''s own build'),
    @('InitializerDefaultsDx12',  'MonoGame 3.8.5 WindowsDX12'))) {
    $project = "validation/$($drv[0])"
    $gates.Add(@{
        Name   = "Initializer defaults ($($drv[1]): a global initializer is the parameter's default with no Set call, vs no-initializer and mgfxc-GL controls)"
        # [scriptblock]::Create, not GetNewClosure(): a closure runs in a module scope that cannot see Invoke-Checked.
        Action = [scriptblock]::Create("Invoke-Checked 'dotnet' @('run', '--project', '$project', '-c', 'Release')")
    })
}
# Issue #423: non-square matrix parameters Set from C# (float4x3, float3x2, float4x3[2], trailing
# matrices) land in the right registers in real MonoGame WindowsDX; the expectation is MonoGame's own
# DirectX semantics, confirmed by the committed real mgfxc 3.8.5 DirectX_11 builds rendered alongside.
$gates.Add(@{
    Name   = 'Non-square matrix Set (issue #423, real MonoGame WindowsDX vs MonoGame DX semantics + mgfxc DirectX_11 builds)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/NonSquareMatrixDx', '-c', 'Release') }
})
# GL vertex-input semantics beyond POSITION/COLOR/TEXCOORD/NORMAL (BINORMAL, TANGENT,
# BLENDINDICES, BLENDWEIGHT, PSIZE, FOG, DEPTH, non-zero indices) and float3x3 interpolants:
# each element of a custom VertexDeclaration drives the colour, vs the mgfxc OpenGL build in the
# same process AND the CPU-known element value, with a relabelled-record control that must diverge.
foreach ($drv in @(
    @('VertexSemanticsGl',    'MonoGame DesktopGL'),
    @('VertexSemanticsKniGl', 'KNI SDL2.GL, MGFX v10 and KNIFX'))) {
    $project = "validation/$($drv[0])"
    $gates.Add(@{
        Name   = "GL vertex semantics + matrix interpolants ($($drv[1]): every VertexElementUsage binds, vs mgfxc OpenGL build, relabelled-record control)"
        Action = [scriptblock]::Create("Invoke-Checked 'dotnet' @('run', '--project', '$project', '-c', 'Release')")
    })
}
# KNI's own effect test suite (EffectTest.cs, checked against XNA by KNI's author) as a gate: each
# driver prints a GATE / KNOWN-FAIL / INFO table and exits non-zero on a gated regression, a
# known-failing row that unexpectedly passes (the fixing PR must flip it), or a missed positive
# control. The Vulkan driver is in the Vulkan block below.
foreach ($drv in @(
    @('KniEffectTestKniGl', 'KNI 4.2.9001 SDL2.GL, MGFX v10 and KNIFX', @()),
    @('KniEffectTestKniGl', 'KNI 4.3.9001 SDL2.GL, MGFX v10 and KNIFX', @('-p:KniVersion=4.3.9001')),
    @('KniEffectTestKniDx', 'KNI 4.2.9001 WinForms.DX11, MGFX v10 and KNIFX', @()),
    @('KniEffectTestKniDx', 'KNI 4.3.9001 WinForms.DX11, MGFX v10 and KNIFX', @('-p:KniVersion=4.3.9001')),
    @('KniEffectTestGl',    'MonoGame DesktopGL', @()),
    @('KniEffectTestDx',    'MonoGame WindowsDX', @()),
    @('KniEffectTestDx12',  'MonoGame 3.8.5 WindowsDX12', @()))) {
    $runArgs = (@('run', '--project', "validation/$($drv[0])", '-c', 'Release') + $drv[2] | ForEach-Object { "'$_'" }) -join ', '
    $gates.Add(@{
        Name   = "KNI EffectTest ($($drv[1]): KNI's own effect tests in the real engine, gated / known-failing / informational rows, up to three positive controls)"
        # [scriptblock]::Create, not GetNewClosure(): a closure runs in a module scope that cannot see Invoke-Checked.
        Action = [scriptblock]::Create("Invoke-Checked 'dotnet' @($runArgs)")
    })
}
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
# MGCB content-processor plugin (Phase 29). The ONE gate here that is not a render proof:
# it drives a REAL `dotnet mgcb` content build through the /reference:'d plugin and asserts
# the .mgfx inside the produced .xnb is byte-for-byte the CLI's. It lives here because
# `dotnet test` has no dotnet-mgcb, so this script is the only place it will actually be run.
# Cheap (seconds, no GPU) and default ON - the failure modes it catches (MGCB stops
# discovering the plugin; the plugin stops finding its natives inside MGCB's process; MonoGame
# changes the content contract) are all silent for everyone until a consumer hits them.
# Since Phase 63 (issue #203) it runs TWO MGCB versions (the manifest 3.8.4.1 and a real 3.8.5,
# which renumbered TargetPlatform) and puts a decoy dxcompiler.dll first on the plugin-arm
# child's PATH, so a fallback to an OS-search-path DXC or an unsigned DX12 module fails loudly.
# Since issue #274 its DesktopVK / WindowsDX12 cases also pin the MGFX v11 source-file string to
# what MGCB's own stock processor writes, and rebuild from a second directory for identical bytes.
# Since issue #280 every case also requires the CLI's own .xnb payload to equal the plugin's, and
# every v11 case rebuilds under /config:Debug (DebugMode=Auto) for bytes identical to the default
# build, with stock MGCB's own /config:Debug build checked the same way as the premise.
$gates.Add(@{
    Name   = 'MGCB content-processor plugin (Phase 29: real dotnet mgcb build, .xnb payload vs CLI bytes; Phase 63: + dotnet-mgcb 3.8.5 arm for Web/DesktopVK/WindowsDX12 + decoy-PATH DXC guard; issue #274: MGFX v11 source-file string == stock, path-independent .xnb; issue #280: CLI .xnb == plugin payload, /config:Debug Auto == default build like stock)'
    Action = {
        # The pinned dotnet-mgcb from .config/dotnet-tools.json (idempotent; cached offline).
        Invoke-Checked 'dotnet' @('tool', 'restore')
        # The driver measures the BUILD OUTPUT, so build the two things it compares first.
        Invoke-Checked 'dotnet' @('build', 'src/ShadowDusk.MgcbPlugin/ShadowDusk.MgcbPlugin.csproj', '-c', 'Release')
        Invoke-Checked 'dotnet' @('build', 'src/ShadowDusk.Cli/ShadowDusk.Cli.csproj', '-c', 'Release')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/MgcbPlugin', '-c', 'Release')
    }
})
# Direct .xnb writer (Phase 60, issue #199). The drop-in claim is "replace your content pipeline
# and change NO lines of code", which only a real ContentManager can test: `new Effect(gd, bytes)`
# proves the payload loads but says nothing about the XNB container, the platform-byte whitelist,
# or the type-reader manifest - and those are precisely what Content.Load validates. This gate
# builds each fixture through stock mgcb AND through ShadowDusk's XnbWriter, loads BOTH with
# Content.Load<Effect>(assetName), and requires the renders to be pixel-identical. Default ON:
# it needs a GPU but is seconds long, and every failure mode it covers is silent for a consumer
# until their game refuses to start.
$gates.Add(@{
    Name   = 'XNB direct writer (Phase 60: real Content.Load<Effect> on a ShadowDusk-written .xnb vs mgcb''s)'
    Action = {
        Invoke-Checked 'dotnet' @('tool', 'restore')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/XnbContentLoad', '-c', 'Release')
    }
})
# The MonoGame DesktopGL arm of the same claim (Phase 64): the most common consumer runtime,
# which the WindowsDX gate above cannot see (a DesktopGL game rejects a DX payload outright,
# and the container is validated by a different ContentManager build). Same shared driver
# source, /platform:DesktopGL reference, OpenGL candidate. Not in CI's llvmpipe lane yet:
# mgcb 3.8.4.1's EffectProcessor compiles through d3dcompiler in-process and is unverified
# on Linux without Wine, so this script is where it runs.
$gates.Add(@{
    Name   = 'XNB on MonoGame DesktopGL (Phase 64: real DesktopGL Content.Load<Effect> on a ShadowDusk-written .xnb vs mgcb''s)'
    Action = {
        Invoke-Checked 'dotnet' @('tool', 'restore')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/XnbContentLoadGl', '-c', 'Release')
    }
})
# The KNI arm of the same claim (Phase 64). KNI 4.2.9001's reader-name resolver throws on the
# mgcb-shaped manifest (stock mgcb .xnb files fail on it), which is why XnbWriter emits the
# XNA-4.0 name; this gate proves that fix on BOTH KNI lines, each a separate build against its
# own nkast package set: real KNI ContentManager.Load<Effect> on the v10 and KNIFX .xnb vs the
# mgcb payload (maxd 0), plus the pinned positive control that stock mgcb output is REJECTED on
# 4.2 and loads on 4.3. Default ON: the KNI failure mode is silent for a consumer until their
# game refuses to start, and CI has no KNI runtime.
$gates.Add(@{
    Name   = 'XNB on KNI 4.2.9001 + 4.3.9001 (Phase 64: real KNI Content.Load<Effect> on a ShadowDusk-written .xnb, v10 + KNIFX vs mgcb payload; stock-mgcb rejection on 4.2 pinned)'
    Action = {
        Invoke-Checked 'dotnet' @('tool', 'restore')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/KniXnbContentLoad', '-c', 'Release')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/KniXnbContentLoad', '-c', 'Release', '-p:KniVersion=4.3.9001')
    }
})
# MonoGame 3.8.5 Content Builder (Phase 63, issue #203). A REAL ContentBuilder subclass builds
# the fixtures through MonoGame's stock pair AND through ShadowDusk.ContentPipeline's, then
# Content.Load<Effect>s both on MonoGame 3.8.5 and requires pixel-identical renders. It is also
# the only place the Builder's unguarded Assembly.GetTypes() scan is exercised against our real
# dependency graph - a dependency that fails type-load takes the consumer's whole build down.
$gates.Add(@{
    Name   = 'MonoGame 3.8.5 Content Builder (Phase 63: ShadowDusk.ContentPipeline pair vs stock pair, real ContentBuilder + Content.Load<Effect> on 3.8.5; issue #274: DesktopVK/WindowsDX12 passes, MGFX v11 source-file string == stock, path-independent .xnb)'
    Action = {
        Invoke-Checked 'dotnet' @('build', 'src/ShadowDusk.Cli/ShadowDusk.Cli.csproj', '-c', 'Release')
        Invoke-Checked 'dotnet' @('run', '--project', 'validation/ContentBuilder', '-c', 'Release')
    }
})
# Slang corpus cross-validation (Phase 61). Two gates in one driver: every corpus .slang is
# accepted by the REAL pinned slangc (proving the corpus is genuine Slang, not HLSL wearing a
# .slang extension), and the uniform-free procedural subset renders PIXEL-IDENTICAL through
# ShadowDusk's route vs through slangc's own HLSL emission (same DXC + SPIRV-Cross both sides,
# so any divergence is attributable to the one thing that differs: how the Slang text was
# read). slangc here is a TEST-TIME ORACLE like fxc/mgcb - downloaded on demand, SHA-256
# verified, cached, never shipped. First run needs network (~55 MB); GL context required.
$gates.Add(@{
    Name   = 'Slang corpus (Phase 61: 17 shaders slangc-validated + procedural subset pixel-diffed vs slangc''s HLSL)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/SlangCorpus', '-c', 'Release') }
})
# Real-slangc Slang corpus (Phase 66 A7). Distinct from the SUBSET-frontend gate above: this
# exercises the ShadowDusk.Slang package's real-slangc compile route (genuine Slang - import,
# generics, interface conformances). Three gates in one driver: (1) the 21-shader corpus
# compiles through the REAL SlangCompiler on OpenGL/DirectX_11/DirectX_12/Vulkan (slangc here
# is the PRODUCT route, not a downloaded test-time oracle - no network needed once
# tools/restore.ps1 has run); (2) the uniform-free procedural subset (8 shaders) renders
# pixel-identical through ShadowDusk's .fx-wrapped route vs slangc's own raw HLSL emission fed
# to the same DXC + SPIRV-Cross (OpenGL); (3) every shader loads into a REAL
# MonoGame.Framework.WindowsDX Effect and renders (DirectX_11). The DirectX_12, Vulkan and FNA
# real-Effect arms are the issue-#230 gates below (and in the FNA / Vulkan blocks).
$gates.Add(@{
    Name   = 'Slang full corpus (Phase 66 A7: real-slangc route, 21 shaders x 4 targets + procedural pixel-diff vs slangc''s raw HLSL + real DirectX_11 Effect load/render)'
    # -f net8.0-windows: the driver multi-targets (issue #227); only the Windows build has gate 3.
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/SlangFullCorpus', '-c', 'Release', '-f', 'net8.0-windows') }
})
# Real-slangc textured shaders on OpenGL in REAL MonoGame DesktopGL (issue #252). The Slang
# full-corpus gate above never shows WHICH texture unit a sampler landed on; this one leaves the
# texture to SpriteBatch (unit 0, never set by parameter), checks the .mgfx sampler table, and
# compares each textured PS-only corpus shader against its own math on the CPU, plus Invert
# against the committed mgfxc golden. Also runs in CI on llvmpipe (validation-render.yml); this
# is the real-GPU-driver run.
$gates.Add(@{
    Name   = 'Slang textured GL (issue #252: real-slangc textured shaders sample SpriteBatch''s unit 0, real DesktopGL, Invert vs mgfxc)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/SlangTexturedGl', '-c', 'Release') }
})
# Real-slangc Slang corpus on DirectX_12 in REAL MonoGame 3.8.5 WindowsDX12 (issue #230): every
# shader through SlangCompiler vs real mgfxc 3.8.5 /Profile:DirectX_12 on the assembled .fx the
# pipeline was handed, same device, tol 4/255, plus two positive controls that must diverge.
$gates.Add(@{
    Name   = 'Slang DX12 (issue #230: real-slangc route vs mgfxc DirectX_12, 21 shaders, real MonoGame 3.8.5 WindowsDX12 Effect load + render)'
    Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/SlangFullCorpusDx12', '-c', 'Release') }
})
if ($IncludeFna) {
    $gates.Add(@{
        Name   = 'FNA fx_2_0 (ShadowDusk .fxb vs fxc /T fx_2_0, real FNA; + the .xnb Content.Load arm, Phase 64)'
        Action = {
            $fnaRestore = Join-Path $repoRoot 'validation/FnaValidation/restore-fna.ps1'
            if (Test-Path $fnaRestore) { & $fnaRestore }
            Invoke-Checked 'dotnet' @('run', '--project', 'validation/FnaValidation', '-c', 'Release')
        }
    })
    # Issue #230: the real-slangc Slang corpus in real FNA, vs fxc /T fx_2_0 on the assembled
    # .fx. Its own process (FNA allows one Game per process). Restore already ran above.
    $gates.Add(@{
        Name   = 'Slang FNA (issue #230: real-slangc route vs fxc /T fx_2_0, 21 shaders, real FNA Effect + .xnb arm)'
        Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/FnaValidation', '-c', 'Release', '--', 'slang') }
    })
    # Owner directive 2026-10-28: initializer defaults (matrix and array included) survive to the
    # FNA runtime. Every numeric parameter read back before any Set, and the unset render, must
    # equal fxc /T fx_2_0's build, on BOTH FNA3D drivers (one process per driver: FNA allows one
    # Game per process). Four stripped-initializer positive controls must diverge.
    $gates.Add(@{
        Name   = 'FNA initializer defaults, D3D11 driver (8 fixtures incl. float2x2/3x3/4x4/row_major and float/vector/int/bool/matrix arrays, vs fxc /T fx_2_0, real FNA getters + unset render, 4 positive controls)'
        Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/FnaValidation', '-c', 'Release', '--', 'defaults', 'D3D11') }
    })
    $gates.Add(@{
        Name   = 'FNA initializer defaults, OpenGL driver (same fixtures and controls as the D3D11 run)'
        Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/FnaValidation', '-c', 'Release', '--', 'defaults', 'OpenGL') }
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
        Name   = 'Initializer defaults (MonoGame 3.8.5 DesktopVK: a global initializer is the parameter default with no Set call, vs no-initializer control)'
        Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/InitializerDefaultsVulkan', '-c', 'Release') }
    })
    $gates.Add(@{
        # Issue #423: the same non-square Set cases on DesktopVK, against MonoGame's DirectX semantics.
        # Real mgfxc 3.8.5's Vulkan builds render alongside, reported only (its transposed header drops
        # a float4x3's translation row, zeroes a float3x2 and throws on float4x3[2]).
        Name   = 'Non-square matrix Set (issue #423, real MonoGame 3.8.5 DesktopVK vs MonoGame DX semantics)'
        Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/NonSquareMatrixVulkan', '-c', 'Release') }
    })
    $gates.Add(@{
        Name   = 'KNI EffectTest (MonoGame 3.8.5 DesktopVK: KNI''s own effect tests in the real engine, gated / known-failing / informational rows, positive controls)'
        Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/KniEffectTestVulkan', '-c', 'Release') }
    })
    $gates.Add(@{
        Name   = 'Vulkan VS-driven + Apos.Shapes #145 reproducer + explicit register slots (ShadowDusk vs mgfxc 3.8.5 goldens, real MonoGame DesktopVK)'
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
            # Issue #324, EXPECT-DIAGNOSTIC (no device): an ARRAY of textures must be refused with
            # SD0221 at its declaration, because real mgfxc 3.8.5's own Vulkan output for it has
            # no parameter, no sampler record and no descriptor binding (the committed golden is
            # decoded to pin that). `-- texarr-reference` draws mgfxc's effect itself, as evidence.
            Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenVulkan', '-c', 'Release', '--', 'texarr')
            # tex2Dlod (vertex AND pixel stage), tex2Dbias, tex2Dproj in legacy sampler syntax vs
            # mgfxc 3.8.5's Vulkan build of the modern twin, maxd 0, four positive controls. Its
            # own process (one device per process on DesktopVK).
            Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenVulkan', '-c', 'Release', '--', 'legacy-lod')
            # Explicit register slots: a texture's Vulkan slot is its SPIR-V binding minus 32, as mgfxc
            # 3.8.5 writes it and DesktopVK reads it. A lone register(t1), a t0+t2 gap and a Texture2DArray
            # on t1 vs their mgfxc goldens, each through SpriteBatch with a known-colour check (before
            # the fix ShadowDusk wrote the ordinal slot and the lone t1 and the array drew (0,0,0,0)).
            Invoke-Checked 'dotnet' @('run', '--project', 'validation/VsDrivenVulkan', '-c', 'Release', '--', 'regslots')
        }
    })
    $gates.Add(@{
        # Issue #230: the real-slangc Slang corpus vs real mgfxc 3.8.5 /Profile:Vulkan on the
        # assembled .fx (measured: mgfxc's Vulkan build of these 21 loads and renders, unlike the
        # CandidateVulkan corpus above), real DesktopVK, tol 4/255, two positive controls.
        Name   = 'Slang Vulkan (issue #230: real-slangc route vs mgfxc Vulkan, 21 shaders, real MonoGame DesktopVK Effect load + render)'
        Action = { Invoke-Checked 'dotnet' @('run', '--project', 'validation/SlangFullCorpusVulkan', '-c', 'Release') }
    })
} else {
    Write-Host "NOTE: Vulkan gates SKIPPED by -SkipVulkan. They are default-ON; only skip them on a box with no Vulkan GPU.`n" -ForegroundColor Yellow
}

# Phase 69: every newer MonoGame release runs the same drivers again, built with
# -p:MonoGameVersion=<v> (validation/MonoGameVersion.targets), BESIDE the pinned arms above, never
# instead of them. The output is the same bytes; only the runtime that loads it changes. A driver's
# MonoGameVersionGuard exits 3 unless that release actually loaded. A new MonoGame release is
# appended here (and to the CI lanes) after its sweep is green (CLAUDE.md: validate before AND after).
$newerMonoGame = @('3.8.6')
$newerGates = [System.Collections.Generic.List[object]]::new()
foreach ($mg in $newerMonoGame) {
    $p = "-p:MonoGameVersion=$mg"
    # Each entry: gate name, then the commands (dotnet run args, 'py:<script>' or 'build:<project>';
    # a leading '?' marks a best-effort command whose exit code is not part of the gate, as in the
    # Vulkan block; 'props:<msbuild property>' adds a property to every dotnet run that follows). The MGCB plugin gate is not here: it runs every dotnet-mgcb version in one
    # process (validation/MgcbPlugin, a 3.8.6 arm since Phase 69).
    $arms = [System.Collections.Generic.List[object]]::new()
    $arms.Add(@("MonoGame $mg WindowsDX corpus (vkd3d + d3dcompiler arms vs mgfxc golden)",
        @('validation/BaselineDx'), @('validation/CandidateDx'), 'py:validation/compare_dx.py',
        @('validation/CandidateDx', '--', 'd3dcompiler'), 'py:validation/compare_dx.py'))
    $arms.Add(@("MonoGame $mg WindowsDX VS rig + Apos.Shapes gallery + resource arrays",
        @('validation/VsDrivenDx'), @('validation/VsDrivenDx', '--', 'apos'),
        @('validation/VsDrivenDx', '--', 'texarr'), @('validation/VsDrivenDx', '--', 'samparr')))
    $arms.Add(@("MonoGame $mg WindowsDX modern features (VTF, legacy registers, half globals, legacy texture slots, legacy lod)",
        @('validation/DxModernFeatures')))
    $arms.Add(@("MonoGame $mg WindowsDX initializer defaults + non-square matrix Set + KNI EffectTest + XNB Content.Load",
        @('validation/InitializerDefaultsDx'), @('validation/NonSquareMatrixDx'),
        @('validation/KniEffectTestDx'), @('validation/XnbContentLoad')))
    $arms.Add(@("MonoGame $mg DesktopGL initializer defaults + KNI EffectTest + XNB Content.Load + Apos.Shapes + MGFX v11",
        @('validation/InitializerDefaultsGl'), @('validation/KniEffectTestGl'),
        @('validation/XnbContentLoadGl'), @('validation/VsDriven', '--', 'apos'), @('validation/MonoGameV11')))
    # 3.8.6 changed how managed DesktopGL masks MRT draw buffers (MonoGame #9509), hence the MRT driver.
    $arms.Add(@("MonoGame $mg DesktopGL VS rig + vertex semantics + MRT + sampler pairs + sampler register order",
        @('validation/VsDriven'), @('validation/VertexSemanticsGl'), @('validation/DeferredSpriteMrtGl'),
        @('validation/SamplerPairsGl'), @('validation/SamplerRegisterOrderGl')))
    # DesktopGL4 (MonoGame 3.8.6+): the native OpenGL 4.1-core runtime, which loads the same OpenGL
    # .mgfx. Not VertexSemanticsGl or KniEffectTestGl: a GLSL non-square matrix (mat3x4/mat4x3) aborts
    # DesktopGL4's core-profile compile of version-less GLSL (docs/validation-matrix.md section 7).
    $arms.Add(@("MonoGame $mg DesktopGL4 (native GL 4.1 core): corpus + VS rig + Apos.Shapes + MRT + sampler pairs/order + initializer defaults + XNB + MGFX v11",
        'props:-p:MonoGameDesktopGL4=true',
        @('validation/Baseline'), @('validation/Candidate'), 'py:validation/compare.py',
        @('validation/VsDriven'), @('validation/VsDriven', '--', 'apos'), @('validation/DeferredSpriteMrtGl'),
        @('validation/SamplerPairsGl'), @('validation/SamplerRegisterOrderGl'), @('validation/InitializerDefaultsGl'),
        @('validation/XnbContentLoadGl'), @('validation/MonoGameV11')))
    # The Content Builder gate's newer arm also moves its DXC tool package (ContentBuilder.csproj).
    $arms.Add(@("MonoGame $mg Content Builder (ShadowDusk.ContentPipeline pair vs stock pair, real ContentBuilder + Content.Load<Effect>)",
        'build:src/ShadowDusk.Cli/ShadowDusk.Cli.csproj', @('validation/ContentBuilder')))
    $arms.Add(@("MonoGame $mg WindowsDX12 corpus + VS rig + Apos.Shapes + texture array + legacy lod",
        @('validation/BaselineDx12'), @('validation/CandidateDx12'), 'py:validation/compare_dx12.py',
        @('validation/VsDrivenDx12'), @('validation/VsDrivenDx12', '--', 'apos'),
        @('validation/VsDrivenDx12', '--', 'texarr'), @('validation/VsDrivenDx12', '--', 'legacy-lod')))
    $arms.Add(@("MonoGame $mg WindowsDX12 initializer defaults + KNI EffectTest + Slang full corpus",
        @('validation/InitializerDefaultsDx12'), @('validation/KniEffectTestDx12'), @('validation/SlangFullCorpusDx12')))
    if (-not $SkipVulkan) {
        $arms.Add(@("MonoGame $mg DesktopVK corpus + VS rig + Apos.Shapes + texarr + legacy lod + register slots",
            @('validation/CandidateVulkan'), @('?validation/BaselineVulkan'), 'py:validation/compare_vulkan.py',
            @('validation/VsDrivenVulkan'), @('validation/VsDrivenVulkan', '--', 'apos'),
            @('validation/VsDrivenVulkan', '--', 'texarr'), @('validation/VsDrivenVulkan', '--', 'legacy-lod'),
            @('validation/VsDrivenVulkan', '--', 'regslots')))
        $arms.Add(@("MonoGame $mg DesktopVK initializer defaults + non-square matrix Set + KNI EffectTest + Slang full corpus",
            @('validation/InitializerDefaultsVulkan'), @('validation/NonSquareMatrixVulkan'),
            @('validation/KniEffectTestVulkan'), @('validation/SlangFullCorpusVulkan')))
    }
    foreach ($arm in $arms) {
        $props = @()
        $lines = @(foreach ($cmd in $arm[1..($arm.Count - 1)]) {
            if ($cmd -is [string] -and $cmd.StartsWith('props:')) {
                $props += $cmd.Substring(6)
            } elseif ($cmd -is [string] -and $cmd.StartsWith('build:')) {
                "Invoke-Checked 'dotnet' @('build', '$($cmd.Substring(6))', '-c', 'Release')"
            } elseif ($cmd -is [string]) {
                "Invoke-Checked 'python' @('$($cmd.Substring(3))')"
            } else {
                $project = $cmd[0]; $bestEffort = $project.StartsWith('?')
                if ($bestEffort) { $project = $project.Substring(1) }
                $runArgs = @('run', '--project', $project, '-c', 'Release', $p) + $props
                if ($cmd.Count -gt 1) { $runArgs += $cmd[1..($cmd.Count - 1)] }
                $quoted = ($runArgs | ForEach-Object { "'$_'" }) -join ', '
                if ($bestEffort) { "& dotnet @($quoted)" } else { "Invoke-Checked 'dotnet' @($quoted)" }
            }
        })
        # [scriptblock]::Create, not GetNewClosure(): a closure runs in a module scope that cannot see Invoke-Checked.
        # A GL driver without a GL context must fail here, never soft-skip as a pass; the variable is
        # removed afterwards, as the SkiaVsKni gate does, so it never leaks into the caller's session.
        $body = "`$env:SHADOWDUSK_REQUIRE_GL = '1'`ntry {`n" + ($lines -join "`n") +
            "`n} finally { Remove-Item Env:SHADOWDUSK_REQUIRE_GL -ErrorAction SilentlyContinue }"
        $newerGates.Add(@{ Name = $arm[0]; Action = [scriptblock]::Create($body) })
    }
}
if ($NewerMonoGameOnly) { $gates.Clear() }
$gates.AddRange($newerGates)

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
