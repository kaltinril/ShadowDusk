# Phase 32 — Vulkan Backend (SPIR-V target)

**Track:** Backend breadth (post-1.0).
**Status:** Done (2026-07-18). Reopened from its original "Parked" status once MonoGame 3.8.5
shipped `DesktopVK` as a stable production platform — see *History* below.

**Depends on:**
- **[Phase 4](PHASE-4-dxc-integration.md)** (DXC integration) — the Vulkan target is a single DXC compile to SPIR-V (`vs_6_0`/`ps_6_0` + `-spirv`); the whole frontend is shared with the OpenGL SPIR-V branch.
- **[Phase 30](PHASE-30-ci-and-nuget-release.md)** (cross-platform CI) — the SPIR-V-validity unit/integration tests run on the Linux/macOS/Windows matrix; the render proof itself (below) is Windows-GPU-only, like DX/FNA/KNI-DX.

**Blocks:** nothing on the critical path. This was post-1.0 backend breadth; now shipped.

**Sibling parked backend:** [Phase 4.1 — WASM + DirectX DXBC spike](PHASE-4.1-SPIKE-wasm-directx-dxbc.md)
remains parked for the same reason this phase used to be — no runtime to validate against. This
phase is proof that "parked" is not "abandoned": it reopens the moment the missing runtime exists.

> The product is the in-memory `IShaderCompiler` library (CLAUDE.md → THE PURPOSE). Vulkan is a
> *fourth output target* of that one faithful pipeline — **no substitute compiler**, same
> HLSL→DXC→SPIR-V front the OpenGL path uses.

---

## History

Written 2026-06-03, this phase was parked: MonoGame 3.8 shipped no Vulkan backend and KNI's
Vulkan story was (and remains) unclear, so there was no `mgfxc`-Vulkan baseline and no MonoGame
Vulkan runtime to render-validate against — rung 4 of the evidence ladder (CLAUDE.md → *What
success actually means*) was unreachable, and the plan explicitly capped the ambition at "valid
SPIR-V + well-formed `.mgfx`" (rung 2-3).

**MonoGame 3.8.5 shipped stable on 2026-07-15** with `DesktopVK` as a production platform (not
preview) — `MonoGame.Framework.Native` and `MonoGame.Runtime.Windows.Vulkan` 3.8.5 are both real,
stable NuGet packages that build and run a genuine Vulkan instance/device on real hardware. This
removed the phase's central blocker, and the "provisional v10-placeholder" container-format
assumption in the original plan turned out to be wrong on inspection (real profile byte is `80`,
not `3`; the shader-blob record shape is a real, distinct format, not v10/v11's shape reused). The
phase reopened for the full rung-4 implementation rather than re-closing the narrower, capped
scope.

---

## What shipped

- **Reflection fix.** Vulkan now reflects from its own SPIR-V (`SpirvReflector`) instead of
  falling into the empty-DXIL oracle path that silently zeroed every Vulkan shader's
  parameters/cbuffers/samplers before this phase.
- **The real Vulkan `.mgfx` container**, reverse-engineered from MonoGame 3.8.5's own open-source
  writer/reader (not guessed): profile byte `80` (not `3`); the v11 shader-record shape
  (`SourceFile`/`Entrypoint` always present) forced for Vulkan regardless of
  `CompilerOptions.MgfxVersion`; each shader's `ShaderCode` field is real SPIR-V wrapped in a
  descriptor-layout header (uniform/texture/sampler slot bitmasks, texture-type table, a
  `VkDescriptorSetLayoutBinding`-shaped binding list) that MonoGame's native Vulkan pipeline
  creation reads directly. No new sibling writer class was needed — `VulkanShaderCodeWrapper`
  (`ShadowDusk.Core`) builds this header from reflection data, feeding straight into the existing
  `MgfxWriter`'s generic bytecode-length+bytes field.
- **Three real, root-caused, confirmed-by-native-crash bugs fixed** (all with a demonstrated
  failure case before the fix — a real `AccessViolationException`/`IndexOutOfRangeException` in
  MonoGame's native Vulkan draw path, or a red unit/integration test):
  1. A texture+sampler pair used together must be a single combined `COMBINED_IMAGE_SAMPLER`
     descriptor, not two separate descriptors — `VulkanTextureSamplerBindingRewriter` forces
     paired texture/sampler declarations onto matching explicit registers so DXC's
     `-fvk-t-shift`/`-fvk-s-shift` land them at the same raw SPIR-V binding.
  2. MonoGame's native Vulkan pipeline creation hardcodes the entry point name to `"main"`,
     mirroring real mgfxc's own behavior — ShadowDusk now renames the compiled entry function to
     `main` for Vulkan (`RenameEntryPointToMain` in `CompilationPipeline`), matching every other
     target's use of the shader's real name.
  3. DXC auto-numbers the implicit `$Globals` cbuffer (loose top-level globals with no explicit
     `cbuffer` block) to whatever raw binding it lands on next, and MonoGame's native pipeline
     hardcodes an expectation that it's bound at `0` — `DxcFlagBuilder` now passes
     `-fvk-bind-globals 0 <space>` for both Vulkan stages to pin it.
- **Full corpus render-proven on real DesktopVK**: all 10 standard post-process shaders
  (`validation/CandidateVulkan`) compile via ShadowDusk's own `EffectCompiler` and render
  correctly (visually spot-checked for representative samples — grayscale, sepia, tint) on a real
  Vulkan-capable GPU, not merely "doesn't crash."
- Full regression suite green (whole `dotnet test ShadowDusk.slnx`, ~1970 tests) at every stage.

## The one honest gap: no mgfxc-Vulkan pixel-diff oracle

Every other backend's rung-4 proof is a pixel-diff: ShadowDusk's output vs. the reference
compiler's output, both rendered by the same real engine. For Vulkan, **that comparison is
currently impossible** — not because ShadowDusk's validation is incomplete, but because **real
mgfxc's own compiled Vulkan output crashes on all 10 corpus shaders** when loaded into real
DesktopVK (`IndexOutOfRangeException` in `TextureCollection.set_Item`), a confirmed, independent,
upstream MonoGame bug: `VulkanShaderProfile.CreateShader`'s `SlotOffset` arithmetic subtracts a
fixed 32-slot offset from an auto-numbered (non-explicit-register) resource's raw binding, going
negative and byte-wrapping (e.g. to 224/225) for the common case where a shader doesn't hand-pick
explicit registers. This reproduces with 100% real mgfxc output and zero ShadowDusk code
involved — full technical record in
[`plan/PHASE-32-appendix/vulkan-mgfx-format-spec.md`](PHASE-32-appendix/vulkan-mgfx-format-spec.md).

So the evidence actually reached is: **ShadowDusk's own Vulkan output is rung-4 render-proven
(loads + renders correctly in real DesktopVK)**; a literal pixel-diff against `mgfxc`'s own
output is blocked upstream, not by anything in this repo. `validation/compare_vulkan.py` reports
this plainly (a missing baseline render is not scored as a ShadowDusk failure; a missing/wrong
candidate render is). If MonoGame fixes the `SlotOffset` bug upstream, the same harness starts
doing a real pixel-diff with no changes needed.

---

## Scope & Non-Goals

**In scope (shipped):**
- `EffectCompiler.CompileAsync` produces a correct, real-format Vulkan-profile `.mgfx` for the
  standard 10-shader post-process corpus, reflecting parameters/cbuffers/samplers correctly.
- Render-proof on real MonoGame 3.8.5 DesktopVK (`validation/CandidateVulkan` +
  `validation/BaselineVulkan` + `validation/compare_vulkan.py`), wired into
  `validation/run-windows-render-gates.ps1` behind `-IncludeVulkan` (opt-in, like `-IncludeFna` —
  needs a real Vulkan-capable GPU, no headless CI path).
- Fail loudly (not silently mis-emit) on shapes the real container can't represent: more than one
  constant buffer per shader stage (`SD0026`, matching real mgfxc's own limit) and a
  Vulkan+KNIFX combination (`SD0025` — KNI ships no Vulkan platform).

**Out of scope / Non-Goals:**
- **KNI Vulkan parity.** This phase proves MonoGame **DesktopVK** parity only. KNI's Vulkan story
  remains unconfirmed and separate — do not read anything here as a KNI claim.
- **A pixel-diff against real mgfxc's Vulkan output.** Blocked upstream (see above) until MonoGame
  fixes the `SlotOffset` bug. ShadowDusk's own render correctness is the evidence tier reached.
- A SPIR-V → MSL/Metal path (that is [Phase 31 — Metal/MSL backend](../PHASE-31-metal-msl-backend.md)).
- Vulkan-in-WASM specifics (the WASM DXC already emits SPIR-V; no separate spike needed).

---

## Architecture & key decisions

- **Reuse the OpenGL frontend verbatim.** Vulkan is `HLSL →[DXC]→ SPIR-V`, full stop — the same
  DXC invocation the OpenGL branch makes for its intermediate. No GLSL transpile, no
  `MonoGameGlslRewriter`. This keeps the "one faithful pipeline" invariant.
- **Reflection gate** (`CompilationPipeline.RunAsync`): `reflectFromSpirv` now includes
  `Target == PlatformTarget.Vulkan` unconditionally (Vulkan has no DXIL-oracle alternative, unlike
  OpenGL which only reflects-from-SPIR-V when a factory is injected). Falls back to a plain
  `SpirvReflector()` when no factory is injected (desktop), rather than null-forgiving a factory
  that's only ever supplied on Android.
- **Profile byte is `80`** (`MgfxProfile.Vulkan`), confirmed against MonoGame 3.8.5's own source —
  the original plan's placeholder value `3` was wrong; fixed before any writer code shipped.
- **No new `EffectContainer` value, no new sibling writer class.** Vulkan routes through
  `CompilationPipeline`'s existing `mgfxProfile` switch into `VulkanShaderCodeWrapper`, which
  wraps raw SPIR-V bytes with the descriptor-layout header before handing them to `MgfxWriter`'s
  existing generic bytecode field — the divergence from v10/v11 is confined to *inside* the
  per-shader blob, not the header/cbuffer-list/parameters/techniques sections, so a full sibling
  writer (the `KnifxWriter` pattern) wasn't needed.
- **`VulkanTextureSamplerBindingRewriter` is deliberately whole-file, not `#if VULKAN`-scoped.** A
  texture is often declared unconditionally, shared across all targets, with only its sampler
  differing per `#if` branch — scoping the scan to inside `#if VULKAN` spans misses that shared
  declaration entirely and breaks the pairing (a real regression hit and fixed during this phase).
  The one real hazard from whole-file scanning — FxPreParser's own legacy-sampler-splitting
  synthesizing an unrelated `_SDTexture`-suffixed declaration elsewhere in the file — is guarded
  by name, not by scope.
- **`-fvk-bind-globals`, not a source rewrite**, pins the implicit `$Globals` cbuffer's binding —
  the surgical, DXC-native fix once the actual crash trigger (the auto-numbered raw binding value,
  not the descriptor shape) was isolated via a minimal repro.

---

## Definition of Done

The Vulkan target compiles end-to-end through `EffectCompiler` to the real, MonoGame-3.8.5-shaped
`Vulkan`-profile `.mgfx`, reflects correctly, and **renders correctly in real MonoGame DesktopVK**
for the standard 10-shader corpus — render-proven, not merely structurally valid. The one
remaining gap (a literal pixel-diff against real mgfxc's own Vulkan output) is blocked by a
confirmed, independent, upstream MonoGame bug, documented plainly rather than hidden behind a
weaker proxy claim. KNI Vulkan parity is explicitly out of scope and unconfirmed.

---

## Open questions / risks

- **KNI Vulkan story unclear.** Whether KNI ever ships a Vulkan backend (desktop or WASM via
  WebGPU) is unknown — coordinate with the KNI runtime question raised in
  [Phase 4.1](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) Option D before investing further.
- **The mgfxc `SlotOffset` bug is worth reporting upstream** (repro is isolated and minimal — see
  the appendix doc) but has not yet been filed with MonoGame maintainers as of this writing.
- **`-fvk-bind-globals`'s vertex-stage space (`0`) is unexercised by the current corpus** — no
  fixture in the standard 10 overrides the vertex shader with its own loose-global cbuffer. The
  choice (matching VS's default auto-binding-space) is principled but not yet empirically proven
  the way the pixel-shader case is; worth a targeted fixture if a future VS-side regression shows up.
