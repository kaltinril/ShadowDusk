# Phase 39 — FNA support: compile `.fx` → D3D9 `fx_2_0` Effects bytecode (MojoShader-loadable)

**Status:** ✅ **DONE — all four evidence rungs proven (2026-06-09)** for the PS-only **and VS-driven** corpora: `PlatformTarget.Fna` compiles D3D9-style `.fx` → `.fxb` (`0xFEFF0901`) via vkd3d `D3D_BYTECODE` + the new `Fx2EffectWriter`, **loads in real FNA 26.06 and renders pixel-equivalent to `fxc /T fx_2_0` (rung-4 gate 14/14 — the 10 Phase-17 PS-only shaders + 4 VS-driven effects — plus 12 extended corpus entries, max delta 0 everywhere except Dots at 1/255)**, and ships **self-contained in the NuGet for win-x64 + linux-x64 with cross-host byte-identical output**. The VS-driven validation (same-day follow-up; see *VS-driven rung-4 completion* below) also **empirically confirmed in-pass render states are honored by real FNA**. Remaining scoped follow-ups: macOS vkd3d binaries, CI hosting of the per-RID artifacts (Phase 37 C). See *Implementation record* below. Original research (2026-06-08) follows unchanged.

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

## Implementation record (2026-06-09)

Option A implemented on `feature/phase39-fna-fx2-target`. Preceded by a four-agent review/research
pass (doc-vs-code audit, product-purpose review, MojoShader byte-spec extraction, empirical vkd3d
gate) whose load-bearing outcomes are recorded here.

> **Full evidence record: [PHASE-39-appendix/](PHASE-39-appendix/README.md)** — the complete
> agent reports (A doc-vs-code integration map · B purpose review · C empirical vkd3d gate ·
> D spec-extraction notes · E adversarial review, every finding verbatim · F rung-3/4 harness
> build report incl. the pre-fix discovery run · G the Dissolve / MojoShader-printFloat
> bisection) plus H, the session record (design rationale, cross-phase observations, WSL-build
> and NuGet-testing gotchas, patcher engineering notes, the SD03xx error-code registry). This
> file is the synthesis; the appendices are the primary sources — nothing load-bearing lives
> only in a chat transcript.

### Empirical gate — vkd3d **1.17 suffices; no version bump**

The pinned, already-vendored vkd3d-shader **1.17** binary was tested directly on this corpus
(scratch P/Invoke harness, Windows 11, 2026-06-09):

- **D3D9-style HLSL → `D3D_BYTECODE` works**: correct version tokens (ps_2_0 `0xFFFF0200`,
  ps_3_0 `0xFFFF0300`, vs_3_0 `0xFFFE0300`), and **every successful blob carries a usable CTAB**
  (names, FLOAT4/SAMPLER register sets, registers, matrix register counts).
- **Corpus sweep: 105/107 attempted SM ≤ 3 entry-point compiles succeed.** The 2 failures
  (`ForwardLighting.fx` PS, `DeferredSprite.fx`) are one vkd3d construct gap: int-typed ternary
  in `clip((c < x) ? -1 : 1)` → `E5017` (float literals work). They fail loudly with vkd3d's
  diagnostic — acceptable.
- vkd3d **accepts `sampler_state { … }` initializers, `texture` declarations, `tex2D`, `COLOR`
  semantics, and even whole technique blocks natively** — so FNA mode strips only what we strip
  for metadata anyway.
- One rewrite IS required: D3D9 stage-scoped reservations **`register(vs|ps, rN)` are
  unimplemented in vkd3d 1.17 (`E5017`)**; the plain `register(rN)` form is honored (CTAB
  confirms pinning). Hence `Sm3StageReservationRewriter` (per-stage, literal occurrences).
- vkd3d's own `TARGET_FX`/fx_2_0 writer confirmed unusable (pass assignments not implemented) —
  the container writer is ours, as designed.
- **The doc's "needs 1.18" concern is resolved: the 1.18-landed ops are ps_1_x-era; ps_2_0+ is
  fine on 1.17.** Staying pinned also keeps the validated DirectX SM5 vkd3d output byte-stable.
- **Golden oracles exist**: both `fxc.exe /T fx_2_0` (three local SDKs) and
  `D3DCompile("fx_2_0")` work on Windows and produce **byte-identical** output. Two goldens +
  sources are checked in at `tests/fixtures/golden/FNA/` (test oracles only — fxc never ships).

### What was built

| Piece | Where | Notes |
|---|---|---|
| `PlatformTarget.Fna = 4` | `ShadowDusk.Core/PlatformTarget.cs` | additive; `MgfxVersion`/`DxbcBackend` ignored for Fna |
| FNA macro set `FNA, HLSL, SM3` | `Preprocessor/PlatformMacros.cs` | deliberately NOT `MGFX`/`SM4`/`OPENGL` |
| CLI `/Profile:FNA` | `ShadowDusk.Cli/ArgumentParser.cs` | additive; mgfxc parity unaffected |
| `FxSourceMode.PreserveSm3` | `ShadowDusk.HLSL/FxPreParser.cs` + `FxSourceMode.cs` | passthrough of all D3D9 constructs; technique/annotation stripping + metadata capture unchanged; default mode byte-identical (proven by existing suite) |
| `Sm3StageReservationRewriter` | `ShadowDusk.HLSL/` | per-stage `register(vs\|ps, rN)` → `register(rN)`; comment/string-aware |
| SM ≤ 3 vkd3d path | `Vkd3d/Vkd3dShaderCompiler.cs` + `D3DCompileRequest.ProfileOverride` + `BlobKind.D3dBytecode` | profile ≤ 3 ⇒ `D3D_BYTECODE`; null override ⇒ existing SM5 path untouched |
| `CtabReader` | `ShadowDusk.Core/Reflection/` | D3D9 CTAB = the FNA reflection source (it is what MojoShader binds against); leading-comments scan only; scalar/vector defaults propagated, matrix defaults deliberately not (F2) |
| `Fx2EffectDesc` + `Fx2EffectWriter` | `ShadowDusk.Core/` | the `0xFEFF0901` container writer per `docs/fx2-binary-format.md`; validates every invariant MojoShader doesn't bounds-check; emits only FNA-honored states |
| `Fx2EffectBuilder` | `ShadowDusk.Compiler/Internal/` | CTAB union + sampler/texture parameter assembly (textures before samplers) + MonoGame-ordinal → D3D9 render/sampler-state value maps |
| Pipeline branch | `CompilationPipeline.RunFnaAsync` | fully separate path — DXC/SPIRV-Cross/MGFX never touched ⇒ existing targets' bytes cannot change |
| `D3d9BytecodePatcher` | `ShadowDusk.Core/` | MojoShader-compat post-pass on vkd3d's SM2/3 token streams (texkill writemask, pre-SM3 texld swizzle, ≥2³² def-literal clamp) — FNA path only; see "MojoShader-compat fixes" below + Appendices F/G/H |
| WASM guard | `ShadowDusk.Wasm/WasmShaderCompiler.cs` | `SD0304` clear error (vkd3d has no WASM build; no substitute compiler, ever) |
| Rung-3/4 harness | `validation/FnaValidation/` | real-FNA load + render-compare vs the `D3DCompile("fx_2_0")` oracle; FNA/fnalibs restored via `restore-fna.ps1`, never committed (Appendix F) |
| Spec + goldens | `docs/fx2-binary-format.md`, `tests/fixtures/golden/FNA/` | MojoShader-derived byte spec (pinned icculus/mojoshader `6333f74`); fxc ground truth |

**Profile policy** (the pre-parser sees profiles before macro expansion, so `compile
PS_SHADERMODEL …` arrives as a macro name): literal SM ≤ 3 profile → honored as written; literal
SM4+ → loud `SD0300`; macro/absent → default `vs_3_0`/`ps_3_0`.

**Error codes**: `SD0300` SM4+ profile under Fna · `SD0301` CTAB missing/corrupt · `SD0302`
fx_2_0 writer validation · `SD0303` FNA effect build (struct globals, sampler arrays,
unsupported/FNA-throwing sampler states) · `SD0304` Fna on the WASM host · `SD0305` bytecode
patcher cannot canonicalize (no free temp / predicated / relative addressing).

### Evidence ladder — ALL FOUR RUNGS PROVEN (2026-06-09; per `docs/the-purpose.md`)

1. ✅ **Compiles**: SM3 corpus → `.fxb` via the real pipeline (vkd3d on every host; never the
   d3dcompiler oracle — output is host-independent by construction).
2. ✅ **Structurally well-formed**: `Fx2BinaryValidator` (test-side reimplementation of
   MojoShader's parse rules + FNA's runtime constraints, derived from the spec doc by an
   agent that never read `Fx2EffectWriter` — independent of the writer, not of the spec)
   parses our output **and is calibrated against the real fxc goldens**; CTAB names ⊂
   parameter names, texture-before-sampler ordering, object wiring, padding all enforced.
   The writer itself also enforces the CTAB-name⊆parameters rule at write time.
3. ✅ **Real MojoShader parses + translates** — `validation/FnaValidation` loads every corpus
   `.fxb` through `new Effect(gd, bytes)` in **real FNA 26.06** (FNA3D → MojoShader
   `abdc8036`, D3D11 backend): **26/26 load clean** (after the MojoShader-compat fixes below;
   originally 22/22, +4 VS-driven rows added by the same-day follow-up; the 5 diagnostic
   probe rows the harness also runs load clean too but are excluded from the corpus counts).
4. ✅ **Real FNA renders pixel-equivalent to `fxc /T fx_2_0`** — the same harness compiles
   each shader with BOTH arms (ShadowDusk vs the in-process `D3DCompile("fx_2_0")` oracle —
   proven byte-identical to fxc.exe), renders both — PS-only effects through the normal
   SpriteBatch path over the Phase-17 cat scene with the Phase-17 parameter values,
   VS-driven effects through the custom-geometry quad scene (the Phase-28 analog) — and
   pixel-compares: **GATE 14/14 (the Phase 17 PS-only set + the 4 VS-driven effects) + the
   12 extended corpus entries — every row passes, max per-channel delta 0 except Dots
   (1/255)**. Run it: `validation/FnaValidation/restore-fna.ps1` then `dotnet run -c Release`.

**The Definition of done below is met for the PS-only and VS-driven corpora** — FNA support
may now be stated publicly. (Initially proven PS-only; the 17-VS-analog VS-driven rung-4 was
closed the same day — see *VS-driven rung-4 completion* below.)

### MojoShader-compat fixes the rung-3/4 harness forced (all in `D3d9BytecodePatcher`)

Real-FNA validation surfaced three vkd3d-codegen-vs-MojoShader incompatibilities invisible
to every proxy (fxc never emits these shapes, so MojoShader had never been exercised on them):

1. **texkill partial writemask** (vkd3d emits `.x`/`.y`; MojoShader hard-requires `.xyzw`) —
   fixed by routing through a fresh temp: `mov rK, reg.<replicated-masked-components>` +
   `texkill rK.xyzw`. Semantics-preserving (blind mask-widening would test garbage lanes).
2. **texld src0 swizzle below SM3** (MojoShader forbids pre-SM3) — same fresh-temp routing.
3. **def literals ≥ 2³² print as ±0.0** — vkd3d's `discard` sentinel is −2³² (`0xCF800000`);
   MojoShader's `MOJOSHADER_printFloat` converts magnitudes through a 32-bit `unsigned long`
   (overflow on Windows LLP64), so the translated HLSL read `-0.0` and `texkill`'s `< 0`
   test never fired (Dissolve rendered un-discarded). Fixed by clamping finite `def` floats
   with |f| ≥ 2³² to the same-signed largest float below 2³² (`±0x4F7FFFFF`) — in-place,
   sign (the sentinel's only observable property) preserved. Found by a probe-ladder
   bisection (`validation/FnaValidation/FnaProbe*.fx` — cmp/bool-lerp/ifc all passed; clip
   diverged) and confirmed against MojoShader's source (`mojoshader_common.c:974` printFloat;
   the defect also affects its GLSL/Metal profiles, i.e. FNA's GL backend, identically).

The harness's `FNALoggerEXT` hook captures exact MojoShader errors; fnalibs provenance: the
old `fna.flibitijibibo.com/archive/fnalibs.tar.bz2` URL is dead — natives come from the
`FNA-XNA/fnalibs-dailies` CI artifacts (see `validation/FnaValidation/restore-fna.ps1`).

### Self-contained packaging — CLOSED for win-x64 + linux-x64 (2026-06-09)

The former blocker ("vkd3d not NuGet-packed, win-x64 only") is resolved for the two primary
RIDs; macOS remains pre-wired-but-pending:

- **`ShadowDusk.HLSL.csproj` now packs each restored `tools/vkd3d` binary into the NuGet as
  `runtimes/<rid>/native`** (win-x64 `libvkd3d-shader-1.dll`, linux-x64 `libvkd3d-shader.so.1`;
  osx-x64/osx-arm64 entries are pre-wired and inert until binaries are restored). Packing is
  restore-state-dependent by design — the release pipeline must restore every shipping RID
  before `dotnet pack` (RELEASING.md; hosting the pinned artifacts for CI is still Phase 37 C).
- **A linux-x64 vkd3d-shader 1.17 was built from the pinned release tarball** (Ubuntu 24.04
  under WSL; runtime deps libc/libm only; recipe recorded in `tools/restore.{ps1,sh}` —
  including the `make include/private/vkd3d_version.h` quirk the lib-only target misses).
- **`Vkd3dLoader` gained a `NATIVE_DLL_SEARCH_DIRECTORIES` probe** — framework-dependent NuGet
  consumers resolve natives from the package cache via deps.json search dirs, where neither
  the base-directory probe nor bare-name probing (our file names don't match the logical
  name's default probing) could find them.
- **Proven end-to-end, both OSes**: a scratch consumer app OUTSIDE the repo, referencing only
  the locally-packed `ShadowDusk.Compiler` from a local feed, framework-dependent, compiled an
  FNA effect on **Windows** and on **Ubuntu (WSL)** — and the two outputs are
  **SHA256-identical** (`9DCFEF04…C509`), proving both "add the package, call the API" and the
  cross-host byte-identity promise for the FNA path. No Wine, no SDK, no tools/ directory.

### Known limitations (documented, all fail loudly or are additive)

- vkd3d 1.17 rejects int-typed ternary at SM ≤ 3 (`clip(c ? -1 : 1)`) — 2/48 corpus files;
  write `? -1.0 : 1.0`. **Diagnostic-fidelity gap (Constraint 5):** for this particular
  failure vkd3d 1.17 returns an *empty* messages blob, so the integrated pipeline surfaces
  only `X0000 "Shader compilation failed"` + the file name — the `E5017` detail appears only
  on vkd3d's own debug stderr. Improving that surface (e.g. capturing vkd3d debug output or
  re-running at a higher log level on failure) is a follow-up.
- `TECHNIQUE(…)` macro fixtures (stock XNA effects) are invisible to the pre-parser on **every**
  target (macro-call techniques; pre-parse precedes expansion) — not an FNA regression; they
  need macro-expanded technique extraction to become targetable.
- The parameter table is CTAB-driven: globals the compiler optimized out are absent (mirrors
  the MGFX writer's reflection-driven table). Unused `texture` declarations likewise.
- `register(vs|ps, …)` rewriting covers literal occurrences; macro-generated ones (e.g.
  `Macros.fxh _vs(c0)`) expand inside vkd3d's preprocessor where we can't rewrite — those
  sources also use `TECHNIQUE(…)` and are blocked upstream of this anyway.
- Non-square matrix parameters are rejected (`SD0302`) until F1 (MojoShader/fxc dims-order
  conflict) is settled with a golden; matrix **defaults** bake as zeros until F2 is settled.
- In-pass render states are emitted faithfully (mapped to the FNA-honored set; everything our
  `RenderStateBlock` models maps inside it) — and **proven honored at runtime**: the
  `FnaMultiPassStates` rung-4 row renders its second pass alpha-blended over its first with
  the device blend state pinned Opaque, which is only possible if the pass's
  `AlphaBlendEnable`/`SrcBlend`/`DestBlend` assignments are applied (and persist across
  passes, XNA semantics) by real FNA. Identical in both arms, max delta 0. The honoring
  evidence is the manually inspected 2026-06-09 candidate PNG (cat through half-green),
  now pinned by the harness's automated flat-image guard — the arm-vs-arm compare alone
  could not see an FNA-side honoring regression, which would degrade both arms identically
  (emission, separately, is structurally pinned by `FnaCompileFixtureTests` rung-2 asserts).
- vkd3d 1.17's preprocessor ignores `#line` directives (and logs a `fixme` per compile to its
  debug stderr), so FNA-path diagnostic line numbers reference the *flattened* preprocessed
  text, not the original include structure — unlike the DXC paths.
- Parameter/technique/pass **annotations** are captured by the pre-parser but **not emitted**
  into the fx_2_0 binary (zero-annotation policy; MojoShader and FNA fully tolerate it). fxc
  would emit them; add emission if a real FNA consumer ever reads annotations.
- `CompilerOptions.Debug` is a **deliberate no-op** on the FNA path: vkd3d's d3dbc target has
  no debug-info knob we pass, and FNA's own guidance is that fxc *debug*-style codegen trips
  MojoShader strictness — so Debug can never produce a `.fxb` MojoShader rejects.
- Shared entry points referenced by multiple passes are compiled and embedded once **per
  referencing pass** (no shader-object sharing) — larger `.fxb` than fxc's for multi-pass
  effects reusing entries, behaviorally identical.
- The FNA integration tests **skip (not fail) when the vkd3d native is absent** — which is the
  case in CI today (Phase 37 C). Until vkd3d lands in CI, rung-1/2 evidence is local-machine
  only; the gate is `FnaFactAttribute`/`FnaTheoryAttribute`.

### Adversarial review (2026-06-09) — found & fixed before commit

A four-reviewer adversarial pass (spec-vs-writer byte audit incl. an independent decode of a
2-technique/3-pass/2-sampler probe, regression audit of every modified file, security review,
completeness critic) confirmed the byte layout, determinism, and regression-safety, and found
one real bug plus hardening gaps — all fixed:

- **INT/BOOL parameter defaults were emitted as float bits** (vkd3d's CTAB stores defaults as
  a float register image even for int/bool globals; the writer copied the bits through —
  `int Count = 7;` baked `7.0f`'s bits, which MojoShader/FNA read back as `1088421888`).
  The writer's value-blob emission is now type-aware (float = IEEE bits, int = rounded raw
  dword, bool = 0/1), with byte-level tests.
- **Literal `ps_4_0_level_9_1`-style profiles silently downgraded** to ps_3_0 (they're not in
  the known-profiles list, so they classified as macro names). The profile policy now
  classifies by shape (`vs_`/`ps_` + major digit), so all literal SM4+ forms fail `SD0300`.
- Duplicate render-state keys in a pass threw `ArgumentException` instead of last-wins (fxc
  semantics) — fixed in the FNA path.
- Sampler arrays (`sampler ss[2]`) were silently reshaped to non-arrays — now fail `SD0303`.
- The d3dcompiler_47 oracle silently ignored `ProfileOverride` — now refuses it loudly
  (`SD0210`), so output can never silently depend on backend choice.
- Writer hardening: negative-`Elements`/oversized-blob rejection, ASCII-only name enforcement
  (lossy re-encode would break MojoShader's strcmp binding), CTAB-name⊆parameters enforcement
  at write time, and a scan cap fixing a quadratic-time path in `Sm3StageReservationRewriter`
  on adversarial input.

### Follow-ups

- [x] ~~Rung 3 / Rung 4~~ — done via `validation/FnaValidation` (see Evidence ladder).
- [x] ~~vkd3d per-RID NuGet packing~~ — done for win-x64 + linux-x64 (see Self-contained
      packaging); **osx-x64/osx-arm64 binaries still needed** (csproj entries pre-wired).
- [x] ~~**VS-driven FNA effects** rung-4 validation~~ — done (2026-06-09, same-day
      follow-up): gate 14/14, zero product changes needed. See *VS-driven rung-4
      completion* below.
- [x] ~~In-pass render-state runtime verification in real FNA~~ — closed empirically by the
      `FnaMultiPassStates` rung-4 row (see Known limitations).
- [ ] Host the pinned per-RID vkd3d artifacts for CI/release restore (Phase 37 C — last gap
      between "works from a dev machine pack" and "works from any CI release").
- [ ] Surface vkd3d's `E5017`-class detail when its messages blob is empty (Constraint-5
      diagnostic fidelity; see Known limitations).
- [ ] Consider upstreaming the MojoShader `printFloat` LLP64 fix (helps every MojoShader
      consumer; our def-clamp keeps working regardless).
- [ ] Option B watch: if upstream vkd3d lands a complete fx_2_0 writer, evaluate swapping the
      container step.

### VS-driven rung-4 completion (2026-06-09, branch `feature/phase39-fna-vs-rung4`)

The first follow-up (VS-driven FNA effects, the 17-VS analog) closed the same day —
**harness-only work; the product pipeline needed zero changes**, confirming the writer's VS
states, the CTAB-driven matrix parameters, and the `D3d9BytecodePatcher` were already
stage-correct.

- **Scene:** `validation/FnaValidation` gained `FnaScene.VsQuad` — a custom-geometry
  clip-space quad via `DrawUserIndexedPrimitives` with a `POSITION0/COLOR0/TEXCOORD0`
  vertex declaration (vertices byte-identical to Phase 28's
  `validation/Shared{,Dx}/Vs*EffectImageRenderer`; the declaration is a superset of every
  VS row's input semantics, satisfying FNA's strict VS-input ⊆ declaration matching). No
  SpriteBatch priming: the effect supplies its own VS; multi-pass techniques loop
  `pass.Apply()` + draw, XNA persistence semantics.
- **Gate rows added (all GATE, all PASS, max delta 0):** `VsTransformColorTexture` (the
  Phase-28 fixture: WVP identity, red tint through the VS color path),
  `PolygonLight` (radial light falloff — float4x4 + float2/float3/float uniforms),
  `VertexAndPixel` (three matrix uploads; **exact-dyadic** scale 0.5 + translation 0.25 so
  both arms compute bit-identical vertex positions — the translation row would expose any
  row/column-major mismatch that identity/scale never could; rendered as a visibly
  off-center half-size quad), and `FnaMultiPassStates` (two passes + in-pass states).
- **Non-vacuousness verified by hand-computed expected images** (the Appendix-G/H
  antidote): each candidate PNG of the 2026-06-09 run was visually confirmed to show the
  predicted non-trivial content (off-center quad, radial gradient, tinted cat,
  cat-through-half-green). The gate itself proves arm-vs-arm equivalence, not content;
  the FnaMultiPassStates row's content is additionally pinned by an automated flat-image
  guard (see *Post-review hardening* below).
- **`FnaMultiPassStates.fx` change:** `PlainColorPS` alpha 1.0 → **0.5**. At alpha 1 the
  second pass's opaque green fully occluded the first pass's tinted-texture output — the
  exact vacuous-coverage trap Appendix G documents. At 0.5, both passes contribute to every
  pixel **and** the persisted in-pass blend states become observable (closing the
  render-state follow-up). The rung-1/2 structural pins in `FnaCompileFixtureTests` don't
  pin the literal, so they're unaffected.
- **No regression:** full suite green (739 tests, 0 failed, 0 skipped — FNA integration
  tests included, vkd3d restored locally); the PS-only gate rows re-passed unchanged in the
  same 14/14 run.
- **Post-review hardening (same day, pre-merge multi-agent review of the branch):** the
  gate verdict now also requires both arms to have *rendered cleanly* (a symmetric soft
  failure — e.g. the shared SetParams delegate throwing identically in both arms — could
  previously pass on identical wrong pixels); the VsQuad scene clears `Textures[0..1]`
  between arms (the reference arm runs first, so a candidate whose sampler→texture map
  regressed away would otherwise inherit the oracle's stale binding and render
  pixel-identical — masked); each row's scene flag is verified against whether the
  candidate `.fxb` actually embeds a VS token stream (a misfiled future row would pass
  vacuously); and an automated flat-image guard on the FnaMultiPassStates candidate pins
  the "cat through half-green" observation so an FNA-side state-honoring regression
  (which degrades both arms identically, invisible to the arm-vs-arm compare) fails the
  gate.

## Definition of done (when pursued)

- A new `PlatformTarget.Fna` compiles an SM3-expressible `.fx` to a `.fxb` whose bytes start with `0xFEFF0901` and parse cleanly in **MojoShader** (no parser errors).
- The `.fxb` **loads in real FNA** (`new Effect(gd, bytes)`) and **renders pixel-equivalent** to the same source compiled with `fxc /T fx_2_0`, for the SM3 PS-only corpus (rung-4 — the FNA analog of Phase 17/18). *(Original PS-only scope; the VS-driven analog was closed the same day — see "VS-driven rung-4 completion" above.)*
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
