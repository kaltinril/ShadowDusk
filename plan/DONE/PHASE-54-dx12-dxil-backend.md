# Phase 54 — DirectX 12 (DXIL) backend

**Track:** Backend breadth (post-1.0), same shape as [Phase 32](PHASE-32-vulkan-backend.md)
(Vulkan).
**Status:** In progress (created 2026-07-23). Split out of
[Phase 52](PHASE-52-monogame-3.8.5-support.md) Area D per that area's own decision gate ("if
source inspection reveals a full new container format on the scale of Phase 32's Vulkan work,
split Area D into its own scoped phase") — source inspection (see
[appendix](PHASE-54-appendix/dx12-dxil-container-research.md)) confirmed no `PlatformTarget
.DirectX12` exists yet; this is genuine new-backend work, not a render-validation rung over an
already-built target.

**Depends on:**
- [Phase 4](PHASE-4-dxc-integration.md) — DXC integration; DX12 is a single DXC compile to
  SM6 DXIL (`vs_6_0`/`ps_6_0`, no `-spirv`), reusing the same DXC invocation shape the existing
  `(PlatformTarget.DirectX, *)` reflection-only case already proves works.
- [Phase 18](PHASE-18-directx-dxbc.md) — the existing DirectX11/DXBC target's
  `DxilReflectionExtractor`/`ReflectionPipeline` reflection code, reused unchanged for DX12 (same
  DXIL reflection-comment format, same SM6 profile).
- [Phase 32](PHASE-32-vulkan-backend.md) — prior art for the method (source-inspect the real
  container before writing code) and for the reused SM6 `apos-shapes-sm6.fx` fixture branch.
- [Phase 52](PHASE-52-monogame-3.8.5-support.md) — MonoGame 3.8.5 stable is the runtime this phase
  validates against (`MonoGame.Runtime.Windows.DX12`, `<MonoGamePlatform>WindowsDX12</MonoGamePlatform>`).

**Blocks:** [Phase 51](../PHASE-51-consolidated-remainder-backlog.md) B1 close-out (now points here
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

**Done (2026-07-23, third pass).** The first pass shipped own-output-only evidence for the PS
corpus and left Apos.Shapes/VS-driven untouched; the user rejected own-output-only as a
completion bar ("if it's not tested, it's not done, period, end of story" — matching the same
oracle rigor DX11/GL/Vulkan are held to). The second pass replaced it with a real
reference-compiler oracle and, in doing so, surfaced a genuine VS-driven draw-crash defect that
it could not root-cause. This third pass root-caused and fixed it by reading MonoGame's actual
current v3.8.5 source directly (not a stale/cached clone — see below) — DX12 is now rung-4 proven
for **both** the PS/SpriteBatch corpus and the Apos.Shapes/VS-driven corpus, maxd 0 against the
real `mgfxc` `DirectX_12` golden, same bar as DX11/GL/Vulkan.

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

**Apos.Shapes/VS-driven DX12 (task 7): FIXED, root-caused against MonoGame's real current source.**
- The prior pass's Task 8 conclusion ("root signature empirically resolved — not needed") was
  **premature**: it was drawn from the PS/SpriteBatch corpus only, and every one of those passes
  has **no `VertexShader` line at all** (falls back to SpriteBatch's own built-in vertex shader),
  so it never actually exercised a ShadowDusk-*compiled* DX12 vertex shader.
- `validation/VsDrivenDx12` (mirrors `VsDrivenDx`'s `vs`/`apos` modes) is the first real test of
  that path, using the real `DirectX_12` goldens above as the oracle. It reproduced a crash:
  ShadowDusk's own compiled vertex shader threw a native `E_INVALIDARG` (`0x80070057`) inside
  `MGG_GraphicsDevice_DrawIndexed` on real `WindowsDX12`, for both `VsTransformColorTexture` and
  `apos-shapes-sm6.fx` — not fixture-specific. `new Effect()` succeeded; the draw call did not.
- **Root cause, confirmed by reading MonoGame's real, current source directly** (the previous
  pass's local clone of `github.com/MonoGame/MonoGame` was stale — its `develop` HEAD predated
  the real DX12 shader-wrapper code entirely, which is why grepping it for `0xB00B00` came up
  empty; re-fetched `upstream` and checked out the real `v3.8.5` tag, dated 2026-07-15, matching
  the actual release): MonoGame's shared managed `VertexInputLayout.GenerateInputElements`
  (`MonoGame.Framework/Platform/Native/VertexInputLayout.Native.cs`, used by every native
  backend including DirectX12 and Vulkan) builds the D3D12 input layout by iterating the vertex
  shader's own per-shader **vertex attribute table** — the same `.mgfx` "attributes" field DX11's
  GL path and Vulkan already populate. ShadowDusk's DirectX12 compile path left this table
  **empty** for every vertex shader (`CompilationPipeline.cs`'s DXIL branch only ever populated
  it for Vulkan). With zero attributes, `GenerateInputElements`'s per-attribute loop
  (`for (i=0; i<inputs.Length; i++)`) never runs — producing a **zero-element input layout** and,
  because its "missing shader input" check lives *inside* that same loop, no error is reported
  either. That empty input layout later fails `CreateGraphicsPipelineState` — called lazily via
  `PipelineStateManager::ApplyCurrentPipelineState()` right before the first `DrawIndexedInstanced`
  — with exactly the observed `E_INVALIDARG`. Confirmed end-to-end: a from-scratch standalone
  D3D12 repro (debug layer + GPU-based validation enabled, MonoGame's exact
  `CreateDefaultRootSignature` root-signature layout reproduced by hand) built a **correct**,
  hand-authored input layout for ShadowDusk's exact shader bytecode and created the PSO cleanly
  with zero validation messages — proving the shader/root-signature was never the problem, only
  the input layout MonoGame derives from the (empty) attribute table.
- Two other real (but non-causal) divergences from the reference compiler were found and fixed
  along the way via direct byte-level comparison against the real `DirectX_12` golden:
  - The DXC root-signature-embedding flags (`-force-rootsig-ver` / `-rootsig-define` /
    `-D_MG_ROOT_SIGNATURE=...`) added in the prior pass were a red herring — the real mgfxc
    golden's compiled DXIL carries **no `RTS0` part at all** (confirmed by decoding it directly),
    so mgfxc's own `DirectX12ShaderProfile` flags are vestigial (no `.fx` source, upstream's or
    ours, carries a `[RootSignature(...)]` attribute referencing the macro, so DXC never actually
    attaches one). Reverted to a plain compile (`Array.Empty<string>()`, matching the `DirectX`
    case), removing the spurious `RTS0`/`STAT` parts our output was carrying that the reference
    never ships.
  - `ParameterListBuilder` was emitting a **4th, standalone `SpriteTextureSampler` parameter**
    the real golden does not have (3 parameters: `WorldViewProjection`, `Tint`, `SpriteTexture` —
    confirmed by decoding the golden's parameter list directly). DirectX11's DXBC reflection path
    already folds sampler+texture into one parameter (`includeSamplerParameters: false`); DX12
    was routed through the generic DXIL `ReflectionPipeline` branch, which defaulted to `true`.
    `ReflectionPipeline.Reflect` now takes an `includeSamplerParameters` flag, set `false` for
    DirectX12 in `CompilationPipeline.cs` (OpenGL/Vulkan behavior unchanged).
- **The fix**: added `DxilVertexInputReflector` (`ShadowDusk.HLSL.Reflection`, mirrors
  `SpirvVertexInputReflector`'s shape but reads `ReflectedEffect.InputSignature` from
  `DxilReflectionExtractor` instead of parsing SPIR-V) and wired it into
  `CompilationPipeline.cs`'s vertex-attribute branch for `PlatformTarget.DirectX12`. Extracted the
  semantic → `VertexElementUsage` mapping shared by both reflectors into
  `ShadowDusk.Core.Reflection.VertexSemanticMapper` so it cannot drift between backends.
  Corrected `SpirvVertexInputReflector`'s doc comment, which had claimed (based on an
  unverified/stale finding) that this table is "inert" on the native backend — false, per the
  above.
- **Verified, all green**: `validation/VsDrivenDx12` (tolerance 4, matching the existing rig) and
  `validation/VsDrivenDx12 -- apos` (**maxd 0**) both pass against the real `DirectX_12` golden.
  The PS/SpriteBatch corpus was re-verified unaffected (`BaselineDx12`/`CandidateDx12`/
  `compare_dx12.py`: still 10/10, maxd 0). Full `dotnet test ShadowDusk.slnx`: 2222/2222 green.
  Full `run-windows-render-gates.ps1` run: every other gate still green (two isolated,
  reproducibility-checked exceptions on this run — Vulkan PS corpus and the ANGLE probe each
  failed on exactly one of several consecutive full-script runs and passed clean standalone and
  on retry; pre-existing environmental flakiness from rapid back-to-back GPU-device churn across
  ~10 heavy render gates in one script invocation, unrelated to this change — neither touches
  vertex-attribute or DX12 code at all). The one **consistently** failing gate, KNI OpenGL
  desktop, is a separate pre-existing "missing reference images" environmental gap, also
  unrelated to DX12.
- Both `VsDrivenDx12` modes are now wired into `run-windows-render-gates.ps1` alongside the PS
  corpus gate (default-ON, matching every other proven backend).

**Still not done / next steps:**
- `docs/the-purpose.md` backend table, `CHANGELOG.md`, `README.md`, and the DocFX site: now
  updateable to claim DX12 "supported" per the seamless-support rule, since both corpora are
  rung-4 proven — do in the docs-audit pass for this PR.
- `docs/validation-matrix.md` and `docs/error-codes.md`: update the DX12 row/cell from "PS corpus
  only" to fully proven.
