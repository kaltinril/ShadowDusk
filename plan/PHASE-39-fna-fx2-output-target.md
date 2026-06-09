# Phase 39 — FNA support: compile `.fx` → D3D9 `fx_2_0` Effects bytecode (MojoShader-loadable)

**Status:** 🔬 **Research / Design (2026-06-08)** — no code yet. This doc is the findings + feasibility + proposed implementation for making ShadowDusk able to produce shaders that load and render in **FNA** (the open-source XNA4 reimplementation by Ethan Lee / flibitijibibo). Own research; not derived from VIC's README/docs work (those changes are not in this branch).

**Track:** Reach (Part 1 of THE PURPOSE) — a *new consumer runtime* (FNA), where FNA's only blessed compiler is the **Windows-only, deprecated `fxc.exe` run under Wine** — exactly the cross-platform gap ShadowDusk exists to close. Additive opt-in target (like Metal/Vulkan), never a change to existing OpenGL/DX11/`.mgfx` v10 output (per `backwards-compat-monogame-382-mgfx-v10`).

---

## TL;DR — Can FNA work with ShadowDusk as-is?

**No.** ShadowDusk today emits MonoGame's **`.mgfx` container** (magic `"MGFX"`, version byte 10) holding either pre-translated **GLSL text** (OpenGL) or **SM5 DXBC** (DirectX). FNA does not understand `.mgfx` at all, and does not understand SM5.

FNA's `Effect(GraphicsDevice, byte[])` consumes the **raw legacy Direct3D 9 "Effects Framework" binary** — the exact bytes `fxc.exe /T fx_2_0` produces (version token `0xFEFF0901`, with **Shader Model 1–3** shader bytecode embedded in the effect's object tables). At runtime FNA hands those bytes to **FNA3D → MojoShader**, which parses the D3D9 effect container, extracts the SM1–3 shaders, and translates them to the active backend (GLSL/SPIR-V/MSL/DXBC). Translation happens **at load time**, not build time.

So there are **two independent incompatibilities**, not one:

1. **Wrong container** — FNA wants the `fx_2_0` D3DX effects binary, not `.mgfx`.
2. **Wrong shader model** — FNA (via MojoShader) tops out at **`vs_3_0`/`ps_3_0`**; ShadowDusk emits SM5 DXBC (DirectX) or SM5-derived GLSL (OpenGL).

To support FNA we need a **new output target** that produces an `fx_2_0` effects binary with embedded SM1–3 shaders. The hard part is *producing that format cross-platform without `fxc.exe`*.

---

## Why do this (the reason for the research)

- **FNA has the same pain ShadowDusk was built to kill.** FNA's documented shader workflow is `fxc.exe /T fx_2_0 MyEffect.fx /Fo MyEffect.fxb`, and on Linux/macOS that means installing the **June 2010 DirectX SDK under Wine** (x86-only, fragile). FNA officially recommends no cross-platform compiler. A native `fx_2_0` emitter would remove Wine from the FNA build entirely — a clean, real differentiator.
- **It widens ShadowDusk's reach** from "MonoGame/KNI" to "MonoGame/KNI **+ FNA**" — the two living XNA-family runtimes — from one `.fx` source.
- **It is genuinely additive.** FNA uses a different consumer, container, and shader model, so this is a new `PlatformTarget`, not a change to any existing output. No risk to the validated MonoGame GL (Phase 17) / DX (Phase 18) corpora.

---

## What FNA actually needs (verified)

### The format

| Property | **FNA effect format** | **ShadowDusk's `.mgfx` (today)** |
|---|---|---|
| Container | D3D9 Effects Framework binary; version token **`0xFEFF0901`** (`fx_2_0`) | ASCII magic **`"MGFX"`** + version byte (10) |
| Producer | `fxc.exe /T fx_2_0` (June 2010 DX SDK) / XNA Content Pipeline | `mgfxc` / ShadowDusk |
| Consumer | FNA `Effect` → FNA3D → **MojoShader** (runtime translate) | MonoGame/KNI `Effect` (reads pre-cooked blobs) |
| Shader model | **SM1.x / 2.x / 3.x only** (`vs_3_0`/`ps_3_0` max) | SM5 DXBC (DX11) or SM5-derived GLSL (GL) |
| When translated | **At runtime** (MojoShader) | **At build time** |
| File ext (FNA) | `.fxb` (raw) or `.xnb` (content-wrapped) | `.mgfx` / `.xnb` |

MojoShader's parser defines the byte layout we must hit (`mojoshader_effects.c`): version token, offset, then parameter / technique-pass / object tables; SM1–3 token streams live inside the object table. It also tolerates an XNA4 header (`0xBCF0…`) it skips. **Confirmed against FNA3D.h ("Parses and compiles a Direct3D 9 Effects Framework binary"), FNA `Effect.cs` (pass-through to `FNA3D_CreateEffect`), and MojoShader source.**

### The source language (what authors must write for FNA)

FNA source must compile under `fxc /T fx_2_0`, i.e. **D3D9 / SM3 HLSL** — the *opposite* style from MonoGame DX11:

- **Textures/samplers:** D3D9 `texture T; sampler S = sampler_state { Texture = <T>; };` + `tex2D(S, uv)` — **not** MonoGame's SM4 `Texture2D` / `SamplerState` / `.Sample()`.
- **Semantics:** `POSITION0` / `COLOR0` / `TEXCOORD0` — **not** `SV_Position` / `SV_Target`.
- **Profiles in the technique:** `compile vs_3_0 …` / `compile ps_3_0 …` (`vs_2_0`/`ps_2_0` safest).
- **SM3 ceiling:** no native integer/bitwise ops, no `SampleLevel`/`Gather`/`Load` (use `tex2Dlod`/`tex2Dgrad`), tight dynamic-flow limits, no geometry/compute/tessellation, limited interpolators.
- **MojoShader is *stricter* than `fxc`** on a few SM3 instructions (e.g. `NOT` modifier on TEMP, `TEXKILL` with input register; `saturate()`-on-vectors from `fxc` **debug** builds). Practical rule: **`ps_2_0` is safest; compile optimized, not `/Od`.**
- **`VPOS` gotchas** (FNA docs): floor the VPOS input if you see half-pixel artifacts; **delete** any legacy D3D9 `-0.5/width` half-texel offsets; FNA backends enforce strict vertex-input/VS-input matching.
- **In-pass render states** (AlphaBlendEnable etc.): MojoShader *parses* them, but FNA/XNA4's model expects render state via the managed API (`GraphicsDevice.BlendState`, …). Treat in-pass state as **not reliably honored** — needs a runtime test before relying on it.

**Implication:** the corpus targetable for FNA is the **SM3-expressible subset**. ShadowDusk's SM3 PS-only corpus (Phase 17, 10/10) is exactly the right starting set. SM5-only shaders are out of scope for FNA by construction.

---

## How ShadowDusk is built today (relevant seams)

From the codebase exploration (file:line):

- **Container writer:** [`src/ShadowDusk.Core/MgfxWriter.cs`](../src/ShadowDusk.Core/MgfxWriter.cs) — `"MGFX"` magic (line 14), version byte (line 71), profile byte (`MgfxProfile`, line 72), effect-key MD5 (`ManagedMd5`, lines 85–89), then constant buffers / shader blobs / parameters / techniques+passes / render-state blocks (lines 91–279), trailing `"MGFX"` footer (line 62). **Not reusable for FNA** — different container.
- **Pipeline:** [`src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs`](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs) — FX pre-parse → preprocess → per-pass compile/reflect/transpile → MGFX write. Target branch at line 136 (`options.Target == PlatformTarget.DirectX`). DXBC path lines 468–492; OpenGL path 494–597; Vulkan 600–621.
- **Targets/options:** [`PlatformTarget.cs`](../src/ShadowDusk.Core/PlatformTarget.cs) (`DirectX=0, OpenGL=1, Metal=2, Vulkan=3`), [`CompilerOptions.cs`](../src/ShadowDusk.Core/CompilerOptions.cs), [`DxbcBackend.cs`](../src/ShadowDusk.Core/DxbcBackend.cs) (`D3DCompiler`, `Vkd3d`). A new `PlatformTarget.Fna` slots in here.
- **DXBC backends:** [`Vkd3d/Vkd3dShaderCompiler.cs`](../src/ShadowDusk.HLSL/Vkd3d/Vkd3dShaderCompiler.cs) and [`D3DCompiler/D3DCompilerShaderCompiler.cs`](../src/ShadowDusk.HLSL/D3DCompiler/D3DCompilerShaderCompiler.cs) — **both hardcode `vs_5_0`/`ps_5_0`** and emit **DXBC_TPF (SM5)**. vkd3d invokes `target_type = DXBC_TPF` only. **Neither emits SM1–3 or `fx_2_0` today.**
- **FX pre-parser:** [`src/ShadowDusk.HLSL/FxPreParser.cs`](../src/ShadowDusk.HLSL/FxPreParser.cs) — strips technique/pass blocks and **rewrites SM3 → SM4 forward**: `sampler_state`→`SamplerState`+synth `Texture2D` (333–420), `texture`→`Texture2D` (422–447), `tex2D(...)`→`.Sample(...)` (59–70), `: COLOR`→`: SV_Target` (498–512). **For FNA this forward rewrite must be skipped** — FNA wants the SM3 source kept as-is and fed to an SM3 backend.
- **Reflection / IR:** `ShaderIR`, `SpirvReflector`, parameter/sampler/CB metadata are largely **target-agnostic** and reusable to build the effect's parameter table.

**Reusable for FNA (~40%):** FX block-stripping, preprocessor (with an FNA macro set), render-state parsing (`RenderStateBlock`), reflection data structures. **New work (~60%):** an SM1–3 compile path and an `fx_2_0` container writer. The existing DXBC backends and the MGFX writer do **not** carry over.

---

## The feasibility crux: how to produce `fx_2_0` + SM1–3 cross-platform

### "FNA is open source — can't we just reuse its compiler / fxc?"

No — and the distinction matters. **`fxc.exe` is Microsoft-proprietary and closed**, not part of FNA; FNA merely *recommends running* it. The open-source code FNA ships is **MojoShader**, which is the **reader/translator** (D3D9 bytecode → GLSL/SPIR-V/MSL at runtime) — it does **not** compile HLSL → bytecode. Even Microsoft's *own* open-source compiler, **DXC**, only emits SM6 DXIL / SPIR-V — never SM1–3 or `fx_2_0`. So neither "FNA is open" nor "DXC is open" gives us the HLSL→SM3 *writer* we need.

What *is* open and usable: **vkd3d-shader** (Wine, LGPL) has a real HLSL → SM1–3 backend (the multi-year-hard part), and MojoShader's open parser **defines the `fx_2_0` byte layout** we must emit. So "build our own cross-platform fx_2_0 compiler" is feasible — as **vkd3d (open HLSL→SM3) + a ShadowDusk container writer guided by MojoShader's open parser** — *not* by reimplementing fxc from scratch.

### vkd3d-shader: the open-source HLSL→SM3 frontend

`fxc.exe` is Windows-only and removed from modern SDKs — using it (even under Wine) defeats the purpose. The natural candidate is **vkd3d-shader**, which ShadowDusk *already vendors* and which has both `VKD3D_SHADER_TARGET_D3D_BYTECODE` (SM1–3) and `VKD3D_SHADER_TARGET_FX` (effects). Verified against vkd3d source + release notes (current line **1.18**, Nov 2025):

| Capability | State in vkd3d 1.18 | Usable for FNA? |
|---|---|---|
| HLSL → **`fx_2_0`** with embedded shaders + state | **Not implemented.** `fx.c` explicitly errors: *"Writing fx_2_0 shader objects initializers is not implemented"* and *"…state assignments is not implemented."* | ❌ The one thing FNA needs end-to-end is missing. |
| HLSL → effects (`fx_4_0/4_1/5_0`) | Works but young (`hlsl_compile_effect()` ~MR !1658 mid-2025; shader-type coverage still landing 2026). | ❌ SM4/5 effects — MojoShader can't read these. |
| HLSL → **SM1–3 bytecode** (`vs_3_0`/`ps_3_0`) via `D3D_BYTECODE` | **Real and maturing**, but core texture ops (`tex`, `texbem`, `texcoord`, `bem`) were only landing in **1.18 (Nov 2025)** — expect gaps on real shaders. Emits a **bare SM3 blob**, not the `fx_2_0` container. | ⚠️ Partial — the shader blobs, yes; the container, no. |

**Verdict: vkd3d cannot be a drop-in `fxc /T fx_2_0` today.** But the SM1–3 bytecode backend is the genuinely useful asset we already ship. That points to a **hybrid** implementation:

> **Use vkd3d's `VKD3D_SHADER_TARGET_D3D_BYTECODE` to compile each pass's VS/PS to SM3 bytecode, and have ShadowDusk author the `fx_2_0` effects container itself** (version token, parameter table, technique/pass table, state assignments, embedded shader objects) around those blobs — matching the byte layout MojoShader's `mojoshader_effects.c` parser expects. Then validate the result against MojoShader (and real FNA).

This is the same shape of work that produced the MGFX writer — a faithful binary container emitter — just targeting a *documented, parser-defined* legacy format instead of MonoGame's. It keeps us on the faithful pipeline (no substitute frontend; HLSL→SM3 is a real compile, not a transpile shim) and cross-platform (vkd3d runs on Linux/macOS/Windows, no Wine).

---

## Proposed implementation

### Option A — Hybrid: vkd3d SM3 blobs + ShadowDusk `fx_2_0` writer *(recommended)*

1. **`PlatformTarget.Fna`** in [`PlatformTarget.cs`](../src/ShadowDusk.Core/PlatformTarget.cs) (additive; default stays OpenGL). CLI/`CompilerOptions` opt-in; output extension `.fxb`.
2. **SM3 source mode:** for the FNA target, **skip the FxPreParser SM3→SM4 forward rewrites** (keep `sampler_state`/`tex2D`/`POSITION`/`COLOR`); still strip technique/pass blocks and capture their metadata. Add an FNA macro set in the preprocessor (`#define`s mirroring MonoGame's `Macros.fxh` SM3 branch).
3. **SM3 compile backend:** new `IDxbcShaderCompiler`-shaped path invoking `vkd3d_shader_compile(source=HLSL, target=D3D_BYTECODE, profile="vs_3_0"|"ps_3_0")` → SM3 token-stream blobs. (Extends `Vkd3dShaderCompiler`, which currently hardcodes SM5/`DXBC_TPF`.)
4. **`Fx2EffectWriter`** (new, parallel to `MgfxWriter`): emit the `0xFEFF0901` D3DX effects container — parameter table (from reflection), technique/pass tables, state assignments (from `RenderStateBlock`, where MojoShader honors them), and the embedded SM3 shader objects. Byte layout dictated by `mojoshader_effects.c`.
5. **Validation harness:** parse our `.fxb` with **MojoShader** (the actual FNA consumer); rung-4 = load in **real FNA** (`Effect`) and render-compare against `fxc /T fx_2_0` output for the SM3 corpus — the FNA analog of Phase 17/18.

**Risk:** vkd3d SM1–3 backend gaps (texture ops only just landing in 1.18) may reject some corpus shaders; the `fx_2_0` container layout must match MojoShader exactly (no public spec — the parser *is* the spec). Effort: medium-large; the writer is the bulk.

### Option B — Wait / track upstream

Track vkd3d's `fx.c` until HLSL→`fx_2_0` (embedded shaders + state) lands upstream, then call it directly (no custom container writer). Lowest effort, **indefinite timeline** — the fx_2_0 writer is explicitly stubbed today with no announced date. Good as a parallel watch, not a plan.

### Option C — Bundle `fxc.exe` + Wine

Rejected. Violates "no Wine/Windows SDK" (Core Design Constraint 1) and the whole purpose; just relocates FNA's existing pain into our package.

### Option D — MojoShader *inverse* (bytecode generator)

Rejected. No maintained HLSL→D3D9 generator exists in MojoShader; writing one is larger than Option A and duplicates what vkd3d's SM3 backend already does.

**Recommendation: Option A**, with Option B as a background watch — if upstream lands the fx_2_0 writer first, swap step 4 for a direct vkd3d call.

---

## Definition of done (when pursued)

- A new `PlatformTarget.Fna` compiles an SM3-expressible `.fx` to a `.fxb` whose bytes start with `0xFEFF0901` and parse cleanly in **MojoShader** (no parser errors).
- The `.fxb` **loads in real FNA** (`new Effect(gd, bytes)`) and **renders pixel-equivalent** to the same source compiled with `fxc /T fx_2_0`, for the SM3 PS-only corpus (rung-4 — the FNA analog of Phase 17/18).
- Existing MonoGame GL/DX `.mgfx` output is **byte-unchanged** (no regression; FNA is purely additive).
- Seamless: consumer picks the FNA target once (it's a platform their game already targets — allowed per `seamless-for-end-user`); no Wine, no SDK, no per-shader flags.
- Honest scope note: shaders requiring SM4+ features are **not** FNA-targetable and must fail loudly with a clear diagnostic, not silently degrade.

## Open questions / to verify during implementation

- Exact `fx_2_0` object-table + state-assignment byte layout MojoShader requires (read `mojoshader_effects.c` as the spec; cross-check against a known-good `fxc`-produced `.fxb`).
- Which `RenderStateBlock` states FNA actually honors from inside the effect vs. ignores (needs a runtime test — see language notes).
- vkd3d SM1–3 coverage against our corpus: which shaders compile clean at `vs_3_0`/`ps_3_0`, which hit not-yet-implemented ops; whether `ps_2_0` is a safer default.
- Whether the XNA4 `0xBCF0…` header is needed or if the bare `0xFEFF0901` effect is accepted by FNA (MojoShader skips the XNA header as optional — likely not needed).
- VS-driven FNA effects (FNA enforces strict VS-input matching) — fold in after PS-only proven, mirroring backlog `17-VS`.

## Provenance

Researched 2026-06-08 at user request: "Can FNA work with ShadowDusk as is, and if not, research what we need to do… compile from FX that works for FNA down to FNA's format." Four parallel research agents: (1) FNA effect format / FNA3D / MojoShader, (2) FNA HLSL/FX source-language constraints, (3) ShadowDusk emission internals, (4) vkd3d-shader fx_2_0 / SM1–3 feasibility. All findings source-verified (FNA3D.h, FNA Effect.cs, mojoshader_effects.c, vkd3d `fx.c` + release notes 1.3–1.18, flibitijibibo's writings). Related: `product-is-selfcontained-library`, `seamless-for-end-user`, `backwards-compat-monogame-382-mgfx-v10`, `phase17-monogame-runtime`, `phase18-directx-dxbc`.
