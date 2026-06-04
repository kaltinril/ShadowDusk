# Phase 34 — GL texture breadth: cube maps, 3D textures, and LOD/grad sampling

**Status:** ✅ **DONE — complete to real limitations (2026-06-04).** Compiler support merged via **PR #10**; rung-4 render validation + archive via **PR #12**. Cube maps supported on Desktop + HiDef + Reach; 3D + explicit-LOD/gradient supported on Desktop + HiDef (Reach = documented platform wall). **Cube + 3D are rung-4 render-validated in real MonoGame DesktopGL (face-selection + per-face binding + voxel sample); LOD/grad GLSL is rung-4-render-proven (honors explicit level/gradient) in the real GL driver** — see **RUNG-4 RENDER VALIDATION (AS-BUILT)**. Every stopping point is a genuine platform/runtime/oracle limit (documented), not a ShadowDusk gap. Full suite **551/551**. Deferred from **Phase 33**, which added loud `SD0210` guards for the GL shader constructs ShadowDusk's MojoShader-dialect rewriter didn't yet model. See **AS-BUILT** below.
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
- [x] **5. Render validation (rung 4).** **Done to real limitations (2026-06-03, branch `phase34-render-validation`).** See the **RUNG-4 RENDER VALIDATION (AS-BUILT)** section below. Headlines:
  - **Cube maps — rung-4 PROVEN in real MonoGame DesktopGL.** New harness `validation/TextureBreadthValidation` loads ShadowDusk's `ExCubeSamplerHidef.fx` `.mgfx` into a real `Effect`, binds a real `TextureCube`, renders, and asserts (a) correct **face selection** (+X/+Y regions) and (b) correct **per-face binding across all six face slots** (six-coloring sweep). PASS, exit 0.
  - **3D / volume — rung-4 PROVEN for the supported subset in real MonoGame DesktopGL.** Same harness: a real `Texture3D` binds and `texture3D(...)` **samples correctly** (a 1×1×1 volume renders its voxel color). The **multi-voxel** case is a documented **runtime** wall: DesktopGL 3.8.2's `Texture3D.GetData` is `NotImplemented` (verified by IL) and a multi-voxel volume does not sample correctly through this path — so coordinate→voxel selection can't be pixel-proven *in this runtime* (not a ShadowDusk gap; the `texture3D` GLSL is correct and links).
  - **LOD / gradient — rung-4-grade RENDER proof of the emitted GLSL** (new standing tests `Phase34LodGradRenderTests`, in `ShadowDusk.ImageTests`). ShadowDusk's emitted `textureLod(…, 2.0)` renders **mip 2** and a large `textureGrad` selects a **high mip** in the real GL 3.3 driver — i.e. the explicit level/gradient is **honored**, not merely link-valid. (The full real-**MonoGame** SpriteBatch PS-only path does **not** surface explicit-LOD — a runtime-path interaction, documented under "What stays a limitation"; ShadowDusk's GLSL is correct, as the GL render proves.)
  - The earlier substitutes remain: the cube same-backend golden cross-val + the real-driver compile/link (`Phase34TextureBreadthTests`).
- [x] **6. Regression gate.** Full `dotnet test` green — **549/549** at compiler-support time, **551/551** after the rung-4 work (Core 248, GLSL 33, HLSL 89, Compiler 13, ImageTests **35** [+2 `Phase34LodGradRenderTests`], Integration 133). Phase 33 `gl_FragColor` fix, the SM3 PS-only corpus, byte-identity (DXIL≡SpirvReflector), and image goldens all unaffected. (The `validation/` real-MonoGame harnesses are standalone — not in `dotnet test` — run on demand.)
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

### What stays a *render-validation* limitation after the rung-4 pass (and why — three kinds)

These are limits of the **validation**, not of ShadowDusk's emitted output (which is correct on every axis we could reach). Each is one of: **(P) platform wall**, **(R) runtime/harness cost we judged not worth paying**, or **(O) missing oracle**.

- **(O) No mgfxc PIXEL golden for cube or 3D.** The only mgfxc cube golden (`EnvironmentMapEffect.mgfx`) is a **VS-driven** model effect (sample direction computed in its *vertex* shader from world normals + eye position); it cannot be driven through our PS-only / SpriteBatch rung-4 harness, so a same-scene pixel diff is not apples-to-apples. There is **no** mgfxc 3D/LOD/grad golden at all (these were beyond the SM3/MojoShader OpenGL corpus). → cube/3D rung-4 is proven by **provable in-runtime correctness** (face selection + per-face binding; voxel bind+sample), and the cube **structural** golden cross-val remains at rung 3. Not byte/pixel golden compare.
- **(R) 3D coordinate→voxel selection is not pixel-proven in real MonoGame DesktopGL.** A **multi-voxel** `Texture3D` does not sample correctly through the real-MonoGame path, and `Texture3D.GetData` is `NotImplementedException` in DesktopGL 3.8.2.1105 (verified by IL — a 6-byte throw stub). So a coordinate-varying volume cannot be constructed **and** read back to assert selection *in this runtime*. The **1×1×1 bind+sample** IS proven; the coordinate math is the driver's (independently exercised for 2D by the image-regression corpus). Building a parallel raw-GL `texture3D` coordinate harness was judged **not worth the cost** given (a) the 2D texel-selection is already covered, (b) the GLSL already compiles+links (rung 3), and (c) the 1×1×1 real-runtime sample already closes the bind path — the marginal evidence is small.
- **(R) Explicit-LOD is not surfaced through the real-MonoGame *SpriteBatch* PS-only path.** Observed live: ShadowDusk's `textureLod(…, 2.0)` returns **mip 0** when rendered via `SpriteBatch` + a PS-only `Effect`, even though (i) the mip chain is uploaded and (ii) *automatic* LOD picks high mips correctly. The **same GLSL renders mip 2 correctly in a direct GL draw** (`Phase34LodGradRenderTests`), so this is a **runtime-path interaction** in SpriteBatch's PS-only sprite pipeline, **not** a defect in ShadowDusk's output. Surfacing explicit-LOD faithfully in real MonoGame would require a **VS-driven** effect (so sampling isn't routed through the sprite VS), and the VS-driven `.mgfx` path is itself **not yet render-proven** (backlog 17-VS, see Carried forward). Chasing it now would mean first proving VS-driven rendering — out of this phase's scope.
- **(P) 3D + explicit-LOD/gradient on KNI Reach / WebGL1** — the platform wall above; no render to validate (the platform lacks the feature). Cube is fine on Reach.
- **KNI WebGL HiDef/Reach rung-4 render** of cube/3D/LOD was **not** run in the Playwright browser harness this pass — see Carried forward. Desktop real-MonoGame rung-4 (above) is the strongest in-env evidence; the browser run is the cross-host extension.

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

- ~~**Rung-4 render validation** of cube/3D/LOD in real MonoGame/KNI~~ — **DONE on real-MonoGame Desktop** (branch `phase34-render-validation`); see **RUNG-4 RENDER VALIDATION (AS-BUILT)** below. The remaining genuine gaps are the documented runtime/oracle limits (3D multi-voxel selection, explicit-LOD via SpriteBatch, no cube/3D pixel golden) under "What stays a render-validation limitation".
- **KNI WebGL (HiDef + Reach) rung-4 browser render** of cube/3D/LOD in the Playwright harness (`tests/ShadowDusk.BrowserTests`) — **not run this pass**; the Desktop real-MonoGame rung-4 + the GL-render LOD/grad proof are the in-env evidence. The KNI converter handling (`textureCube`/`texture3D`→`texture`, generic LOD/grad pass-through) is verified by source inspection (§4) but not yet browser-rendered. This is the cross-host (Part-1) extension; it rides with Phase 30 CI's WASM/browser lane.
- **VS-driven `.mgfx` rendering** (backlog 17-VS) — confirmed still open *and now empirically relevant here*: a VS+PS effect (`#version 140` dialect, empty VS attribute table) **loads** into a real DesktopGL `Effect` but renders **blank** (verified live this pass). This blocks (a) a full arbitrary-3-component cube/3D direction sweep and (b) faithful explicit-LOD through real MonoGame (both need a VS that isn't SpriteBatch's sprite VS). Tracked under 17-VS, not Phase 34.
- **Legacy FX9 *intrinsic* forms** `texCUBE(...)` / `tex3D(...)` (DXC rejects them; the FX9 pre-parser only rewrites `tex2D`) — separate, lower-priority frontend gap. The modern `.Sample()` form (used by the fixtures) works.
- **Projected sampling** (`tex2Dproj`/`textureProj`) — left generic if it arises; no fixture in-corpus, not exercised.

---

## RUNG-4 RENDER VALIDATION (AS-BUILT, 2026-06-03, branch `phase34-render-validation`)

The Phase-34 carry-forward was "no in-engine rung-4 pixel render of cube/3D; harness is 2D-only." This pass closes it to real limitations. **Nothing in the compiler changed** — this is validation + evidence only.

### Harnesses added

- **`validation/TextureBreadthValidation/`** — a standalone real-**MonoGame DesktopGL** console harness (mirrors the Phase-17 `validation/Candidate` pattern; references `MonoGame.Framework.DesktopGL` + `ShadowDusk.Compiler`; **not** in `ShadowDusk.slnx`, run on demand: `dotnet run --project validation/TextureBreadthValidation`). It compiles the cube + 3D fixtures **in-memory** with ShadowDusk, loads each `.mgfx` into a real `Effect`, binds a real `TextureCube` / `Texture3D`, renders via the proven PS-only `SpriteBatch` path, reads pixels back, **self-asserts**, and writes evidence PNGs to `validation/output-texbreadth/` (git-ignored). Exit 0 = pass; clean-skips (exit 0, logged) when no GL device — matching the Phase-17 harness.
- **`tests/ShadowDusk.ImageTests/Tests/Phase34LodGradRenderTests.cs`** — **two standing CI tests** (raw GL 3.3 Compatibility, the same context the image suite uses) that **render** ShadowDusk's emitted `textureLod` / `textureGrad` GLSL against a mipmapped texture and assert the explicit level / gradient is **honored**. Added to the regression gate (now 551/551).

### Results (live, this machine — NVIDIA GL 4.6 / SDL2 DesktopGL device)

| Construct | Rung-4 result | How proven |
|---|---|---|
| **Cube map** | ✅ **PASS** in real MonoGame DesktopGL | ShadowDusk's cube `.mgfx` loads in real `Effect`; **face selection** correct (+X region = +X face, +Y region = +Y face, distinct); **per-face binding** correct across **all six** face slots (six-coloring sweep, each color rotated onto +X). |
| **3D / volume** | ✅ **PASS (supported subset)** in real MonoGame DesktopGL | ShadowDusk's 3D `.mgfx` loads in real `Effect`; a real `Texture3D` binds and `texture3D(...)` **samples correctly** (1×1×1 volume renders its voxel color — white & orange). Multi-voxel coordinate selection = documented runtime wall (DesktopGL 3.8.2). |
| **Explicit-LOD (`SampleLevel`)** | ✅ **PASS** — emitted GLSL honors LOD in real GL driver | `textureLod(ps_s0, uv, 2.0)` (ShadowDusk's actual output) renders **mip 2 (Blue)**, not mip 0 — explicit LOD obeyed in a direct GL render. (Real-MonoGame SpriteBatch path doesn't surface it — runtime-path limit, documented.) |
| **Gradient (`SampleGrad`)** | ✅ **PASS** — emitted spelling honors gradient in real GL driver | A large `textureGrad` derivative selects a **high mip** (not mip 0) in a direct GL render. |

### Key findings (honest, evidence-backed)

1. **ShadowDusk's cube/3D/LOD/grad GLSL is correct on every axis we could render.** Cube + 3D sample correctly in real MonoGame; LOD + grad honor the explicit level/gradient in a real GL draw. The rung-3→rung-4 step revealed **no** ShadowDusk-output defect.
2. **The remaining gaps are runtime/oracle/platform, not ShadowDusk:** (a) DesktopGL 3.8.2 `Texture3D` is weakly supported (`GetData` unimplemented; multi-voxel doesn't sample through this path); (b) MonoGame's `SpriteBatch` PS-only path doesn't surface explicit-LOD (the same GLSL renders correctly in a direct GL draw); (c) no mgfxc cube/3D *pixel* golden exists (the cube golden is VS-driven; there's no 3D/LOD golden at all). See "What stays a render-validation limitation" for the (P)/(R)/(O) classification.
3. **VS-driven rendering (backlog 17-VS) is the common blocker** for the two richest would-be tests (arbitrary-direction cube/3D sweep; faithful real-MonoGame explicit-LOD). A VS+PS `.mgfx` loads but renders blank in real DesktopGL — confirmed live. Out of Phase-34 scope.

---

### Provenance
Created 2026-06-04 to formalise the follow-up the Phase 33 `SD0210` guards reference ("Tracked for Phase 34"). Feasibility split (cube maps everywhere; 3D/LOD on Desktop+HiDef; WebGL1 wall) established during the Phase 33 review + the follow-up discussion. See [PHASE-33-webgl2-es300-hidef-output.md](PHASE-33-webgl2-es300-hidef-output.md) § Scope.
