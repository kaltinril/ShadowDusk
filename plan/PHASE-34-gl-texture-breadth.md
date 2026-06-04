# Phase 34 — GL texture breadth: cube maps, 3D textures, and LOD/grad sampling

**Status:** 🟢 Implemented (2026-06-04, branch `phase34-gl-texture-breadth`). Cube maps supported on Desktop + HiDef + Reach; 3D + explicit-LOD/gradient supported on Desktop + HiDef (Reach = documented platform wall). Deferred from **Phase 33**, which added loud `SD0210` guards for the GL shader constructs ShadowDusk's MojoShader-dialect rewriter didn't yet model. See **AS-BUILT** below.
**Roadmap track:** Fidelity / reach (alongside Phase 28, Phase 33).
**Prereq:** Phase 33 (KNI HiDef/WebGL2) merged to `main` (PR #9).

---

## TL;DR

Phase 33 made ShadowDusk **fail loudly** (`SD0210`) instead of silently emitting broken GLSL when a shader used texture features the GL rewriter didn't model: **cube/3D samplers** and **LOD/projected/gradient sampling**. That was the safe interim — a clear error beats a silent broken render. **Phase 34 lifts those guards by actually supporting each feature *where the target platform allows*,** and keeps a clear, accurate diagnostic only for the one genuine **platform wall** (the old WebGL1 / KNI Reach profile lacks some of these).

These are valid, common HLSL features — the author did nothing wrong. The gap was on *our* (translator) side, plus, for one corner, on the *old platform's* side.

---

## What we can realistically fix — and will

| Feature (HLSL → GLSL) | Desktop GL | KNI HiDef (WebGL2) | KNI Reach (WebGL1) | Plan |
|---|---|---|---|---|
| **Cube maps** (`TextureCube`/`samplerCube`) | ✅ | ✅ | ✅ | **Full support everywhere.** Cube maps exist in GLSL ES 1.00, ES 3.00, and desktop GL. Highest value (skyboxes, reflections), cleanest fix. KNI's converter already rewrites `textureCube(`→`texture(` for HiDef — no KNI dependency. |
| **3D / volume textures** (`Texture3D`/`sampler3D`) | ✅ | ✅ | ❌ (platform wall) | **Support on Desktop + HiDef.** Emit `texture3D(` (KNI converts for HiDef). Not possible on Reach (see wall). |
| **Explicit-LOD / gradient sampling** (`SampleLevel`, `SampleGrad`) | ✅ | ✅ | ❌ (platform wall) | **Support on Desktop + HiDef.** Emit `textureLod`/`textureGrad` (core in ES 3.00 + desktop). Not reliable on Reach (see wall). |
| **Projected sampling** (`tex2Dproj`/`textureProj`) | ✅ | ✅ | ⚠️ | Include if it falls out of the LOD/grad work naturally; lower priority. |

## What we **cannot** fix — and exactly why (the platform wall)

- **3D textures and explicit-LOD/gradient sampling on KNI Reach / WebGL1.** This is **not a ShadowDusk limitation** — the *destination platform* lacks the capability:
  - **WebGL1 (OpenGL ES 2.0) has no 3D textures at all** — added only in WebGL2 (ES 3.0).
  - **Explicit-LOD / gradient sampling in a *fragment* shader is not core WebGL1** — it's gated behind an optional, non-guaranteed extension (`GL_EXT_shader_texture_lod`, `texture2DLodEXT`). Many devices have it; the standard doesn't require it.
- No translation can add a feature a platform never had. The ceiling is **WebGL1's**, not ours — analogous to translating a word into a language that has no equivalent term.
- **Cube maps are unaffected** — they *are* in WebGL1, so we support them on Reach too.
- These features were also largely **beyond what the old MonoGame/`mgfxc` OpenGL (SM3/MojoShader) path ever supported**, so we are not losing ground the original tool had.

**Consequence:** a shader using 3D textures or explicit-LOD requires **HiDef/WebGL2 or desktop**; it cannot run on Reach/WebGL1. Phase 34 makes that an honest, documented outcome (clear message where detectable), never a silently-wrong render.

---

## The core design tension — one OpenGL blob, two KNI profiles

ShadowDusk emits **one** GLSL blob for the `OpenGL` target and **does not know at compile time** whether the consumer's KNI game is Reach (WebGL1) or HiDef (WebGL2) — that single-blob seamlessness is exactly what Phase 33 established (one `.mgfx` serves both; no profile knob, by design). For a feature that exists on HiDef/Desktop but **not** Reach (3D textures, explicit-LOD), the single blob can be:

- **(a) emitted in the capable form** → works on Desktop + HiDef; fails to load on Reach (platform genuinely lacks the feature);
- **(b) hard-blocked (`SD0210`)** → denies the feature to capable Desktop/HiDef users too;
- **(c) degraded for Reach** → unfaithful (changes the rendered result — violates the "same as `mgfxc`/intended" bar).

**Recommended (confirm reproduce-first):** **(a)** for 3D/LOD — enable the feature on the targets that support it rather than denying everyone, and **document** that such shaders require HiDef/desktop. Cube maps need no tradeoff (work everywhere). Where a Reach incompatibility is detectable, surface a clear message. The reproduce-first step must confirm the real behavior: does emitting `textureLod`/`texture3D` compile on Desktop GL as-is? does KNI HiDef's converter pass it through cleanly? what exactly is the Reach failure mode?

*(Rejected, as in Phase 33: adding a per-GL-profile output or a `CompilerOptions` "GL profile" knob — anti-seamless. Out of scope.)*

---

## Components likely touched (deeper than Phase 33's one-spot fix)

Cube/3D support spans more than the GLSL rewriter — **verify each, don't assume:**

- **FX9 pre-parser / HLSL frontend** — recognise `TextureCube`/`Texture3D` and `SampleLevel`/`SampleGrad` (and any FX9 `texCUBE`/`tex3D` forms).
- **Reflection** (`SpirvReflector` + the DXIL oracle) — the **sampler dimension** (2D vs Cube vs 3D) must be reflected.
- **GLSL emission** (`MonoGameGlslRewriter`) — model non-2D sampler declarations; emit the matching texture builtin (`textureCube`/`texture3D`/`textureLod`/`textureGrad`) instead of forcing `texture2D`.
- **MGFX writer** — the per-sampler **`Type` byte** must encode the sampler dimension the MonoGame/KNI runtime expects (2D / Cube / Volume(3D)). **Verify against the format / MonoGame's reader — do not invent an encoding.**
- **Validation harness** — cube/3D/LOD test shaders and render scenes (a cube-map sample scene is new harness work).

---

## Tasks (reproduce-first → fix → validate — same discipline as Phase 33)

- [x] **0. Reproduce + investigate (RED).** Done by the reproduce-half — `tests/ShadowDusk.GLSL.Tests/PHASE34-INVESTIGATION.md` is the component-by-component map (RED reproduction, full-pipeline trace, verified MGFX sampler-`Type` bytes, KNI-converter + WebGL1 walls, the one-blob design evidence). 3D fixture `ExVolumeTextureHidef.fx` added.
- [x] **1. Cube maps — full support (Desktop + Reach + HiDef).** Rewriter models `samplerCube` (renamed to `ps_s{k}`, kept), emits `textureCube(`; the cube **dimension** is sourced from the matched `TextureReflection.Dimension` and the MGFX sampler-`Type` byte is written as `1`. Cube branch of the guard removed.
- [x] **2. 3D textures + LOD/grad — support on Desktop + HiDef.** Resolved as **option (a)**: 3D emits `sampler3D` + `texture3D(` (`Type=2`); LOD/grad keep the **generic** `textureLod`/`textureGrad` (NOT `texture2DLod`, which KNI HiDef can't convert). No per-profile knob.
- [x] **3. Reach/WebGL1 wall — honest diagnostic.** The `(Tracked for Phase 34.)` placeholder is gone. 3D/LOD on Reach is **not** compile-time detectable (one blob, unknown consumer profile), so it is a **documented limitation** (CHANGELOG + this doc), mirroring the Phase-33 KNI-version-floor pattern. The remaining loud `SD0210` fires only for still-unmodelled kinds (array/shadow samplers), with placeholder text removed.
- [x] **4. Tests — unit + cross-validation.** Rewriter unit tests (per-dimension builtin + decl rename + `Dimension`; mixed 2D+cube; LOD/grad generic; cube/3D un-guarded; array/shadow still loud); integration fixtures flipped to success; **cube cross-val vs the `mgfxc` `EnvironmentMapEffect` golden** (same legacy form + sampler-`Type` byte 1). Byte-identity/goldens unaffected (2D corpus output unchanged); no re-baseline needed.
- [~] **5. Render validation (rung 4).** **Partial / honest.** Rung-3 achieved in-env: the emitted cube/3D/LOD/grad GLSL **compiles + links in the real GL 3.3 driver** (catches the `texture2D(non-2D)` overload class) and the cube `.mgfx` cross-validates against the `mgfxc` cube golden (same-backend). A full **pixel-equivalence cube/3D render scene** in real MonoGame/KNI was **not** built — the image harness (`ShaderSceneRenderer`) is hardwired to 2D `TextureTarget.Texture2D`; a cube/3D scene (6 faces / volume + direction-vector VS) is non-trivial new harness work. Rung-4 in real KNI HiDef/Reach + Desktop is **carried forward**.
- [x] **6. Regression gate.** Full `dotnet test` green — **549/549** across all 6 projects (Core 248, GLSL 33, HLSL 89, Compiler 13, ImageTests 33, Integration 133). Phase 33 `gl_FragColor` fix, the SM3 PS-only corpus, byte-identity (DXIL≡SpirvReflector), and image goldens all unaffected.
- [x] **7. Housekeeping.** `CHANGELOG.md` updated (Added: cube/3D/LOD/grad; revised the Phase-33 guard "Changed" entry). This plan ticked + AS-BUILT recorded. `plan.md` roadmap updated. No `(Tracked for Phase 34.)` strings remain in shipped error messages.

---

## Validation / success bar

- **Cube maps:** render correctly in **real KNI HiDef + Reach + Desktop** (evidence-ladder rung 4).
- **3D textures + LOD/grad:** render correctly in **real KNI HiDef + Desktop**; documented and (where detectable) clearly diagnosed as Reach-unsupported.
- **No regression:** Phase 33 (`gl_FragColor`), the SM3 PS-only corpus, and byte-identity all stay green.

## Risks / open questions

- **Depth:** touches the HLSL frontend, reflection, the MGFX **sampler-`Type` byte**, and the rewriter — not just one method. Scope each via reproduce-first.
- **MGFX sampler-type encoding** must match what MonoGame/KNI's runtime actually loads — verify against the format, don't invent.
- **The one-blob design decision** for 3D/LOD (the Reach tradeoff) — resolve with reproduce-first evidence, not assumption.
- **Render-validation surface:** cube/3D need new test scenes; LOD needs a mip-mapped texture scene. Some may be CI-only if not exercisable locally.

## What stays a hard error / unsupported after Phase 34 (by design)

- **3D textures and explicit-LOD / gradient sampling on KNI Reach / WebGL1** — the platform wall (WebGL1 lacks the capability). Clear diagnostic, not a silent break. (Cube maps: supported on Reach.)
- Constructs ShadowDusk doesn't model in *any* GL path beyond these (e.g. exotic sampler states) remain out of scope — parity, not unbounded feature growth.

---

## AS-BUILT (2026-06-04)

### What changed, by component

- **Rewriter — `src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs`:**
  - New `MonoGameSamplerDimension` enum (`Texture2D=0`/`TextureCube=1`/`TextureVolume=2`), carried on `MonoGameGlslSampler.Dimension`.
  - `SamplerDecl` regex now captures the kind (`2D`/`Cube`/`3D`); a non-2D decl is renamed to `ps_s{k}` **keeping its kind** and recorded with its dimension.
  - **Per-sampler texture-call rewrite.** SPIRV-Cross emits the *generic* `texture(<sampler>, …)` for every dimension (verified). The rewrite now does, per modelled sampler, `texture(ps_sK, …)` → `texture2D`/`textureCube`/`texture3D(ps_sK, …)` by dimension. The `\btexture\s*\(` pattern intentionally does NOT match `textureLod`/`textureGrad`/`textureProj`.
  - LOD/grad/proj are **kept in their generic spelling** (no down-rewrite to `texture2DLod`). `ThrowIfUnsupportedSampling` + its token list were **deleted**.
  - `ThrowIfUnsupportedSamplerType` narrowed: cube/3D pass; only still-unmodelled kinds (`sampler2DArray`, `sampler2DShadow`, `samplerCubeArray`, …) fail loudly. `(Tracked for Phase 34.)` text removed.
- **Pipeline + MGFX `Type` byte — `src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs`:**
  - The sampler↔texture pairing (which already existed) now also resolves the matched `TextureReflection.Dimension`, and `MgfxSamplerInfo.Type` is set via `SamplerTypeByte(dimension)` → `2D=0, Cube=1, Volume(3D)=2, 1D=3` (was hardcoded `0`).
  - **No reflection-type change** was needed: the dimension is reflected identically by both the DXIL oracle and the pure-managed `SpirvReflector` onto `TextureReflection`, so the MGFX output stays byte-transparent between the desktop and WASM reflection paths (the byte-identity test stays green).
- **`MgfxWriter`:** unchanged — it already wrote `s.Type`; the upstream hardcode was the bug.

### Evidence

- **Cube same-backend cross-val (rung 3):** ShadowDusk's `ExCubeSamplerHidef.fx` `.mgfx` matches the `mgfxc` `tests/fixtures/golden/OpenGL/EnvironmentMapEffect.mgfx` golden — both emit `samplerCube ps_s{k}` + `textureCube(ps_s{k}, …)` and both carry sampler-`Type` byte **1**.
- **Real GL-driver compile + link (rung 3):** the emitted cube (`textureCube`), 3D (`texture3D`), LOD (`textureLod`), grad (`textureGrad`) fragment shaders all compile AND link in the live GL 3.3 Compatibility context (`Phase34TextureBreadthTests`). This is the direct catch for the `texture2D(non-2D)` overload class Phase 33 guarded against.
- **MGFX `Type` bytes (decoded):** cube=1, 3D=2, LOD/grad=0.
- **Regression:** 549/549 `dotnet test`, 0 failures/skips.

### What works vs what stays walled (exact Reach behavior)

| Construct | Desktop GL | KNI HiDef (WebGL2) | KNI Reach (WebGL1) |
|---|---|---|---|
| **Cube map** | ✅ | ✅ (KNI converts `textureCube(`→`texture(`) | ✅ (`samplerCube`/`textureCube` are native ES 1.00) |
| **3D / volume** | ✅ | ✅ (KNI converts `texture3D(`→`texture(`) | ❌ **fails to load** — WebGL1 has no `sampler3D`/`texture3D` (GLSL link error). Documented, not compile-detectable. |
| **Explicit-LOD / gradient** | ✅ | ✅ (generic `textureLod`/`textureGrad` are core ES 3.00, passed through) | ❌/⚠️ **unreliable** — fragment-shader explicit-LOD/grad is gated behind the optional `GL_EXT_shader_texture_lod`; not guaranteed. Documented, not compile-detectable. |

### Carried forward

- **Rung-4 render validation** of cube/3D/LOD in **real MonoGame/KNI** (HiDef + Reach + Desktop) — needs a new cube/3D render scene (6 faces / volume + a direction-vector VS); the current image harness is 2D-only. The cube golden cross-val + real-driver compile/link are the in-env substitutes.
- **Legacy FX9 *intrinsic* forms** `texCUBE(...)` / `tex3D(...)` (DXC rejects them; the FX9 pre-parser only rewrites `tex2D`) — separate, lower-priority frontend gap. The modern `.Sample()` form (used by the fixtures) works.
- **Projected sampling** (`tex2Dproj`/`textureProj`) — left generic if it arises; no fixture in-corpus, not exercised.

---

### Provenance
Created 2026-06-04 to formalise the follow-up the Phase 33 `SD0210` guards reference ("Tracked for Phase 34"). Feasibility split (cube maps everywhere; 3D/LOD on Desktop+HiDef; WebGL1 wall) established during the Phase 33 review + the follow-up discussion. See [PHASE-33-webgl2-es300-hidef-output.md](DONE/PHASE-33-webgl2-es300-hidef-output.md) § Scope.
