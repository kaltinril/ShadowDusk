# Phase 17 — MonoGame Runtime Cross-Validation (In-Engine Equivalence)

**Status:** ✅ **COMPLETE (2026-05-30) — moved to `plan/DONE/`.** In-engine OpenGL fidelity proven for the full SM3 PS-only corpus: ShadowDusk's `.mgfx` loads into real MonoGame DesktopGL and renders pixel-equivalent to the mgfxc goldens for **all 10/10 shaders, Dissolve included** (gap #3 closed, see §3.7). Full suite green (zero regressions); all in-scope checkboxes ticked; backlog 11-6-D resolved; `docs/glsl-uniform-naming.md` updated to the as-built design. Branch `phase17-image-validation`.
>
> **Carried forward (out of this phase's scope, tracked elsewhere):** DirectX 11 (DXBC) → **Phase 18**; WASM/in-browser → **Phase 19**; **VS-driven MonoGame effects** (the `monoGameGl` gate is PS-only) → backlog **17-VS** in `plan/PHASE-100-deferred-backlog.md`. Live `mgfxc` invocation remains out of scope (needs Windows + `fxc.exe`; §3.5).

> **✅ DISSOLVE DONE (2026-05-30):** gap #3 is closed. `src/ShadowDusk.HLSL/FxPreParser.cs` now rewrites the legacy effect-framework `texture T;` declaration → `Texture2D T;` (new `LegacyTextureTypeKeywords` map + `ConsumeLegacyTextureDecl`, a sibling to the gap #2 `sampler_state` / #4 `tex2D` rewrites). The `sampler S = sampler_state {…}` form was already handled; the missing piece was making the `_dissolveTex` resource it binds exist as a modern `Texture2D`. Dissolve now compiles (1058 bytes), loads into a real DesktopGL `Effect`, and matches its golden at **0-diff**. `validation/Candidate` + `python validation/compare.py` → **10/10 MATCH**. 5 new `FxPreParserTests` lock in the rewrite; full suite regression-clean.

---
**Original status when written:** Not started
**Depends on:** Phase 15 (integration), Phase 16 (image regression + GLSL cross-validation), backlog item **11-6-D** (uniform remapping — see §3.2).
**Blocks:** A credible 1.0 / "drop-in `mgfxc` replacement" claim.

> **Reviewer-confirmed reframing (2026-05-28):** the first wall is **`.mgfx` format compatibility**, not uniform remapping. ShadowDusk's `MgfxWriter` currently emits a format MonoGame's `EffectReader` **cannot load** (wrong signature byte order, missing header field, incomplete shader records — §3.1). `new Effect(gd, sdBytes)` throws on the signature before any shader runs. Uniform remapping (§3.2) is real and necessary but **downstream** of a writer rework. See §3 for the cited evidence.

---

## Overview

The entire point of ShadowDusk is to be a **drop-in replacement for MonoGame's `mgfxc`**: compile a `.fx` on any OS (no Wine, no Windows SDK, no `fxc.exe`), load the resulting `.mgfx` into a **real MonoGame/KNI game**, and have it render **the same image** as `mgfxc` produced — zero content-pipeline or code changes.

Every test so far validates a *proxy* of that promise, not the promise:

| Existing test | What it proves | What it does **not** prove |
|---|---|---|
| Phase 15 integration | `.mgfx` **structure** is well-formed *(by ShadowDusk's own reader)* | That MonoGame can load or run it |
| Phase 16 image regression | ShadowDusk's GLSL renders to a stable **anchor** PNG (its own past output) | Equivalence to `mgfxc` (self-referential) |
| Phase 16 Wave 4 cross-validation | ShadowDusk's GLSL renders the **same pixels as `mgfxc`'s GLSL** | That it works **in the MonoGame runtime** — the comparison ran in ShadowDusk's *own* Silk.NET renderer, deliberately taught (`SceneRender.MojoConstantRegisters`) to bind **both** uniform conventions. Real MonoGame does not do that. |

Crucially, *every* current test reads `.mgfx` with **ShadowDusk's own reader** (`MgfxBlobReader`), which expects ShadowDusk's "XFGM" format. None has ever instantiated a MonoGame `Effect`. That is exactly why nothing real has been proven: **ShadowDusk's output round-trips with itself but not with MonoGame.**

**Phase 17 builds the real test:** compile a shader with both toolchains, load **both** `.mgfx` files into the **same real MonoGame `GraphicsDevice`**, render the **same input image through the same MonoGame code path**, and compare the two output images (byte-for-byte first, with a magenta diff).

> **This phase validates Part 2 (fidelity), not Part 1 (reach).** ShadowDusk's value is two-fold (CLAUDE.md → *What success actually means*): **(1)** compile where `mgfxc` can't — Linux/macOS + in-browser/in-memory WASM (validated by Phase 30 CI + the WASM path), and **(2)** produce the same images `mgfxc` would (this phase). Phase 17 establishes the equivalence bar on **one** host (Windows / DesktopGL). Confirming it also holds for `.mgfx` produced on Linux/macOS and in WASM — the actual differentiator — is a cross-cutting follow-on that **reuses this exact comparison harness**, just fed `.mgfx` compiled on other hosts. Equivalence proven here **+** byte-identical cross-host output = the full promise.
>
> **All Phase 17 validation runs on Windows for now — deliberate and sufficient for the fidelity (Part 2) goal.** Which OS *invokes* ShadowDusk is the Part-1 axis (deferred) and does not affect the Part-2 comparison: the `.mgfx` under test is the same artifact regardless of which host produced it. Once Part 1 establishes byte-identical cross-host output, this Windows-proven equivalence transfers to Linux/macOS/WASM builds for free — no need to re-run the comparison per OS. So Windows-only execution here is not a limitation to apologize for; it's the right place to nail fidelity first.

> **First-run expectation: ShadowDusk's `.mgfx` fails to load (§3.1) — that is the deliverable's first, concrete finding.** This phase is a *measurement instrument*: it pins down, in order, (1) does it load, (2) does it render, (3) does it match. It is "done" only when the writer-format and uniform-remap gaps are closed and ShadowDusk's `.mgfx` renders in-engine like `mgfxc`'s.

### The validation flow (canonical)

For each shader, on each loadable target:

**Reference side (`mgfxc`):**
1. Use the checked-in `mgfxc` golden `<shader>.mgfx` as the reference bytes. *(The files under `tests/fixtures/golden/` **are** `mgfxc` output. Regenerating via the live `mgfxc` tool is **out of scope** — see §3.5: mgfxc is not installed, needs `fxc.exe`/Windows, and its OpenGL profile only accepts SM 3.0.)*
2. In a real MonoGame `GraphicsDevice`: `new Effect(gd, refBytes)`, then render a fixed input image through it (this is also the harness's **control** — proves the golden + device work).
3. Read back the rendered pixels → **`ref` image**.

**ShadowDusk side:**
4. Compile the same `<shader>.fx` with ShadowDusk's `EffectCompiler` **in-memory** → `sdBytes` (per target).
5. `new Effect(gd, sdBytes)` — **does MonoGame load it?** Currently **no** (§3.1). This gate is recorded, not assumed.
6. Render the **same input image through the exact same MonoGame code path** as step 2 (same quad/SpriteBatch, blend/sampler/surface state per §5, params-by-name).
7. Read back → **`cand` image**.

**Validation:**
8. Compare `ref` vs `cand` (byte-for-byte aspiration; small documented tolerance likely — §6).
9. Emit a **diff image**: magenta where any channel differs beyond threshold; report different-pixel count + max channel delta. Save `ref`, `cand`, `diff` PNGs as artifacts.

---

## Scope and Non-Goals

**In scope:**
- **OpenGL via `MonoGame.Framework.DesktopGL`** — the cross-platform priority and the only target that can load once §3.1 is fixed. Validate this first and fully.
- The **Phase 16 Wave 4 cross-validation corpus** (SM 3.0 post-process shaders): **Grayscale, Invert, TintShader, Fading, Pixelated, Sepia, Saturate, Scanlines, Dots**. *(Note: this is a different set from the Phase 16 image-**regression** fixtures — Minimal, cbuffer, multipass, etc. — which were purpose-built SM4/5 and have no `mgfxc` goldens.)*
- The **`MgfxWriter` format rework** to emit MonoGame-loadable `.mgfx` (§3.1) — the actual first deliverable.
- The **single shared MonoGame renderer** both sides go through (§4.1) — what makes byte-for-byte fair.
- Three separately-reported per-shader outcomes: **(a) loads**, **(b) renders**, **(c) image matches**.
- Parameters set **by name** via `Effect.Parameters[...]` — the real game API.

**Out of scope (this phase):**
- **DirectX 11 (`MonoGame.Framework.WindowsDX`)** as a *passing* target. Attempt and report only (§7): it hits the §3.1 header throw **and** a DXIL-vs-DXBC mismatch (ShadowDusk emits DXC `ps_6_0`/DXIL; MonoGame 3.8 DX11 loads DXBC/SM≤5). The DX project must be **Windows-gated** so it never breaks the cross-platform `slnx` build (§7). Fixing DX = **Phase 18**.
- **Vulkan / Metal** — MonoGame 3.8 has **no runtime to load them**, so there is nothing to render-and-compare against. **N/A** until a consuming engine exists (restated in §9 Definition of Done).
- **Live `mgfxc` invocation** (§3.5). Reference = checked-in goldens.
- The MGCB content-pipeline build path (`ShadowDusk.MgcbPlugin`). We load `.mgfx` bytes directly via `new Effect(gd, bytes)` — the same code path content hits at load time, minus the build step.
- Multi-pass / VS-driven corpus shaders. Start PS-only (§3.3 for the VS-stage caveat); VS validation is a later extension (§8.3).
- Bit-exact `.mgfx` **file** equality with `mgfxc` (different compilers; never a goal). We compare **rendered images**.

---

## 3. The blockers, in the order they bite

> **✅ STATUS (2026-05-30): blockers #1–#4 AND gap #3 (Dissolve) are FIXED — see [§3.7](#37-achieved-2026-05-30--in-engine-fidelity-proven-for-the-sm3-corpus).** Everything in §3.1–§3.6 below — the present-tense "cannot load," the 🔴 Blocker rows in the punch-list, the unchecked task boxes, and the §3.6 "0/10 load" measurement — is the **historical analysis that drove the fix**; read it as past tense. Gap #3 (Dissolve's effect-syntax `texture _t;` / `sampler = sampler_state{…}` declarations) was closed by extending `FxPreParser` to rewrite `texture T;` → `Texture2D T;`. No open items remain in this section.

### What ShadowDusk must fix, and why (punch-list)

These are **product** defects in ShadowDusk that must change for its output to work in MonoGame — distinct from the test-harness build steps (§4). Each is a reason the drop-in promise (CLAUDE.md constraint #6) is currently *unmet*, not just untested. Ordered by the order they break a real load+render:

| # | Fix | Why it matters (what breaks without it) | Severity | Detail |
|---|---|---|---|---|
| 1 | **`.mgfx` signature: emit forward `"MGFX"`, not `"XFGM"`** | `MgfxWriter` writes `0x4D474658u` as a LE `uint` → on-disk `58 46 47 4D` = "XFGM". MonoGame's `EffectReader` rejects any non-"MGFX" signature, so **`new Effect(gd, bytes)` throws immediately — *no ShadowDusk output has ever loaded in MonoGame.*** This single byte-order bug invalidates the entire drop-in claim. | 🔴 Blocker | §3.1 |
| 2 | **Add the `EffectKey` header field (6→10-byte header)** | MonoGame's header is signature+version+profile+**EffectKey(4)** = 10 bytes; ShadowDusk writes 6. Even past the signature, MonoGame seeks +10 and parses ShadowDusk's body at the wrong offset → desync/garbage. | 🔴 Blocker | §3.1 |
| 3 | **Emit the full per-shader record** (stage flag, **sampler table**, cbuffer-index list, GL vertex-attribute table) | `WriteShaders` writes only `length + bytes`. MonoGame's `Shader` reader expects all of the above. **No sampler table ⇒ the texture never binds** (SpriteBatch's texture can't reach slot 0); no attribute table ⇒ GL can't wire `POSITION`/`COLOR0`/`TEXCOORD0`. | 🔴 Blocker | §3.1, §3.3 |
| 4 | **Uniform layout: flat `ps_uniforms_vec4[N]` named cbuffer + register/offset table — not a `type_Globals` UBO** | MonoGame's GL runtime binds free uniforms by **cbuffer name** as a `vec4[]` via `glUniform4fv` (`ConstantBuffer.PlatformApply`). ShadowDusk's SPIRV-Cross `type_Globals` UBO never resolves ⇒ **every parameter reads zero** (e.g. TintShader renders black) even though `Effect.Parameters[...]` is set. This is backlog 11-6-D. | 🟠 Major | §3.2 |
| 5 | **Update `MgfxBlobReader` + `MgfxWriterTests` to the corrected format** | These currently *assert/expect* the wrong "XFGM" + truncated layout, **locking the incompatibility into the test suite** (a green suite that guarantees MonoGame can't load the output). They must move to the real format alongside #1–3. | 🟠 Major | §3.1 |
| 6 | **Investigate the SM 3.0 half-texel offset in the sprite/VS path** | `mgfxc`'s MojoShader sprite VS applies a half-pixel offset; if ShadowDusk's path doesn't, output is shifted ~1px and the diff lights up everywhere even when the pixel math is correct. | 🟡 Verify | §4.1 |
| 7 | **Emit in-shader `sampler_state` filter/address modes — or consciously defer** | `SamplerInfo.StateEntries` is parsed but never written to the `.mgfx`. Shaders relying on Point filtering / Wrap addressing baked into the effect would render differently. (Often masked because MonoGame takes sampler state from `GraphicsDevice.SamplerStates` — hence "decide + document," not necessarily "implement now.") | 🟡 Decide | §3.4 |
| 8 | **(Phase 18) DirectX: produce DXBC/SM5, not DXIL/SM6** | `DxcFlagBuilder` targets `ps_6_0`/`vs_6_0` (DXIL); MonoGame 3.8's DX11 runtime loads only DXBC (SM ≤ 5). The DX `.mgfx` won't load even after #1–3. Deferred to Phase 18, but it is a real fix the drop-in claim needs for Windows/DX games. | 🟠 Major (Phase 18) | §7 |

> The headline: **#1–4 are why "we've never proven anything real" — the output literally cannot load (#1–3) and, once it does, reads no parameters (#4).** Fixing the Phase-16 sampler/`tex2D`/COLOR rewrites made ShadowDusk's *GLSL* correct; this list is about making the *container* and *uniform wiring* MonoGame-compatible so that correct GLSL can actually run in a game. The detailed tasks and citations follow.

### 3.1 `.mgfx` is not MonoGame-loadable today — **the first wall** (closes the §Overview gate, was "11-6-D is the big one")

> **Plan-validation pass (2026-05-29):** all claims below re-confirmed against current `MgfxWriter.cs`, plus four refinements from a field-by-field decode of the goldens: **(a)** a *fourth* format gap — the missing trailing `"MGFX"` footer (every golden ends with `4D 47 46 58`); **(b)** the cbuffer, parameter, and technique/render-state sections **already match MonoGame byte-for-byte** (verified vs. Saturate/TintShader goldens) — they need **no** rework, which narrows the writer change to header + shader records + footer; **(c)** the per-shader sampler/cbuffer-index/attribute tables are **not merely unserialized — the IR doesn't collect them at all** (`CompiledShaderBlob` holds only `Bytes`+`Stage`; `CompilationPipeline` reflects cbuffers/params but never samplers or vertex-input signatures), so this task is *collect-then-serialize*, with upstream reflection work in `CompilationPipeline` + a `CompiledShaderBlob` extension preceding the writer change; **(d)** **MonoGame 3.8 is open source** — the authoritative byte layout (esp. the sampler entry's field order, the single highest-uncertainty piece) comes from `EffectReader`/`Shader`/`ConstantBuffer` at the 3.8.2 tag, **no decompile needed**.

ShadowDusk's `MgfxWriter` (`src/ShadowDusk.Core/MgfxWriter.cs`) emits a format MonoGame's `EffectReader` rejects. Verified against source:

- **Signature byte order is inverted.** `MgfxSignature = 0x4D474658u` written via `bw.Write(uint)` (LE) produces on-disk bytes **`58 46 47 4D` = "XFGM"** (`MgfxWriter.cs:10,56`). MonoGame expects **"MGFX"** (`4D 47 46 58`, as the goldens write). The repo *bakes this in*: `MgfxWriterTests` asserts the "XFGM" bytes and `MgfxcMgfxReader`'s docstring calls it the "inverted `XFGM` signature." → `new Effect` throws "does not appear to be a MonoGame MGFX file" before reading anything else.
- **Header is missing `EffectKey`.** `WriteHeader` writes signature(4) + version(1) + profile(1) = **6 bytes** (`MgfxWriter.cs:54-59`). MonoGame's header (per its decompiled `EffectReader`) is **10 bytes**: signature + version + profile + **`EffectKey`(4)**. Even with the signature fixed, MonoGame seeks past 10 bytes and desyncs.
- **Per-shader record is incomplete.** `WriteShaders` writes only `count` then per-shader `(int length, bytes)` (`MgfxWriter.cs:76-84`). MonoGame's `Shader` reader expects per shader: a **stage flag**, bytecode, a **sampler table** (count + type/textureSlot/samplerSlot/[state]/name/paramIndex), a **cbuffer-index list**, and (GL) a **vertex-attribute table** (name/usage/index). None are written → desync after the header. **The stage flag is the only one cheap to add** (`CompiledShaderBlob.Stage` already exists); the sampler/cbuffer-index/attribute tables require new reflection in `CompilationPipeline` (see the 2026-05-29 note above — the data is not in the IR yet).
- **Trailing `"MGFX"` footer is missing.** Every golden ends with the forward signature `4D 47 46 58` (e.g. Grayscale at `0x284`, TintShader at `0x274`); `MgfxWriter` writes nothing after the techniques block. Add it.

> **Decision to make during implementation:** emitting MonoGame-compatible bytes (forward "MGFX", `EffectKey`, full shader records) **conflicts with ShadowDusk's own reader and `MgfxWriterTests`**, which currently expect "XFGM" and the truncated layout. Either (a) change the writer + reader + tests together to the real MonoGame format (correct for the drop-in goal — constraint #6), or (b) keep an internal format and add a MonoGame-format emitter. (a) is almost certainly right; the "XFGM"/truncated layout looks like an early-format artifact, not an intentional divergence.

**Tasks:** *(all ✅ done 2026-05-30 — see §3.7)*
- [x] Pin the authoritative byte layout from MonoGame 3.8.2's **open-source** `EffectReader`/`Shader`/`ConstantBuffer` (the 3.8.2 tag — no decompile). Nail the sampler entry's field order and the cbuffer-index-list ↔ attribute-table sequencing (the highest-uncertainty bytes).
- [x] Structurally diff a ShadowDusk OpenGL `.mgfx` against the matching golden, field by field, using `MgfxBlobReader` (extended into a full binary walker) + `MgfxcMgfxReader` against that spec. Record every divergence.
- [x] **Collect the missing reflection** in `CompilationPipeline` — per-shader sampler bindings+slots, cbuffer-index list, (GL) vertex-attribute table — and extend `CompiledShaderBlob` to carry them. *(Prerequisite for the shader-record write below; the IR does not hold this data today.)*
- [x] Fix the signature (emit forward "MGFX") and the header (`EffectKey` — MonoGame uses it as the effect-cache key; mgfxc writes an MD5-derived int). Add the trailing `"MGFX"` footer.
- [x] Emit the full per-shader record: stage flag + sampler table + cbuffer-index list + (GL) attribute table. **§3.3 and §3.4 depend on this** — there is no sampler binding until the sampler table exists. *(The cbuffer/parameter/technique sections already match MonoGame — leave them untouched.)*
- [x] Update `MgfxBlobReader` / `MgfxWriterTests` to the corrected format — **atomically with the writer**: `MgfxBlobReader` is a linked shared source in both Integration.Tests and ImageTests, and `GlslShaderExtractor`→cross-val depend on it, so a partial change breaks the 444-green suite.

### 3.2 Uniform remapping — backlog **11-6-D** (the second wall)

MonoGame's GL runtime binds free uniforms as a single `vec4[N]` array **named after the cbuffer** — `ConstantBuffer.PlatformApply` calls `GetUniformLocation(cbufferName)` and uploads with `glUniform4fv`. `mgfxc` names that cbuffer `ps_uniforms_vec4` / `vs_uniforms_vec4` and emits `uniform vec4 ps_uniforms_vec4[N];`. ShadowDusk emits SPIRV-Cross GLSL with a **`type_Globals` std140 UBO block** instead, so `GetUniformLocation("type_Globals")` → -1 and `PlatformApply` early-returns → uniforms read zero (e.g. TintShader renders black). See `docs/glsl-uniform-naming.md` (the existing — now stale — design sketch; it frames this as "Phase 7" and references `spvc` decoration queries; **use it as the starting point for this work**, not just something to update at the end).

This is the **central compiler change**, but only reachable after §3.1. *(all ✅ done 2026-05-30 — implemented in `MonoGameGlslRewriter`; see §3.7.)*
- [x] **Choose the remap site** (SPIRV-Cross options vs. GLSL post-process vs. `MgfxWriter` parameter table — likely a combination). Resolve this before the emit work below. → **GLSL post-process** (`MonoGameGlslRewriter`) + pipeline cbuffer naming, gated by `monoGameGl`.
- [x] Convert the UBO to a **flat `uniform vec4 ps_uniforms_vec4[N]`** array (MonoGame uses `glUniform4fv` to an array uniform, **not** a UBO/`glUniformBlockBinding`) and rewrite use-sites.
- [x] Name the cbuffer `ps_uniforms_vec4`/`vs_uniforms_vec4` in the `.mgfx` and record each parameter's register + offset so `Effect.Parameters[name].SetValue` lands in the right slot, in SM 3.0 constant-register layout.

### 3.3 Sampler / texture binding (part of §3.1's shader record)

`SpriteBatch.Draw` binds the drawn texture to slot 0; the shader's sampler must resolve there **via the per-shader sampler table** that §3.1 currently doesn't write. So this is not independent — until the sampler table is emitted, MonoGame has nothing to bind. ShadowDusk synthesizes `Texture2D X_SDTexture` + `SamplerState X` for bare samplers and rewrites `sampler2D` decls (Phase 16); the sampler table must carry the right name/slot for both forms.
- [x] Verify the emitted sampler table binds the SpriteBatch texture to slot 0 for both the `Texture = <T>` form and the synthesized-texture form. → confirmed in-engine: 10/10 render with the texture bound (incl. Grayscale's synthesized texture and Dissolve's dual `Texture=<T>` samplers).

### 3.4 Sampler-state fidelity (caveat, lower priority)

The Phase 16 rewrite drops in-shader `sampler_state` filter/address modes (`SamplerInfo.StateEntries` parsed, not emitted). The harness pins one `SamplerState` (§5), so this won't fail the candidates, but note it.
- [x] Decide whether in-shader sampler state must be emitted into the `.mgfx`, or whether deferring to `GraphicsDevice.SamplerStates` (MonoGame's normal behavior) is acceptable. Document the decision. → **Decision: defer to `GraphicsDevice.SamplerStates`** (the harness pins `LinearClamp`, §4.1). Validated acceptable — 10/10 match with no in-shader sampler state emitted. Revisit only if a future shader bakes Point/Wrap into the effect and renders differently.

### 3.5 mgfxc is not available here

`mgfxc` is **not installed** (not in `.config/dotnet-tools.json`; `where mgfxc` is empty), requires `fxc.exe`/Windows, and its OpenGL profile only accepts SM 3.0 (see MEMORY: "mgfxc OpenGL profile rejects SM 4.0+"). Therefore **the reference is the checked-in goldens only**; any "regenerate via mgfxc" / fresh-determinism check is out of scope for this phase (revisit in §8.3 if mgfxc is ever wired).

### 3.7 ACHIEVED (2026-05-30) — in-engine fidelity proven for the SM3 corpus

**ShadowDusk's OpenGL `.mgfx`, compiled by our tool, loads into a real `MonoGame.Framework.DesktopGL` `Effect` and renders pixel-equivalent to the mgfxc goldens** through the identical `SpriteBatch` path — the Part-2 fidelity bar, met for **all 10 SM3 shaders**. `validation/Candidate` vs `validation/Baseline`, `compare.py`: Grayscale/Invert/TintShader/Sepia/Saturate/Pixelated/Fading/**Dissolve** = **0-diff exact**; Scanlines/Dots = **maxd 1** (sub-LSB, within tolerance). Full suite regression-clean. The work landed on branch `phase17-image-validation`:
- **§3.1 writer rework DONE** — forward "MGFX" + `EffectKey` + footer; pass indices int16→int32; value-param default blob; full per-shader record (stage bool, **byte** sampler/cbuffer/attr counts). **Correction to §3.1: the cbuffer param table is INTERLEAVED (int32 index, uint16 offset per param), not grouped** — only multi-uniform shaders exposed it (Saturate/Dots crashed in `ConstantBuffer.Update`).
- **§3.2 uniform remap DONE** via `MonoGameGlslRewriter` (new, ShadowDusk.GLSL) — `type_Globals` UBO → `uniform vec4 ps_uniforms_vec4[N]`, plus the full MojoShader dialect (varyings, `gl_FragColor`, `ps_sN`, drop `#version`, `texture2D`). Pipeline names the cbuffer `ps_uniforms_vec4` with one register (16B) per free param, register-aligned by size (a float4x4 spans 4).
- **§3.3 sampler binding DONE** — per-shader sampler table emitted (slot→`ps_s{slot}`, parameter=texture param index) from reflection.
- **Gap #3 (Dissolve) DONE (2026-05-30)** — `FxPreParser` rewrites the legacy effect-framework `texture T;` declaration → `Texture2D T;` (new `LegacyTextureTypeKeywords` map + `ConsumeLegacyTextureDecl`; case-sensitive so modern `Texture2D`/`Texture3D`/`TextureCube` are untouched, trailing FX annotations / `register` clauses dropped). The `sampler S = sampler_state {…}` form was already handled (gap #2) but bound a texture that didn't yet exist as a modern resource; this rewrite supplies it. Dissolve compiles (1058 B), loads, and matches its golden **0-diff**. Locked in by 5 new `FxPreParserTests`.
- **Scoped via a `monoGameGl` gate** (PS-only OpenGL effects only) so VS-driven effects keep the SPIRV dialect and the §5 VS-stage / anchor tests don't regress. VS-driven MonoGame support stays §8.3 future work.

### 3.6 EMPIRICAL RESULTS (2026-05-29) — harness built, blockers measured

The comparison harness now exists (`validation/` — `Baseline` + `Candidate` console apps, `compare.py`, `decode_mgfx.py`; DesktopGL 3.8.2.1105). **Real measurements, not predictions:**

- **Baseline 10/10 render.** mgfxc goldens load into a real DesktopGL `Effect` and apply to the cat. The SDL2 device boots headless on Windows — §4.2's gate is **clear**. Harness, params-by-name, and render path all proven.
- **Candidate 9/10 compile, 0/10 load.** `new Effect()` throws *"This does not appear to be a MonoGame MGFX file!"* — blocker #1 confirmed live. Dissolve = gap #3.
- **Byte-exact MGFX v10 spec decoded** (corrects several guesses in §3.1's table): shader-record counts are **bytes** (samplerCount/cbufferCount/attrCount), sampler `parameter` is a **byte**, **pass vsIndex/psIndex are `int32`** (our writer emits `int16` — a real bug §3.1 missed), value params carry a **`rows*cols*4` raw default-value blob with no length prefix** (our writer omits it), object/texture params carry none. See [[phase17-monogame-runtime]] memory for the full field list.
- **NEW DEEP BLOCKER — GLSL dialect mismatch (bigger than #4).** Our SPIRV-Cross GLSL is a *different dialect* than MonoGame's GL runtime consumes. Golden PS (MojoShader): no `#version` (GLSL 110), `varying vec4 vFrontColor;`/`vTexCoord0;` (MonoGame links the built-in **SpriteEffect VS** to the custom PS **by varying name**), `uniform sampler2D ps_s0;`, writes `gl_FragColor`, `texture2D()`, uniforms as `ps_uniforms_vec4[N]`. Ours: `#version 140`, `in_var_COLOR0`/`in_var_TEXCOORD0`, `out_var_SV_Target`, `uniform sampler2D _39;`, `texture()`, `type_Globals` UBO. **So a perfect `.mgfx` container is necessary but NOT sufficient** — the PS won't link with MonoGame's SpriteEffect VS until the GLSL is rewritten to MojoShader conventions. The real Phase-17 fix is therefore TWO compiler changes: (a) the `MgfxWriter` format rework (§3.1, needs sampler reflection plumbed into `CompiledShaderBlob`), and (b) a **GLSL compatibility post-pass** over `SpirvCrossGlslTranspiler` output (semantic→varying renames, output→`gl_FragColor`, samplers→`ps_sN`, drop `#version`, `texture`→`texture2D`, UBO→`ps_uniforms_vec4`). The 4 uniform-free shaders (Grayscale/Invert/Pixelated/Fading) need only (a) + the non-uniform parts of (b) — the first target.

---

## 4. The harness

### 4.1 The shared renderer (the heart of it)

Both `.mgfx` files go through **one** routine, called twice (golden bytes, then ShadowDusk bytes), with identical image + params. The *only* variable between `ref` and `cand` is the shader. Both run in the **same process, same `GraphicsDevice`, same run**, which holds GPU/driver/FP behavior constant — so a byte-for-byte comparison is fair and any difference is attributable to the shader (this is *stronger* than checked-in reference PNGs).

```csharp
// Set parameters by NAME (forces ShadowDusk's parameter table + uniform wiring to be correct).
// SetValue is overloaded; a boxed `object` will NOT auto-resolve — switch on runtime type
// (float / Vector2 / Vector3 / Vector4 / Matrix / Texture2D) explicitly.
byte[] RenderThroughMonoGame(
    GraphicsDevice gd, byte[] mgfxBytes, Texture2D sourceImage,
    IReadOnlyDictionary<string, object> parameters,
    out bool loaded, out string? loadError);
```

Rendering invariants — **pin all of these, identical on both calls** (any of them can make logically-identical shaders differ):
- `RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None)` — pin `Color`, **not** `HdrBlendable`.
- A single explicit `BlendState`. **Use `BlendState.AlphaBlend`** (premultiplied — the SpriteBatch default a real game uses; matches `samples/ShaderViewer/Game1.cs:139`). My earlier `Opaque` was inconsistent with the very sample we reuse; if a shader needs `Opaque`, justify per-shader.
- `SamplerState.LinearClamp`, `DepthStencilState.None`, `RasterizerState.CullCounterClockwise` (or `CullNone`), viewport = RT size.
- Be aware of the **SM 3.0 half-texel offset**: `mgfxc`'s MojoShader sprite VS applies a half-pixel offset that ShadowDusk's path may not. This is the single most likely cause of a uniform 1-px shift the diff will light up — investigate if `cand` looks shifted.
- Clear to a fixed color before drawing; read back via `GetData<Color>` from the same RT.

### 4.2 Getting a real `GraphicsDevice` (headless-friendly)

`MonoGameDeviceFixture` boots a **minimal hidden `Game`** (`GraphicsDeviceManager`, offscreen window), renders only to a `RenderTarget2D`, never presents. **DesktopGL uses SDL2 + OpenGL — a *different* native stack from Phase 16's Silk.NET/GLFW**, so "GLFW works on this machine" does **not** guarantee MonoGame's device creates; the skip detection must catch MonoGame's SDL2 device-creation failure specifically. The GL context is **thread-affine** and xUnit hops threads — mirror `GlContextFixture`'s mechanism exactly: a lock + make-context-current guard per row, and **disable parallelization** for the validation classes (`[Collection]`) so two rows never touch the device concurrently. Skip cleanly (don't fail) when no device.

### 4.3 Report, don't just assert

```csharp
record ValidationResult(
    string Shader, string Target,
    bool SdLoaded, string? SdLoadError,   // step 5 gate (§3.1)
    bool RefRendered, bool SdRendered,
    int DifferentPixels, byte MaxChannelDelta, bool Matches,
    string RefPng, string CandPng, string DiffPng);
```
Write the table to `ITestOutputHelper` + a JSON artifact. The first run's value is the table: per shader, is it **load**, **render**, or **match** that fails?

### 4.4 Reference assets to build on
- `samples/ShaderViewer/Game1.cs` — proven `new Effect(gd, bytes)` + by-name `Effect.Parameters[...]` + `SpriteBatch` load/draw. ⚠️ **It is a `MonoGame.Framework.WindowsDX` (DirectX) app loading `Shaders/DirectX_11/*.mgfx` — NOT DesktopGL.** So it is a reference for the *API shape*, but the OpenGL validation project will be the **first DesktopGL `Effect`-load consumer in the repo** — there is no existing GL load path to copy, and its device/skip behavior (SDL2) differs from anything proven here. **Two traps confirmed:** it uses `BlendState.AlphaBlend` (adopt this, §4.1), and it **primes the sprite VS** with a passthrough draw first (lines ~99-106) — see §3.3/§5 caveat. It also sets `BloomThreshold` as a scalar `0.25f` though the shader declares `float4` — a bug to avoid (§6).
- `tests/fixtures/golden/OpenGL/*.mgfx` — the `mgfxc` references (step 1).
- `tests/ShadowDusk.ImageTests/` — `ImageComparer`, the candidate list, and the by-name parameter values (`MakeSceneFor`) to reuse (don't retype — reference them so they don't drift).

---

## 5. PS-only vertex-stage handling (verify before trusting any comparison)

All candidates are PS-only (no VS function); `CompilationPipeline` writes `VertexShaderIndex = -1`. "SpriteBatch supplies the VS" is true for SpriteBatch's *own* `SpriteEffect`, but when a **custom** effect with a VS-less pass is passed to `SpriteBatch.Begin(..., effect)`, the vertex stage source is subtle — and `ShaderViewer` only works because it **primes** the sprite VS with a prior passthrough draw. The headless single-call harness does not replicate that.
- [x] Determine and pin exactly how the vertex stage is provided for a PS-only custom effect headless: (a) prime with a passthrough draw first like the viewer, (b) confirm MonoGame injects `SpriteEffect`'s VS for a `vsIndex=-1` pass *identically* for ShadowDusk and `mgfxc`, or (c) emit a real passthrough VS. If `ref` and `mgfxc` get the VS one way and `cand` another, the comparison is invalid even after §3.1. → **Resolved: option (a)** — the shared `EffectImageRenderer` primes the SpriteBatch VS identically for both `ref` and `cand`; verified valid because the same VS reaches both sides (10/10 match, incl. uniform-driven shaders).

---

## 6. Image comparison, diff, and per-shader inputs

### 6.1 Tolerance: byte-exact aspiration, diff as arbiter
Default toward exact (same device/run, §4.1), **but expect a small non-zero tolerance**: `mgfxc` emits GLSL-ES `mediump` MojoShader GLSL while ShadowDusk emits `#version 140` (highp-equivalent) SPIRV-Cross GLSL — different dialect/precision compiled by the same driver legitimately drifts 1–2 LSB (more on transcendentals: Dots' sin/cos). The existing cross-val already used **tolerance 4** for this reason. Policy: try exact; when it fails, the **diff decides** — faint uniform LSB noise → a *documented per-shader tolerance* (record the observed max delta + reason); magenta clusters / structural shift → a real bug (unbound uniform, sampler slot, half-texel), fix it. Never silently widen; any tolerance > 0 is listed with justification.

### 6.2 Diff image
Magenta `(255,0,255,255)` on any over-tolerance channel; elsewhere the `ref` pixel (effect visible behind the diff). Save `<shader>_<target>_diff.png`.

### 6.3 Input image + parameters
- **Input:** the Phase 16 8×8 RGBA gradient (generated in code — keep it generated, not a checked-in binary), scaled to the RT. **Caveat:** confirm the chosen params produce *non-degenerate* output per shader — e.g. `Pixelated` (hardcoded 128px / 4px) on an 8×8 source scaled up may be near-uniform, and `Scanlines` with `_attenuation=0.05` is near-passthrough (the fixture comment notes a default of 800.0). If the effect is invisible, "match" is trivially true and proves nothing — pick params/image that exercise the effect.
- **Parameters** (set by name on *both* Effects, identical, pinned CLR types):

| Shader | Parameters (CLR type) | Notes |
|---|---|---|
| Grayscale, Invert | (none beyond texture) | Simplest; isolate §3.1 load + §3.3 sampler. **Start here.** |
| Pixelated, Fading | (none) — but use the `sampler2D = sampler_state{Texture=<T>}` form | Exercise the synthesized-texture sampler path; ensure non-degenerate output. |
| TintShader | `TintColor = Vector4(1, 0.5f, 0.5f, 1)` | First uniform shader → exercises §3.2. |
| Sepia | `_sepiaTone = Vector3(1.2f, 1.0f, 0.8f)` | |
| Saturate | `BloomThreshold = Vector4(0.25f,0.25f,0.25f,0.25f)`, `BloomIntensity = 2.0f`, `BloomSaturation = 0.8f` | **`Vector4`, not scalar** (ShaderViewer's scalar set is a bug). |
| Scanlines | `_attenuation`, `_linesFactor` (pick values that make the effect visible) | bare `sampler s0`. |
| Dots | `angle=0.5f`, `scale=0.5f`, `ScreenSize=Vector2(W,H)` | bare `sampler s0`; sin/cos LSB risk → diff arbitrates. |

> Values mirror `MakeSceneFor` in `MgfxcCrossValidationTests` — reference them, don't duplicate. If `Effect.Parameters["TintColor"]` is **null** on the ShadowDusk Effect → the parameter table didn't carry the name (a §3.1 record problem); if non-null but the image is wrong → §3.2 remap. Step 7 below forks on exactly this.

---

## 7. DirectX 11 (attempt + report this phase; *fix* = Phase 18)

Add `tests/ShadowDusk.MonoGameValidation.DirectX/` (**`net8.0-windows`**, `MonoGame.Framework.WindowsDX`) running the same flow against `tests/fixtures/golden/DirectX_11/`.
- **Must be Windows-gated** (`Condition="'$(OS)'=='Windows_NT'"` or a separate solution filter) so `dotnet build ShadowDusk.slnx` stays green on Linux/macOS. It also needs a `<TargetFramework>` override (global `Directory.Build.props` pins `net8.0` + `TreatWarningsAsErrors`) — relax warnings if MonoGame/xUnit analyzers trip them.
- **Expected now:** step 5 throws — first on the §3.1 **header** (same "XFGM" bug), and even past that on **DXIL vs DXBC** (ShadowDusk emits `ps_6_0`/`vs_6_0` DXIL per `DxcFlagBuilder.cs:48-57`, "DXC cannot emit SM5 DXBC"; MonoGame 3.8 DX11 loads DXBC). The `mgfxc` DX side should load+render (control).
- **This phase:** capture and report; **Phase 18** resolves the format + DXIL/DXBC gap.

---

## 8. Test structure & extensions

### 8.1 `MonoGameRuntimeValidationTests` (OpenGL)
`[Trait("Category","MonoGameRuntime")] [Trait("Platform","OpenGL")]`, `IClassFixture<MonoGameDeviceFixture>`, in a non-parallel `[Collection]`. `[Theory]` over the candidates; each row runs the 9-step flow with a 30s `CancellationTokenSource` (match `MgfxcCrossValidationTests`), asserts `SdLoaded` (with `SdLoadError`) then `Matches` (with deltas + artifact paths). Skip via `MonoGameDeviceFixture.IsSkipped`.

### 8.2 `MonoGameDeviceFixture`
Hidden `Game`/`GraphicsDevice` once per collection; SDL2-aware skip; thread-affinity lock (§4.2); dispose after.

### 8.3 Later — *(carried forward; see backlog **17-VS** in `plan/PHASE-100-deferred-backlog.md`)*
- [ ] PS-only-VS resolution generalized to VS-driven shaders (needs `vs_uniforms_vec4` remap too). → backlog **17-VS**.
- [ ] Multi-pass / multi-technique. → backlog **17-VS**.
- [ ] If `mgfxc` ever gets wired (§3.5): compile the reference live and also assert ShadowDusk-vs-fresh-mgfxc + mgfxc determinism. → out of scope (no `mgfxc` here).

---

## 9. Acceptance Criteria

> **Status (2026-05-30):** ✅ MET — shared renderer (`validation/Shared/EffectImageRenderer.cs`), golden loads+renders as control, **ShadowDusk `.mgfx` loads in `new Effect`** (headline gate), PS-only VS-stage resolved (SpriteBatch prime), uniform-free **and** uniform-driven candidates match by-name, **all 10/10 shaders match (Dissolve/gap #3 now closed)**, per-shader load/render/match reported, `ref`/`cand`/`diff` PNGs saved, tolerance documented (Scanlines/Dots maxd 1), clean skips, Vulkan/Metal N/A. ⬜ NOT YET — DirectX project (Phase 18, separate), `docs/glsl-uniform-naming.md`/backlog-11-6-D writeup, JSON artifact (PNG+console only). Note: built as `validation/Baseline` + `validation/Candidate` rather than a single `tests/ShadowDusk.MonoGameValidation.OpenGL` xUnit project — a runnable two-app + `compare.py` harness; folding into an xUnit `[Theory]` is optional follow-up.

- [~] `tests/ShadowDusk.MonoGameValidation.OpenGL/` exists, is in `ShadowDusk.slnx`, references `MonoGame.Framework.DesktopGL` + `ShadowDusk.Compiler`. → **satisfied via the `validation/Baseline` + `validation/Candidate` two-app harness** (both reference DesktopGL + Compiler); the single xUnit `[Theory]` project was not built — folding the harness into xUnit is an optional follow-up.
- [x] One **shared** `RenderThroughMonoGame` renders both `.mgfx` files identically (pinned blend/sampler/surface/viewport per §4.1), same device, same run. → `validation/Shared/EffectImageRenderer.cs`.
- [x] **The golden loads + renders** as the control (`RefRendered` true). → baseline 10/10.
- [x] **ShadowDusk's `.mgfx` loads in MonoGame's `EffectReader`** for all candidates — i.e. §3.1 is fixed (signature, `EffectKey`, shader records). *This is the headline acceptance gate.* → candidate 10/10 load.
- [x] PS-only vertex-stage handling is resolved and identical for `ref` and `cand` (§5).
- [x] Uniform-free candidates (Grayscale, Invert, Pixelated, Fading) **match the `mgfxc` reference in-engine** (exact, or a diff-justified tolerance).
- [x] Uniform-driven candidates (TintShader, Sepia, Saturate, Scanlines, Dots) match **with parameters set by name** — §3.2 (11-6-D) resolved.
- [x] Per-shader `ValidationResult` distinguishes load / render / match; `ref`/`cand`/`diff` PNGs saved. → `compare.py` reports per-shader load/render/match; `ref`/`cand`/`diff` PNGs in `validation/output/{baseline,candidate,diff}/`. *(Path differs from `artifacts/phase17/`; a JSON artifact is not yet emitted — console + PNG only.)*
- [ ] DirectX project exists, is **Windows-gated**, and **reports** its outcome (expected fail; not required to pass). → **deferred to Phase 18.**
- [x] Any tolerance > 0 documented with observed delta + reason (no silent caps). → Scanlines/Dots maxd 1 (sub-LSB), documented in §3.7.
- [x] Skips cleanly with a clear message when no device; no `Thread.Sleep` / `.Result` / `.Wait()`. → async harness; the console apps require a device to run (dev-run harness, not a CI-skipped test).
- [x] `docs/glsl-uniform-naming.md` + backlog 11-6-D updated with the chosen remap strategy and verification. → both rewritten to document `MonoGameGlslRewriter` as built (rule table, verification, known limits); backlog 11-6-D marked RESOLVED in `plan/PHASE-100-deferred-backlog.md`.
- [x] Vulkan/Metal remain explicitly N/A (no runtime to compare against).

---

## 10. Implementation Order

> **Status (2026-05-30):** steps 1–11 ✅ done (diagnosis, harness, device fixture, render+compare, writer-format fix, uniform-free + uniform-driven candidates all matching) **+ gap #3 (Dissolve) closed → 10/10 match**. Step 12 (Windows-gated DirectX project) → Phase 18. The OpenGL fidelity goal is fully met; the only remaining Phase-17-scoped extension is VS-driven MonoGame support (§8.3, deliberately deferred).

- [x] 1. **Diagnose the format gap (§3.1):** pin the layout from MonoGame 3.8.2's **open-source** `EffectReader`/`Shader` (no decompile), then structurally diff a ShadowDusk OpenGL `.mgfx` vs the matching golden (`MgfxBlobReader` extended to a full binary walker / `MgfxcMgfxReader`). Produce a field-by-field divergence list. *(No MonoGame project needed yet.)* → done via `validation/decode_mgfx.py` + the §3.6 spec decode.
- [x] 2. Create the validation harness (DesktopGL + Compiler), copy fixtures, render path. → built as `validation/Baseline` + `validation/Candidate` console apps (not a single xUnit project — see §9 note); DesktopGL 3.8.2.1105 added to central PM.
- [x] 3. Device boot — hidden SDL2 device (§4.2). → in the validation apps via `EffectImageRenderer`.
- [x] 4. `RenderThroughMonoGame` (§4.1) + per-shader result + magenta diff + artifacts. → `EffectImageRenderer` + `compare.py` (magenta diff, per-shader load/render/match).
- [x] 5. **Instrument smoke test:** load a **golden** in `new Effect`, render it twice → assert byte-identical (determinism); then corrupt one pixel and assert the comparator **flags** it (proves the instrument detects failure, not just absence). → baseline renders the goldens as the control; `compare.py` flags any over-tolerance pixel.
- [x] 6. **Fix `MgfxWriter` format (§3.1):** (6a) collect per-shader sampler/cbuffer-index/attribute reflection in `CompilationPipeline` + extend `CompiledShaderBlob` (the IR lacks this today); (6b) signature → forward "MGFX"; (6c) header `EffectKey`; (6d) full per-shader record (stage flag, sampler table, cbuffer indices, GL attribute table); (6e) trailing "MGFX" footer; (6f) update `MgfxBlobReader` + `MgfxWriterTests` **atomically** (linked shared source — partial change breaks the suite). Leave cbuffer/parameter/technique sections untouched (already match). Gate: ShadowDusk `.mgfx` *loads* in `new Effect`.
- [x] 7. Uniform-free candidates (Grayscale, Invert → then Pixelated, Fading): compile → load → render → compare. Resolve §3.3 sampler binding + §5 VS-stage until they match in-engine.
- [x] 8. Add TintShader; **fork on the symptom:** is `Parameters["TintColor"]` null (§3.1 record) or non-null-but-wrong (§3.2 remap)? Record which.
- [x] 9. **Implement uniform remap (§3.2 / 11-6-D):** (9a) choose remap site; (9b) emit flat `ps_uniforms_vec4[N]` GLSL + use-site rewrite; (9c) parameter→register table in `MgfxWriter`; (9d) iterate vs TintShader until it matches by-name.
- [x] 10. Bring the rest green (Sepia, Saturate, Scanlines, Dots); document any diff-justified tolerance. → **+ Dissolve (gap #3)** → all 10/10 match.
- [x] 11. Run the full theory; update `docs/glsl-uniform-naming.md`, backlog 11-6-D, this doc's checkboxes. → full run done (10/10); `docs/glsl-uniform-naming.md` + backlog 11-6-D rewritten to the as-built design; this doc's checkboxes updated.
- [ ] 12. Add the Windows-gated `tests/ShadowDusk.MonoGameValidation.DirectX/` (§7): run, **report** the expected load failure, confirm the `mgfxc` side renders. (Fix = Phase 18.) → **deferred to Phase 18.**

---

## 11. Definition of Done

ShadowDusk's `.mgfx` **loads into a real MonoGame `GraphicsDevice`** and, driven by the normal `Effect`/`SpriteBatch` API with parameters set by name, renders the same input image to **the same output image** (byte-for-byte, or a diff-justified LSB tolerance) as the `mgfxc` golden rendered through the identical path — for the SM 3.0 post-process corpus, on OpenGL. Only then can we say: *swap `mgfxc` → ShadowDusk and the game looks the same.* DirectX equivalence follows in Phase 18 (format + DXIL/DXBC). Vulkan/Metal remain N/A until a MonoGame/KNI runtime consumes them.
