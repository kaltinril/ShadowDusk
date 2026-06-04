# Phase 34 — GL texture breadth: cube maps, 3D textures, and LOD/grad sampling

**Status:** 🟡 Planned (created 2026-06-04). Deferred from **Phase 33**, which added loud `SD0210` guards for the GL shader constructs ShadowDusk's MojoShader-dialect rewriter didn't yet model.
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

- [ ] **0. Reproduce + investigate (RED).** Add minimal `.fx` test shaders for (a) a cube map, (b) a 3D texture, (c) `SampleLevel`/LOD. Confirm each currently hits `SD0210` (or fails elsewhere). **Trace where each construct breaks across the *whole* pipeline** (HLSL parse → DXC → reflection → GLSL rewrite → MGFX sampler-type byte) and document what each needs end-to-end. Confirm KNI's converter handling of `textureCube(`/`texture3D(`. Establish the harness scaffolding for cube/3D/LOD render scenes.
- [ ] **1. Cube maps — full support (Desktop + Reach + HiDef).** Rewriter models `samplerCube` + emits `textureCube(`; reflection carries the cube dimension; MGFX `Type` byte set correctly. Remove the cube branch of the `SD0210` guard.
- [ ] **2. 3D textures + LOD/grad — support on Desktop + HiDef.** Emit the capable form; resolve the one-blob design decision per the recommendation above (confirmed by reproduce-first evidence).
- [ ] **3. Reach/WebGL1 wall — honest diagnostic.** Replace the placeholder `(Tracked for Phase 34.)` text in the `SD0210` messages with a clear, user-facing one for the cases that remain genuinely unsupported (e.g. *"3D textures / explicit-LOD sampling are not available on KNI Reach/WebGL1; require HiDef/WebGL2 or desktop"*). Keep a guard only where the construct truly can't be served.
- [ ] **4. Tests — unit + cross-validation.** Rewriter emits the correct builtin per sampler type/dimension; reflection reports the right dimension; MGFX `Type` byte correct. Re-baseline byte-identity/goldens if output changes.
- [ ] **5. Render validation (rung 4).** Cube/3D/LOD shaders render correctly in **real KNI HiDef** + **Desktop**, and cube maps additionally in **real KNI Reach**, via the Playwright/image harness where feasible. Honest reporting if a scene can't be exercised in-env.
- [ ] **6. Regression gate.** Full `dotnet test` green; Phase 33's `gl_FragColor` fix and the basic corpus unaffected; guards still fire for genuinely-unsupported cases (3D/LOD on Reach).
- [ ] **7. Housekeeping.** Update `CHANGELOG.md`; the Phase 33 doc's "Phase 34" references now resolve; `plan.md` index/roadmap; soften the `SD0210` user-facing message (Task 3).

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

### Provenance
Created 2026-06-04 to formalise the follow-up the Phase 33 `SD0210` guards reference ("Tracked for Phase 34"). Feasibility split (cube maps everywhere; 3D/LOD on Desktop+HiDef; WebGL1 wall) established during the Phase 33 review + the follow-up discussion. See [PHASE-33-webgl2-es300-hidef-output.md](PHASE-33-webgl2-es300-hidef-output.md) § Scope.
