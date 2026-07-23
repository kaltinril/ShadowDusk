# Phase 54 — DirectX 12 (DXIL) backend

**Track:** Backend breadth (post-1.0), same shape as [Phase 32](DONE/PHASE-32-vulkan-backend.md)
(Vulkan).
**Status:** In progress (created 2026-07-23). Split out of
[Phase 52](PHASE-52-monogame-3.8.5-support.md) Area D per that area's own decision gate ("if
source inspection reveals a full new container format on the scale of Phase 32's Vulkan work,
split Area D into its own scoped phase") — source inspection (see
[appendix](PHASE-54-appendix/dx12-dxil-container-research.md)) confirmed no `PlatformTarget
.DirectX12` exists yet; this is genuine new-backend work, not a render-validation rung over an
already-built target.

**Depends on:**
- [Phase 4](DONE/PHASE-4-dxc-integration.md) — DXC integration; DX12 is a single DXC compile to
  SM6 DXIL (`vs_6_0`/`ps_6_0`, no `-spirv`), reusing the same DXC invocation shape the existing
  `(PlatformTarget.DirectX, *)` reflection-only case already proves works.
- [Phase 18](DONE/PHASE-18-directx-dxbc.md) — the existing DirectX11/DXBC target's
  `DxilReflectionExtractor`/`ReflectionPipeline` reflection code, reused unchanged for DX12 (same
  DXIL reflection-comment format, same SM6 profile).
- [Phase 32](DONE/PHASE-32-vulkan-backend.md) — prior art for the method (source-inspect the real
  container before writing code) and for the reused SM6 `apos-shapes-sm6.fx` fixture branch.
- [Phase 52](PHASE-52-monogame-3.8.5-support.md) — MonoGame 3.8.5 stable is the runtime this phase
  validates against (`MonoGame.Runtime.Windows.DX12`, `<MonoGamePlatform>WindowsDX12</MonoGamePlatform>`).

**Blocks:** [Phase 51](PHASE-51-consolidated-remainder-backlog.md) B1 close-out (now points here
instead of Phase 52 Area D).

> The product is the in-memory `IShaderCompiler` library (CLAUDE.md → THE PURPOSE). DX12 is a
> **sixth** output target of the one faithful pipeline — no substitute compiler, same
> HLSL→DXC→(DXIL) front the existing DirectX11 reflection step already uses. Per the standing
> backwards-compatibility directive, this is **strictly additive**: the MonoGame pin stays
> `3.8.2.1105`, the default `CompilerOptions.MgfxVersion` stays `10`, and DX12 output is never a
> consumer-facing flag — a game that already targets `WindowsDX12` gets it automatically, exactly
> as Vulkan/DesktopVK auto-selects today via `CompilerOptions.Profile`/`GraphicsTarget`.

---

## 1. What source inspection found (full detail: appendix)

Read directly from `MonoGame/MonoGame` tag `v3.8.5`. Full findings, code excerpts, and citations:
[`PHASE-54-appendix/dx12-dxil-container-research.md`](PHASE-54-appendix/dx12-dxil-container-research.md).
Headline points:

- **MGFX profile byte `2`** (`DirectX12ShaderProfile : ShaderProfile` → `base("DirectX_12", 2)`).
  Confirmed free in ShadowDusk's `MgfxProfile` enum (`OpenGL=0, DirectX11=1, Vulkan=80`).
- **Reuses Vulkan's exact SM6 fixture branch** — same macros (`HLSL=1, SM6=1`), same
  `vs_6_0`/`ps_6_0` requirement, same modern `Texture2D`/`SamplerState`/`.Sample()` syntax. No new
  DX12 fixture variant needed; `apos-shapes-sm6.fx`'s existing SM6 branch is reused.
- **Column-major matrices** (`/Zpc`, i.e. DXC's HLSL default — achieved by simply *not* passing
  `-Zpr`), unlike Vulkan (which also omits `-Zpr`, so DX12 and Vulkan agree here) and unlike
  DirectX11 (which compiles through `d3dcompiler_47`, a wholly separate path).
- **The DXC-to-SM6-DXIL compile step already exists in ShadowDusk** as a reflection-only
  side-path for the DirectX11 target (`DxcFlagBuilder`'s `(PlatformTarget.DirectX, *)` case) — this
  is the concrete basis for Phase 52's "DXIL path is already built" claim. What's missing is a real
  `PlatformTarget`, container profile, and writer wrapper to ship that DXIL as an actual output.
- **A small DX12-only wrapper**: `0xB00B00` (uint32 magic) + `samplerMaxSlot` (int32) +
  `textureMaxSlot` (int32) + the raw DXC-compiled bytes, verbatim. Far simpler than Vulkan's
  descriptor-layout wrapper — no bitmasks, no per-binding table.
- **Root signature embedding is an open, empirical question.** The reference compiler embeds one
  via `-rootsig-define`/`-force-rootsig-ver`, but MonoGame's native DX12 layer
  (`CommandContext::CreateDefaultRootSignature`) builds and binds its **own** fixed root signature
  independently — it never reads one from the shader blob. Whether `CreatePipelineState` still
  validates an embedded RTS0 against the explicitly-bound signature (and rejects a mismatch) is
  untested; resolve empirically with the simplest possible shader before replicating the flags.
- **Version forced to 11** for every profile as of 3.8.5 (matches the Vulkan precedent already in
  ShadowDusk).
- **MGFX body (ConstantBuffers/Shaders/Parameters/Techniques, render-state blocks) is unchanged**
  — same conclusion Phase 32 reached for Vulkan; no new sibling writer class.

## 2. Scope & Non-Goals

**In scope:**
- `PlatformTarget.DirectX12` (new enum member) and `MgfxProfile.DirectX12 = 2`.
- Compile routing: a new `(PlatformTarget.DirectX12, ShaderStage.*)` case in `DxcFlagBuilder`
  (SM6, no `-spirv`, no `-Zpr`), wired through `CompilationPipeline`/`EffectCompiler`.
- Reuse of the existing DXIL reflection pipeline (`DxilReflectionExtractor`/`ReflectionPipeline`)
  for DX12 — no new reflection code.
- The `0xB00B00`+2-int wrapper for the DX12 shader-bytecode field.
- Rung-4 render validation: `validation/BaselineDx12` + `validation/CandidateDx12` (mirroring the
  existing `BaselineDx`/`CandidateDx` pattern) over the same PS/SpriteBatch corpus DX11 already
  covers, plus Apos.Shapes + VS-driven coverage mirroring `VsDrivenDx`'s `-- apos` mode. Wired into
  `run-windows-render-gates.ps1` only once actually proven.
- Resolving the root-signature-embedding open question empirically.

**Out of scope / Non-Goals:**
- Bumping the MonoGame pin or the default MGFX version (**rejected by standing directive** —
  CLAUDE.md → *Backwards compatibility*).
- Making DX12 a default/consumer-chosen target, or exposing any ShadowDusk-specific flag to select
  it — seamless rule: auto-selected from the consumer's own target/profile only, same mechanism
  Vulkan already uses.
- Xbox / `_GAMING_XBOX` (`ShaderProfile = 21` in the native caps, a completely different container
  question) — desktop `WindowsDX12` only.
- KNI: KNI ships no DX12 platform (mirrors Vulkan's MonoGame-only status).
- Re-deriving MGFX body sections (ConstantBuffers/Parameters/Techniques/render-state) — confirmed
  unchanged, reused as-is.

## 3. Tasks

1. ~~Source-inspect the real container~~ — done, see appendix.
2. Add `MgfxProfile.DirectX12 = 2` and `PlatformTarget.DirectX12` (ordinal 5), with XML doc
   comments matching the existing style (see how `Vulkan`'s entry documents its profile byte and
   version gating).
3. Add the `(PlatformTarget.DirectX12, ShaderStage.Vertex/Pixel)` case to `DxcFlagBuilder` — SM6,
   no `-spirv`, and widen the `-Zpr` exclusion to cover `DirectX12` alongside `Vulkan`.
4. Wire `CompilationPipeline`/`EffectCompiler` routing: DX12 compiles via DXC (not
   d3dcompiler_47/vkd3d-shader), reuses the existing DXIL reflection extractor, forces the v11
   shader-record shape, and wraps the compiled bytes with the `0xB00B00`+2-int header before
   `MgfxWriter` sees them.
5. Unit/integration test coverage mirroring `VulkanEffectCompilerTests.cs`. Full
   `dotnet test ShadowDusk.slnx` must stay green (CLAUDE.md regression-testing rule).
6. `validation/BaselineDx12` + `validation/CandidateDx12` + `compare_dx12.py`, covering
   `DxShaderInputs.ShaderNames` (the SpriteBatch/PS corpus DX11 already covers).
7. Apos.Shapes + VS-driven DX12 coverage, reusing/extending `VsDrivenDx`'s two-arm pattern.
8. Resolve the root-signature open question against a real MonoGame 3.8.5 `WindowsDX12` device;
   record the answer in the appendix.
9. Wire proven gates into `run-windows-render-gates.ps1`; update all support-surface docs
   (CLAUDE.md's list) once real evidence exists — never mark a doc "proven" ahead of a real
   passing render gate on real hardware.

## 4. Evidence ladder position (CLAUDE.md's rungs)

Tracked honestly per-task in the Status line below rather than claimed wholesale — this phase
does not call itself "done" until rung 4 (real MonoGame `WindowsDX12`, real GPU, pixel-equivalent
to the `dotnet-mgcb`/DXC `DirectX_12` profile golden) is reached for both the PS/SpriteBatch
corpus and the Apos.Shapes/VS-driven corpus, same bar every other backend was held to.

## Status

See the implementation commit(s) on this phase's branch for exactly how far task 3 onward
reached in this pass — recorded honestly rather than pre-claimed here (a stale "done" in a plan
doc is worse than an accurate "in progress").
