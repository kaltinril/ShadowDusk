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

**In progress (2026-07-23, second pass).** The first pass shipped own-output-only evidence for
the PS corpus and left Apos.Shapes/VS-driven untouched; the user rejected own-output-only as a
completion bar ("if it's not tested, it's not done, period, end of story" — matching the same
oracle rigor DX11/GL/Vulkan are held to), so this pass replaced it with a real reference-compiler
oracle and, in doing so, surfaced a genuine unresolved defect. Both are recorded here honestly.

**Real oracle now exists — PS/SpriteBatch corpus is genuinely oracle-proven:**
- **`dotnet-mgcb` 3.8.5's own `mgcb.dll` content builder can build a real `DirectX_12` profile
  directly** (`/platform:WindowsDX12 /profile:HiDef`) — no separate standalone `mgfxc.exe` is
  needed for 3.8.5 (unlike 3.8.4.1, whose `dotnet-mgcb-editor-windows` package ships one; 3.8.5's
  editor-windows package does not, but the main `dotnet-mgcb` package's content builder handles
  DX12 directly). This resolves the oracle-tool-version question **for DX12 specifically** by
  installing `dotnet-mgcb-editor-windows`/`dotnet-mgcb` 3.8.5 as a **scratch, non-pinned** tool for
  golden generation only — `.config/dotnet-tools.json`'s pinned 3.8.4.1 oracle is untouched (that
  pin is [Phase 52 Area C](PHASE-52-monogame-3.8.5-support.md)'s decision to make, not this phase's).
- Real goldens for all 10 PS/SpriteBatch shaders + `VsTransformColorTexture.fx` +
  `apos-shapes-sm6.fx` were built via the real 3.8.5 content pipeline, extracted from the XNB
  wrapper (`EffectReader`'s raw `.mgfx` payload — same byte format used everywhere else in this
  repo) and checked into `tests/fixtures/golden/DirectX_12/`.
- **`validation/BaselineDx12` + `CandidateDx12` + `compare_dx12.py`: 10/10 MATCH, maxd 0** against
  the real mgfxc golden, real MonoGame 3.8.5 `WindowsDX12`. This is now the same evidence tier
  every other backend's PS corpus was held to — task 6 is genuinely done for this corpus.
- Fixture correctness fix needed to get there: the 10 base-corpus `.fx` fixtures (and
  `VsTransformColorTexture.fx`) gated their modern-syntax/SM6 branch on `#if VULKAN`, which
  DirectX12 does not define (its macro set is `{MGFX, HLSL, SM6}`, no `VULKAN`) — widened to
  `#if SM6` (matching the upstream `apos-shapes-sm6.fx` convention, which already used `SM6` and
  needed no change). Behavior-preserving for every existing target: `SM6` was previously set only
  alongside `VULKAN`, so Vulkan's own branch selection is unchanged; verified by re-running the
  full `run-windows-render-gates.ps1` (every pre-existing gate still green, including both Vulkan
  gates at maxd 0).

**Confirmed, UNRESOLVED defect — Apos.Shapes/VS-driven DX12 (task 7):**
- The prior pass's Task 8 conclusion ("root signature empirically resolved — not needed") was
  **premature**: it was drawn from the PS/SpriteBatch corpus only, and every one of those passes
  has **no `VertexShader` line at all** (falls back to SpriteBatch's own built-in vertex shader),
  so it never actually exercised a ShadowDusk-*compiled* DX12 vertex shader.
- `validation/VsDrivenDx12` (new — mirrors `VsDrivenDx`'s `vs`/`apos` modes) is the first real test
  of that path, using the real `DirectX_12` goldens above as the oracle. Result: **both modes
  load their baseline (real mgfxc) golden and render correctly**, but **ShadowDusk's own compiled
  vertex shader crashes real `WindowsDX12` at draw time** — a native `E_INVALIDARG` (`0x80070057`)
  inside `MGG_GraphicsDevice_DrawIndexed`. `new Effect()` and PSO creation (`pass.Apply()`)
  succeed; the actual `DrawIndexedInstanced` call does not. Reproduces identically for the simple
  `VsTransformColorTexture` rig and for `apos-shapes-sm6.fx` — not fixture-specific.
- Added the missing DXC root-signature-embedding flags real mgfxc's `DirectX12ShaderProfile`
  carries (`-force-rootsig-ver rootsig_1_0 -rootsig-define _MG_ROOT_SIGNATURE -D_MG_ROOT_SIGNATURE=
  "..."`, verbatim from the appendix) as the leading hypothesis — `DxcFlagBuilder`'s
  `Dx12RootSignatureFlags`. **This did NOT fix the crash** (confirmed by direct retest; compiled
  size grew as expected, showing the flag took effect, but the draw still throws identically).
  Does not regress the PS corpus (re-verified maxd 0 after the change).
  Root cause is **still open** — ruled out so far: root-signature embedding (tried, insufficient),
  descriptor-table binding order (code-read: both VS/PS SRV+sampler root tables are bound
  unconditionally whenever any texture/sampler is set, regardless of which stage owns it, so an
  unbound-root-parameter theory doesn't hold up either). Input-layout / vertex-shader input-
  signature mismatch between ShadowDusk's and mgfxc's DXC output remains the leading open
  hypothesis but is unconfirmed — needs either a debug-layer-enabled native DX12 build (the
  shipped `MonoGame.Runtime.Windows.DX12` NuGet is a release build; `_DEBUG`-gated
  `EnableDebugLayer()` is compiled out) or a byte-level DXIL signature-chunk diff.
- **Do not claim DX12 "done" for VS-driven content until this is fixed and `VsDrivenDx12` is
  green.** It is deliberately NOT wired into `run-windows-render-gates.ps1` (which only gates
  proven-green drivers) — run it manually to reproduce: `dotnet run --project
  validation/VsDrivenDx12` and `-- apos`.

**Still not done / next steps:**
- Root-cause and fix the VS-driven DX12 draw crash above (task 7 blocker).
- `docs/the-purpose.md` backend table, `CHANGELOG.md`, `README.md`, and the DocFX site remain
  deliberately NOT updated to claim DX12 "supported" — the PS-corpus-only proof is real but
  partial, and the standing rule is never to pre-claim support ahead of full evidence. Once
  VS-driven is fixed and green, do the consumer-facing docs sweep in the same PR as that fix.
- `docs/validation-matrix.md` and `docs/error-codes.md` updated this pass to state the evidence
  honestly (PS corpus: real oracle, maxd 0; VS-driven: confirmed broken, not just untested).
- Vertex attribute table (mgfxc's DX12 profile collects one, though its own comment says the
  native runtime doesn't use it) is still not built for ShadowDusk's DX12 output — worth
  revisiting once the VS-driven crash is root-caused, in case it turns out to be relevant after
  all (this pass's code-reading did not find where D3D12 would consume it, but the draw-time
  crash means some VS-related assumption in this phase is wrong).
