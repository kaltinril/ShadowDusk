# Phase 17 — MonoGame Runtime Cross-Validation (In-Engine Equivalence)

**Status:** Not started
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

ShadowDusk's `MgfxWriter` (`src/ShadowDusk.Core/MgfxWriter.cs`) emits a format MonoGame's `EffectReader` rejects. Verified against source:

- **Signature byte order is inverted.** `MgfxSignature = 0x4D474658u` written via `bw.Write(uint)` (LE) produces on-disk bytes **`58 46 47 4D` = "XFGM"** (`MgfxWriter.cs:10,56`). MonoGame expects **"MGFX"** (`4D 47 46 58`, as the goldens write). The repo *bakes this in*: `MgfxWriterTests` asserts the "XFGM" bytes and `MgfxcMgfxReader`'s docstring calls it the "inverted `XFGM` signature." → `new Effect` throws "does not appear to be a MonoGame MGFX file" before reading anything else.
- **Header is missing `EffectKey`.** `WriteHeader` writes signature(4) + version(1) + profile(1) = **6 bytes** (`MgfxWriter.cs:54-59`). MonoGame's header (per its decompiled `EffectReader`) is **10 bytes**: signature + version + profile + **`EffectKey`(4)**. Even with the signature fixed, MonoGame seeks past 10 bytes and desyncs.
- **Per-shader record is incomplete.** `WriteShaders` writes only `count` then per-shader `(int length, bytes)` (`MgfxWriter.cs:76-84`). MonoGame's `Shader` reader expects per shader: a **stage flag**, bytecode, a **sampler table** (count + type/textureSlot/samplerSlot/[state]/name/paramIndex), a **cbuffer-index list**, and (GL) a **vertex-attribute table** (name/usage/index). None are written → desync after the header.

> **Decision to make during implementation:** emitting MonoGame-compatible bytes (forward "MGFX", `EffectKey`, full shader records) **conflicts with ShadowDusk's own reader and `MgfxWriterTests`**, which currently expect "XFGM" and the truncated layout. Either (a) change the writer + reader + tests together to the real MonoGame format (correct for the drop-in goal — constraint #6), or (b) keep an internal format and add a MonoGame-format emitter. (a) is almost certainly right; the "XFGM"/truncated layout looks like an early-format artifact, not an intentional divergence.

**Tasks:**
- [ ] Structurally diff a ShadowDusk OpenGL `.mgfx` against the matching golden, field by field, using `MgfxBlobReader` + `MgfxcMgfxReader` and the decompiled MonoGame `EffectReader`/`Shader`/`ConstantBuffer` as the spec. Record every divergence.
- [ ] Fix the signature (emit forward "MGFX") and the header (`EffectKey` — MonoGame uses it as the effect-cache key; mgfxc writes an MD5-derived int).
- [ ] Emit the full per-shader record: stage flag + sampler table + cbuffer-index list + (GL) attribute table. **§3.3 and §3.4 depend on this** — there is no sampler binding until the sampler table exists.
- [ ] Update `MgfxBlobReader` / `MgfxWriterTests` to the corrected format.

### 3.2 Uniform remapping — backlog **11-6-D** (the second wall)

MonoGame's GL runtime binds free uniforms as a single `vec4[N]` array **named after the cbuffer** — `ConstantBuffer.PlatformApply` calls `GetUniformLocation(cbufferName)` and uploads with `glUniform4fv`. `mgfxc` names that cbuffer `ps_uniforms_vec4` / `vs_uniforms_vec4` and emits `uniform vec4 ps_uniforms_vec4[N];`. ShadowDusk emits SPIRV-Cross GLSL with a **`type_Globals` std140 UBO block** instead, so `GetUniformLocation("type_Globals")` → -1 and `PlatformApply` early-returns → uniforms read zero (e.g. TintShader renders black). See `docs/glsl-uniform-naming.md` (the existing — now stale — design sketch; it frames this as "Phase 7" and references `spvc` decoration queries; **use it as the starting point for this work**, not just something to update at the end).

This is the **central compiler change**, but only reachable after §3.1.
- [ ] **Choose the remap site** (SPIRV-Cross options vs. GLSL post-process vs. `MgfxWriter` parameter table — likely a combination). Resolve this before the emit work below.
- [ ] Convert the UBO to a **flat `uniform vec4 ps_uniforms_vec4[N]`** array (MonoGame uses `glUniform4fv` to an array uniform, **not** a UBO/`glUniformBlockBinding`) and rewrite use-sites.
- [ ] Name the cbuffer `ps_uniforms_vec4`/`vs_uniforms_vec4` in the `.mgfx` and record each parameter's register + offset so `Effect.Parameters[name].SetValue` lands in the right slot, in SM 3.0 constant-register layout.

### 3.3 Sampler / texture binding (part of §3.1's shader record)

`SpriteBatch.Draw` binds the drawn texture to slot 0; the shader's sampler must resolve there **via the per-shader sampler table** that §3.1 currently doesn't write. So this is not independent — until the sampler table is emitted, MonoGame has nothing to bind. ShadowDusk synthesizes `Texture2D X_SDTexture` + `SamplerState X` for bare samplers and rewrites `sampler2D` decls (Phase 16); the sampler table must carry the right name/slot for both forms.
- [ ] Verify the emitted sampler table binds the SpriteBatch texture to slot 0 for both the `Texture = <T>` form and the synthesized-texture form.

### 3.4 Sampler-state fidelity (caveat, lower priority)

The Phase 16 rewrite drops in-shader `sampler_state` filter/address modes (`SamplerInfo.StateEntries` parsed, not emitted). The harness pins one `SamplerState` (§5), so this won't fail the candidates, but note it.
- [ ] Decide whether in-shader sampler state must be emitted into the `.mgfx`, or whether deferring to `GraphicsDevice.SamplerStates` (MonoGame's normal behavior) is acceptable. Document the decision.

### 3.5 mgfxc is not available here

`mgfxc` is **not installed** (not in `.config/dotnet-tools.json`; `where mgfxc` is empty), requires `fxc.exe`/Windows, and its OpenGL profile only accepts SM 3.0 (see MEMORY: "mgfxc OpenGL profile rejects SM 4.0+"). Therefore **the reference is the checked-in goldens only**; any "regenerate via mgfxc" / fresh-determinism check is out of scope for this phase (revisit in §8.3 if mgfxc is ever wired).

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
- `samples/ShaderViewer/Game1.cs` — proven `new Effect(gd, bytes)` + by-name `Effect.Parameters[...]` + `SpriteBatch` load/draw. **Note two traps:** it uses `BlendState.AlphaBlend` (adopt this, §4.1), and it **primes the sprite VS** with a passthrough draw first (lines ~99-105) — see §3.3 caveat. It also sets `BloomThreshold` as a scalar `0.25f` though the shader declares `float4` — a bug to avoid (§6).
- `tests/fixtures/golden/OpenGL/*.mgfx` — the `mgfxc` references (step 1).
- `tests/ShadowDusk.ImageTests/` — `ImageComparer`, the candidate list, and the by-name parameter values (`MakeSceneFor`) to reuse (don't retype — reference them so they don't drift).

---

## 5. PS-only vertex-stage handling (verify before trusting any comparison)

All candidates are PS-only (no VS function); `CompilationPipeline` writes `VertexShaderIndex = -1`. "SpriteBatch supplies the VS" is true for SpriteBatch's *own* `SpriteEffect`, but when a **custom** effect with a VS-less pass is passed to `SpriteBatch.Begin(..., effect)`, the vertex stage source is subtle — and `ShaderViewer` only works because it **primes** the sprite VS with a prior passthrough draw. The headless single-call harness does not replicate that.
- [ ] Determine and pin exactly how the vertex stage is provided for a PS-only custom effect headless: (a) prime with a passthrough draw first like the viewer, (b) confirm MonoGame injects `SpriteEffect`'s VS for a `vsIndex=-1` pass *identically* for ShadowDusk and `mgfxc`, or (c) emit a real passthrough VS. If `ref` and `mgfxc` get the VS one way and `cand` another, the comparison is invalid even after §3.1.

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

### 8.3 Later
- [ ] PS-only-VS resolution generalized to VS-driven shaders (needs `vs_uniforms_vec4` remap too).
- [ ] Multi-pass / multi-technique.
- [ ] If `mgfxc` ever gets wired (§3.5): compile the reference live and also assert ShadowDusk-vs-fresh-mgfxc + mgfxc determinism.

---

## 9. Acceptance Criteria

- [ ] `tests/ShadowDusk.MonoGameValidation.OpenGL/` exists, is in `ShadowDusk.slnx`, references `MonoGame.Framework.DesktopGL` + `ShadowDusk.Compiler`.
- [ ] One **shared** `RenderThroughMonoGame` renders both `.mgfx` files identically (pinned blend/sampler/surface/viewport per §4.1), same device, same run.
- [ ] **The golden loads + renders** as the control (`RefRendered` true).
- [ ] **ShadowDusk's `.mgfx` loads in MonoGame's `EffectReader`** for all candidates — i.e. §3.1 is fixed (signature, `EffectKey`, shader records). *This is the headline acceptance gate.*
- [ ] PS-only vertex-stage handling is resolved and identical for `ref` and `cand` (§5).
- [ ] Uniform-free candidates (Grayscale, Invert, Pixelated, Fading) **match the `mgfxc` reference in-engine** (exact, or a diff-justified tolerance).
- [ ] Uniform-driven candidates (TintShader, Sepia, Saturate, Scanlines, Dots) match **with parameters set by name** — §3.2 (11-6-D) resolved.
- [ ] Per-shader `ValidationResult` distinguishes load / render / match; `ref`/`cand`/`diff` PNGs saved to `artifacts/phase17/<target>/` (gitignored).
- [ ] DirectX project exists, is **Windows-gated**, and **reports** its outcome (expected fail; not required to pass).
- [ ] Any tolerance > 0 documented with observed delta + reason (no silent caps).
- [ ] Skips cleanly with a clear message when no device; no `Thread.Sleep` / `.Result` / `.Wait()`.
- [ ] `docs/glsl-uniform-naming.md` + backlog 11-6-D updated with the chosen remap strategy and verification.
- [ ] Vulkan/Metal remain explicitly N/A (no runtime to compare against).

---

## 10. Implementation Order

- [ ] 1. **Diagnose the format gap (§3.1):** structurally diff a ShadowDusk OpenGL `.mgfx` vs the matching golden (`MgfxBlobReader`/`MgfxcMgfxReader` + decompiled `EffectReader`/`Shader`). Produce a field-by-field divergence list. *(No MonoGame project needed yet.)*
- [ ] 2. Create `tests/ShadowDusk.MonoGameValidation.OpenGL/` (DesktopGL + Compiler + xUnit), add to `ShadowDusk.slnx`, copy fixtures (template: `ShadowDusk.ImageTests.csproj` `<ItemGroup>`, minus reference-images), non-parallel `[Collection]`.
- [ ] 3. `MonoGameDeviceFixture` — hidden SDL2 device, skip-on-no-device, thread-affinity lock (§4.2).
- [ ] 4. `RenderThroughMonoGame` (§4.1) + `ValidationResult` (§4.3) + magenta diff + artifacts.
- [ ] 5. **Instrument smoke test:** load a **golden** in `new Effect`, render it twice → assert byte-identical (determinism); then corrupt one pixel and assert the comparator **flags** it (proves the instrument detects failure, not just absence).
- [ ] 6. **Fix `MgfxWriter` format (§3.1):** (6a) signature → forward "MGFX"; (6b) header `EffectKey`; (6c) full per-shader record (stage flag, sampler table, cbuffer indices, GL attribute table); (6d) update `MgfxBlobReader` + `MgfxWriterTests`. Gate: ShadowDusk `.mgfx` *loads* in `new Effect`.
- [ ] 7. Uniform-free candidates (Grayscale, Invert → then Pixelated, Fading): compile → load → render → compare. Resolve §3.3 sampler binding + §5 VS-stage until they match in-engine.
- [ ] 8. Add TintShader; **fork on the symptom:** is `Parameters["TintColor"]` null (§3.1 record) or non-null-but-wrong (§3.2 remap)? Record which.
- [ ] 9. **Implement uniform remap (§3.2 / 11-6-D):** (9a) choose remap site; (9b) emit flat `ps_uniforms_vec4[N]` GLSL + use-site rewrite; (9c) parameter→register table in `MgfxWriter`; (9d) iterate vs TintShader until it matches by-name.
- [ ] 10. Bring the rest green (Sepia, Saturate, Scanlines, Dots); document any diff-justified tolerance.
- [ ] 11. Run the full theory; update `docs/glsl-uniform-naming.md`, backlog 11-6-D, this doc's checkboxes.
- [ ] 12. Add the Windows-gated `tests/ShadowDusk.MonoGameValidation.DirectX/` (§7): run, **report** the expected load failure, confirm the `mgfxc` side renders. (Fix = Phase 18.)

---

## 11. Definition of Done

ShadowDusk's `.mgfx` **loads into a real MonoGame `GraphicsDevice`** and, driven by the normal `Effect`/`SpriteBatch` API with parameters set by name, renders the same input image to **the same output image** (byte-for-byte, or a diff-justified LSB tolerance) as the `mgfxc` golden rendered through the identical path — for the SM 3.0 post-process corpus, on OpenGL. Only then can we say: *swap `mgfxc` → ShadowDusk and the game looks the same.* DirectX equivalence follows in Phase 18 (format + DXIL/DXBC). Vulkan/Metal remain N/A until a MonoGame/KNI runtime consumes them.
