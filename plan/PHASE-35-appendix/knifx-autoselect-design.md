# Phase 35 appendix — Capability auto-detect + override: design & phased plan

**Status:** Architecture proposal (no code). Produced by the 2026-06-14 design investigation (5-agent
research workflow). All file:line references verified against `main` (2026-06-13). KNI runtime state
confirmed against KNI CHANGELOG v4.2.9001 (2025-11-02) and the MonoGame/mojoshader fork. Companion to
[knifx-vs-mgfx-v11-research.md](knifx-vs-mgfx-v11-research.md) and [knifx-area-b-kickoff-brief.md](knifx-area-b-kickoff-brief.md).

**Why this exists:** the owner asked "can we auto-detect the consumer's runtime capability and always give
them the newest best experience, with an optional override to different outputs?" This is the grounded
answer, plus the one decision-critical finding that reshapes the request.

> **⚠️ Update (2026-06-14): the KNIFX deferral below is SUPERSEDED.** This doc originally recommended *not*
> building KNIFX (defer until a proof that v10 renders worse on KNI). The owner explicitly **overrode** that:
> v10 + v11 + KNIFX are all committed additive outputs so consumers can *use* the newer formats' features,
> independent of whether v10 still loads. **KNIFX is now BUILT and render-proven:** `KnifxWriter` ships in
> `ShadowDusk.Core`, the compile path emits it via `CompilerOptions.Container = Knifx`, and the KNIFX corpus
> **loads + renders 10/10 in real KNI v4.2.9001** (`validation/KniDesktopGL knifx`, maxd 0 vs v10). The
> byte-exact format is in [knifx-format-spec.md](knifx-format-spec.md). What remains from this doc is the
> **format-selection seam itself** (the `CapabilityProfile` model + the detection helper in §§1-4), which is
> still the right design for "auto-detect → newest + override" and is **not yet implemented**. Read the
> sections below as the seam design; treat the "KNIFX is a smaller prize / defer it" conclusion as historical.

> **⚠️ Update (2026-06-14, part 2): seams 1-3 (the dialect axis) are now LANDED, byte-identical.**
> `ShaderDialect` (`NotApplicable | LegacyMojoShader | ModernGlsl`) and the closed `CapabilityProfile`
> set (`MonoGameGL_3_8_2`, `MonoGameDX_SM5`, `Fna_Fx2`) ship in `ShadowDusk.Core`, and
> `CompilerOptions.Profile` (nullable, default `null`) refines the GL dialect gate
> (`CompilationPipeline.cs` ~`:210`). `Profile == null` reproduces today's behavior exactly
> (`CrossHostByteIdentity` + the corpus stay green) and explicitly selecting `MonoGameGL_3_8_2` emits
> identical bytes (`CapabilityProfileByteIdentityTests`). **Seams 4 and 5 are now landed too:** seam
> 5 folds the container axis into the profile (`Container` + `MgfxVersion`, with the
> `MonoGameGL_3_8_5` MGFX-v11 and `KniGL_4_02` KNIFX proven profiles, byte-identical to the
> standalone options); seam 4 adds the `ShaderFeatures` axis + `ShaderFeatureSupport`, which
> **rejects (SD0201) any feature no shipping runtime consumes yet** (today: all of them, since KNI GL
> is still MojoShader), so a feature can never silently emit unloadable bytes. Still open: the
> detection assembly + CLI override (seams 6-7).

---

## TL;DR — the one finding that matters

**There is no runtime that can consume "modern GLSL" (the un-down-converted output) today.** So the
"newest best experience" cannot mean "emit modern GLSL now":

- **KNI's GL backend is still MojoShader, capped at SM2-3.** Confirmed: KNI CHANGELOG v4.2.9001 (2025-11-02)
  has no DXC/SPIRV-Cross switch and no vertex-texture-fetch / texture-arrays. KNI's GL pipeline still uses
  the [MonoGame/mojoshader fork](https://github.com/MonoGame/mojoshader). "Allow SM4.0 on GLSL" is
  feature-level (FL10_0) gating, not real SM4 features through MojoShader.
- **MonoGame 3.8.2 GL hard-codes `SupportsVertexTextures = false`.** The runtime ceiling, not the GPU, is
  the limiter.
- So emitting modern GLSL today = **silently broken output in every shipping runtime** — the exact failure
  CLAUDE.md forbids.
- **KNIFX is not the modern-GLSL path either.** It is a *new container around the same MojoShader-dialect
  body*; its wins are XNA-compat render-parity fixes (sampler-without-texture, `Matrix4x4` demotion, GLES
  precision), not richer shaders. And **our v10 already loads in KNI** via its MGFX-v10 migration path. So
  KNIFX is product-scope parity polish, gated behind two missing proofs (a reverse-engineered writer + a
  finding that v10 renders *worse* on KNI v4.02). Neither exists yet.

**Therefore the deliverable is the capability-profile architecture + the runtime-detection seam, landed
empty of new emitters, with the SD0210 rejections kept exactly as-is.** It makes ShadowDusk ready to flip
on modern-GLSL or KNIFX the instant a real runtime proves it can consume them, without ever shipping broken
bytes in the interim. The architecture is the product; the new emitters are deferred until their runtime
exists.

The single guardrail that makes "auto-detect → newest" safe: **auto-detect chooses which *proven* profile
to use; it never invents an unproven one.**

---

## 1. The capability-profile model

Today exactly one axis exists: `PlatformTarget` (`OpenGL | DirectX | Vulkan | Fna`). Capability is implicit
and hard-coded: the `monoGameGl` bool at `CompilationPipeline.cs:210` silently selects the legacy
MojoShader dialect, and `MgfxVersion = 10` (`CompilerOptions.cs:54`) silently selects the container. The
design makes these explicit, composable, and named.

### Three orthogonal axes

| Axis | Values (today → future) | Selects | Current seam |
|---|---|---|---|
| **Container** | `MgfxV10` (default) → `MgfxV11`?, `Knifx11` | on-disk header + body framing | `MgfxWriter.cs:22` + `MgfxWriterOptions.MgfxVersion` |
| **Dialect** | `LegacyMojoShader` (default GL) → `ModernGlsl` | whether `MonoGameGlslRewriter.Rewrite` runs (down-convert) vs SPIRV-Cross pass-through | `applyMonoGameGlsl` / `CompilationPipeline.cs:1059-1062` |
| **AllowedFeatures** | `{}` (default) → `VertexTextureFetch`, `TextureArrays`, `FullPrecisionGLES` | which SD0210 throw sites are lifted vs enforced | the throw sites in `MonoGameGlslRewriter.cs` (incl. VTF reject `:362-371`) |

### The composition rule: a profile is a named, *validated* point, not a free cross-product

A `CapabilityProfile` is a **closed enum of validated combinations**, each rung-4 proven against a specific
runtime. This is the load-bearing decision — it keeps "newest best experience" from becoming "untested
cross-product of broken bytes."

```
CapabilityProfile  (closed set; each member = one proven runtime contract)
  ├─ MonoGameGL_3_8_2  = { Container=MgfxV10, Dialect=LegacyMojoShader, Features={} }   ← DEFAULT, proven (Phase 17/43)
  ├─ MonoGameDX_SM5    = { Container=MgfxV10, Dialect=n/a(DXBC),        Features={} }   ← proven (Phase 18)
  ├─ Fna_Fx2           = { Container=Fx2(.fxb), ...                                 }   ← proven (Phase 39/40)
  │
  ├─ KniGL_4_02        = { Container=Knifx11, Dialect=LegacyMojoShader, Features={FullPrecisionGLES} }   ← FUTURE, unproven
  └─ ModernGL_VTF      = { Container=?,       Dialect=ModernGlsl,       Features={VTF,TextureArrays} }   ← FUTURE, no runtime yet
```

Invariants:
1. **No anonymous combinations.** `AllowedFeatures` is not a consumer-settable bag; it is a property of the
   chosen profile. A consumer cannot ask for "v10 + VTF" — that tuple is not a profile because no runtime
   honors it. This is how the model refuses to miscompile.
2. **`Dialect=ModernGlsl` and `AllowedFeatures≠{}` are coupled.** Lifting SD0210 is only meaningful in
   modern-GLSL mode (legacy MojoShader cannot express VTF/arrays). The profile binds them so they can never
   be lifted independently.
3. **Every profile = one (container, dialect, feature-set) + one validation gate.** Adding a profile means
   adding a row and its rung-4 corpus. No profile ships green-tested-only.

Same discipline already used for `DxbcBackend` (`CompilerOptions.cs:68`): a closed enum where each value is
a validated, host-independent contract, not an open knob.

---

## 2. Detection — what, where, and the determinism resolution

The two delivery shapes have different determinism obligations; the design treats them asymmetrically.

### 2a. In-app runtime library (live `GraphicsDevice`) — auto-upgrade lives here, determinism-safe

Auto-detected, in priority order:
1. **Fork + version** by reflecting over the *loaded* XNA assembly (the proven pattern at
   `validation/ForwardCompat/Program.cs:42-44`): the assembly simple name
   (`MonoGame.Framework` | `nkast.Xna.Framework` | `FNA`) is the only reliable fork discriminator (all three
   share the `Microsoft.Xna.Framework` namespace), plus its `AssemblyInformationalVersion`.
2. **GL capability ceiling**, only when a real cap query is reachable. KNI exposes `Adapter.Backend`
   (`OpenGL|GLES|WebGL|DirectX11|...`). MonoGame's `GraphicsCapabilities.SupportsVertexTextures` /
   `SupportsTextureArrays` are internal + hard-coded false on DesktopGL — so the runtime *ceiling*, not the
   GPU, is the limiter.
3. **Reach vs HiDef / GLES vs desktop** for the GLSL-version block (mirrors the Phase 33 precedent).

Detection lives in a **separate optional advisory assembly** (`ShadowDusk.Runtime.Detection`) that
references the XNA assemblies and returns a recommended `CapabilityProfile`. It is **never** inside
`ShadowDusk.Compiler` — the product library stays free of any MonoGame/KNI/FNA reference (it is today:
`IShaderCompiler.CompileAsync` takes only `string + CompilerOptions + CancellationToken`). The consumer's
game calls the helper, gets a profile, passes it in.

**Why auto-upgrade is determinism-safe here:** in-app compilation persists nothing. The `.mgfx` bytes feed
straight into `Effect` and are discarded, regenerated every run on the very machine that renders them. Core
Constraint 3 ("same source + target = byte-identical") governs *reproducible build artifacts a consumer
ships*; an in-memory blob that never leaves the process has no cross-host obligation to violate. The
detected profile is a property of this machine's runtime, and that machine is also the renderer.

### 2b. Build-time (CLI / MGCB — no live runtime) — deterministic, never auto-guesses

No `GraphicsDevice`, and the output is a file the consumer ships and another machine renders. Probing the
*build* host would bake host-specific bytes into a shipped artifact — non-reproducible, a direct breach of
`CrossHostByteIdentityTests`.

- **Default = `MonoGameGL_3_8_2` (MGFX v10)**, unchanged, the universal artifact.
- Target is an **explicit-but-defaulted** input, never a host probe: the MGCB platform string
  (`DesktopGL`/`Windows`/`Web`) or a new `--target-runtime` enum. The consumer's project already declares
  its platform to MGCB, so "auto-select KNIFX for a KNI Web target" reads a declared input, not a guess —
  still seamless (a platform the game already targets), not a ShadowDusk-specific flag.

### 2c. Fail-loud fallback (both shapes)

- Ambiguous/unknown fork or version → emit safe default `MonoGameGL_3_8_2` (v10) and surface it in
  diagnostics. v10 loads everywhere, so conservative is never *broken*, only *not-upgraded*.
- Source needs a feature the detected target cannot honor → hard SD0210, never a silent downgrade. Detection
  *narrows when SD0210 fires*; it never *suppresses* it for an unproven target.

---

## 3. The override API — non-required escape hatch

**Library:** one nullable field, `CompilerOptions.Profile` (`CapabilityProfile?`, default `null`). `null` →
build-time uses `MonoGameGL_3_8_2`; in-app uses whatever the advisory helper supplied (itself v10 on
ambiguity). The compiler stays a **pure function of `(source, options)`** — detection happens *outside* it
and is reified into `options.Profile` before the call. An explicit non-null `Profile` **always wins** over
detection.

**CLI:** keep `--mgfx-version` (10 default; 11 = documented raw-byte stub). Add `--target-runtime
<monogame-gl|monogame-dx|kni-gl|fna|...>` mapping to a profile, **defaulted, never required**. The
`--mgfx-version 11` stub stays exactly as documented — a raw header byte, dead-on-arrival in KNI, never
advertised as v11/KNIFX support.

**WASM/browser:** mirror the CLI (deterministic default + explicit override, no auto-detect). Its bar is
`CrossHostByteIdentityTests`; an ambient probe would red that gate. The browser is a sample of reach, never
the product.

---

## 4. Minimal code seams, in dependency order (1-5 are byte-identical refactors)

1. **`CapabilityProfile` (closed enum/record) in `ShadowDusk.Core`** — define only proven members
   (`MonoGameGL_3_8_2`, `MonoGameDX_SM5`, `Fna_Fx2`) + the axis fields. No behavior change.
2. **`CompilerOptions.Profile` (nullable, default null)** — `null` resolves to today's behavior.
3. **Thread the profile to the dialect gate** — replace `bool monoGameGl = options.Target == OpenGL`
   (`CompilationPipeline.cs:210`) with a profile-derived `Dialect`. `Profile==null` ⇒ `LegacyMojoShader` ⇒
   identical `applyMonoGameGlsl=true` ⇒ byte-identical output.
4. **`MonoGameGlslRewriter.Rewrite(glsl, stage, AllowedFeatures)`** (currently `:223`) — the throw sites
   consult `features` before throwing. Default `{}` ⇒ every SD0210 still fires. Inert until a profile
   populates `features`.
5. **Container selector** — map `Profile.Container` → writer. Today only `MgfxWriter` (`MgfxV10`); a future
   `Knifx11` needs a new `KnifxWriter` here (not built now).
6. **Optional advisory detection assembly** (`ShadowDusk.Runtime.Detection`) — references XNA assemblies,
   returns a recommended profile via the ForwardCompat reflection pattern. Outside `ShadowDusk.Compiler`.
7. **Wire the CLI escape hatch** (`--target-runtime`) + MGCB-platform→profile mapping into `PipelineRunner`.

Seams 1-5 ship behavior-identical and are independently testable (assert byte-identity of the existing
corpus before/after). Nothing in 1-7 changes a single output byte for a current consumer.

---

## 5. Phased plan (reproduce-first)

### Phase 0 — Reproduce & establish the KNI floor (gates everything; cheap, no new code)
Decides whether KNIFX is ever worth building.
- **0a.** Render-validate ShadowDusk's current **v10 output in real KNI v4.02+** (a different fork than the
  MonoGame floor; never rung-4'd against KNIFX-era KNI). Reuse/refresh the Phase 24 real-browser KNI harness.
- **0b.** Capture a **KNIFXC-produced golden** for 2-3 corpus shaders, to reverse-engineer the container
  later if needed.
- **0c.** Document (now answered): KNI GL is still MojoShader (SM2-3, no VTF/arrays) as of v4.2.9001. No
  runtime consumes modern un-down-converted GLSL today. Re-verify each KNI release.

**Exit decision:** v10 renders pixel-equivalent on KNI v4.02 → KNIFX is parity-polish, defer it (expected).
v10 renders visibly worse → KNIFX enters product scope, schedule Phase 4.

### Phase 1 — Land the capability architecture (inert, byte-identical)
Seams 1-5. Define `CapabilityProfile` with only proven members. **Acceptance:** entire existing corpus
compiles byte-identical (existing goldens + `CrossHostByteIdentityTests`). This is the architecture the
owner wants, landed with zero output change.

### Phase 2 — Modern-GLSL capability mode (built, but default for no target)
Implement `Dialect=ModernGlsl` (formalize the existing un-rewritten pass-through) + feature-gated lifting of
SD0210 behind `AllowedFeatures`. **Acceptance:** a `ModernGL_VTF` profile exists and is exercisable in
tests, but maps to no shipping runtime and is never auto-selected. SD0210 stays default for every real
target. This satisfies squarebananas' "option to suppress MojoShader limitations" as a capability that
*exists*, gated on a future per-runtime proof.

### Phase 3 — Runtime detection (in-app advisory)
Seam 6 + 7. **Acceptance:** in real MonoGame and real KNI, the helper returns the correct *proven* profile
(v10 for both today). Auto-upgrade is conservative: it only ever returns a profile rung-4 proven for the
detected runtime. Determinism-safe (in-app output non-persisted).

### Phase 4 — KNIFX container (ONLY if Phase 0 proved v10 renders worse on KNI)
Reverse-engineer `KNIFXHeader` + the KNIFX body (per-shader `ShaderVersion`, separate GL/GLES blocks, GLES
full precision) from the Phase-0b golden. Build a faithful `KnifxWriter`. Add `KniGL_4_02`, rung-4 validate,
regenerate the manifest as a deliberate version event. **Note:** even KNIFX keeps `Dialect=LegacyMojoShader`
(KNI's GL still consumes MojoShader-dialect GLSL). KNIFX is a container + parity upgrade, NOT the modern
path.

### Phase 5 — Modern-GLSL goes live (ONLY when a runtime proves it)
When any shipping runtime (a future KNI that swaps MojoShader for DXC/SPIRV-Cross, à la cpt-max; or a new
MonoGame GL path) proves it consumes modern GLSL + binds vertex textures, gate `ModernGL_*` on detecting
*that specific runtime*, rung-4 validate, let detection auto-select it. The lift is gated on the detected
runtime, never blanket.

---

## 6. Risks + honest recommendation

| Risk | Severity | Mitigation |
|---|---|---|
| Shipping modern GLSL / lifted SD0210 before a runtime consumes it → silent miscompile everywhere | Critical | The model refuses it: `AllowedFeatures` is a property of a proven profile, never a consumer knob; SD0210 stays default; Phase 2's modern profile maps to no shipping target. |
| Build-time auto-detect bakes host-specific bytes into shipped artifacts | High | Asymmetric policy: build-time never probes; explicit-but-defaulted target only. `CrossHostByteIdentityTests` stays the guard. |
| Silent output drift when a consumer bumps their KNI/MonoGame | High | In-app auto-upgrade only returns a rung-4-proven profile (today: v10 everywhere). New auto-selected output ships as a deliberate version event, never a silent track of the runtime version. |
| KNIFX reverse-engineering is a large unmapped job | Medium | Deferred behind Phase 0's "is it even needed" gate. |
| Recompiling every launch in-app (no persisted artifact) is a perf cost | Medium | Out of scope here, flagged: in-app auto-upgrade likely needs a per-machine compile cache (keyed on source + resolved profile). |
| `--mgfx-version 11` stub mistaken for real KNIFX support | Low | Keep it documented exactly as the appendix says; a real `Knifx11` profile supersedes it. |

### The honest recommendation

**Build the architecture (Phases 0-1) and the modern-GLSL *capability* (Phase 2) when ready. Do NOT build
KNIFX now, and do NOT auto-ship modern GLSL now.**

- The architecture is the actual deliverable: "auto-detect → newest with override" is a seam + profile
  model + detection helper, all of which land byte-identical and make ShadowDusk ready to flip on any new
  capability the instant its runtime exists.
- Modern GLSL is the capability the owner is really reaching for — build the mechanism (Phase 2) so it is
  tested and ready, but keep it inert (no runtime consumes it; KNI is still MojoShader-SM3). Phase 5 flips
  it on per-detected-runtime the day a runtime ships DXC/SPIRV-Cross GL.
- KNIFX is a smaller prize than it looks: a container + XNA-compat parity over a still-MojoShader body, and
  our v10 already loads in KNI. Build it only if Phase 0 proves v10 renders worse on KNI v4.02 — a finding
  that does not yet exist.
</content>
