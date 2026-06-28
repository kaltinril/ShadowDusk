# Phase 46 — ShaderToy → FX Conversion Tool (experiment / SPIKE)

> **📦 Archived to DONE (2026-06-28).** The experiment succeeded (converter built,
> compile-proven, and render/fidelity-proven) and was **promoted to
> [Phase 47](../PHASE-47-shadertoy-frontend-promotion.md)**, which now owns the converter as an
> in-solution product library and carries all forward ShaderToy work. This doc is retained as
> the technique/coverage reference. Its own deferred items (host-uniform passthrough, broader
> WebGL-reference validation) live with the active Phase 47.

**Track:** Reach experiment (adoption/demo). **Not** the product pipeline.

> ## ⏯️ SESSION HANDOFF — current state (2026-06-20)
>
> Everything below this block is the running history; THIS block is the live status to resume from.
>
> - **Branch:** `experiment/shadertoy-to-fx` — pushed. **Draft PR #112 OPEN**
>   (`https://github.com/kaltinril/ShadowDusk/pull/112`). `main` untouched, working tree clean.
> - **Isolation verified:** the ONLY changes outside `tools/shadertoy2fx/` are `.gitignore`, this doc,
>   and `plan/plan.md`. No `src/` or `tests/` product file changed; the tool is NOT in `ShadowDusk.slnx`.
> - **Tests: 380/380 green, 0 warnings** (`dotnet test tools/shadertoy2fx/shadertoy2fx.slnx`).
> - **Compile sweep (all goldens):** OpenGL 72/72, DirectX_11 72/72, **FNA 70/72** (the 2 FNA misses =
>   `bitwise_ops`/`uint_type`, the inherent D3D9/SM3 no-integer-bitwise ceiling, compile fine on GL/DX).
> - **★ PIXEL-FIDELITY GATE (new, the strongest proof): `render-proof --fidelity`.** Renders the
>   ORIGINAL ShaderToy GLSL directly in a raw Silk.NET GL context (ground truth) vs OUR converted
>   `.fx` through MonoGame, and diffs per pixel. **46/46 deterministic shaders MATCH the original GLSL
>   at mean 0.00/255** (pixel-identical), incl. every matrix/precision trap + the 4 complex shaders.
>   Committed montage `render-proof/output/fidelity.png` (reference | ours | diff). This gate **caught
>   a real bug** the compile/render gates missed: `vec *= mat` rendered the vertical mirror (wrong
>   `mul` side in the compound-assign lowering) — **now fixed**, `mul(M,v)` for `v *= M`.
> - **Render GALLERY (`render-proof --gallery`): 72/72 authored shaders render non-trivially** in real
>   MonoGame GL (montage `render-proof/output/gallery.png`). 4 COMPLEX original shaders
>   (`raymarch_sphere`, `fbm_clouds`, `kaleidoscope`, `domain_warp`) all render correctly.
> - **Sample loads ANY file now:** `dotnet run --project tools/shadertoy2fx/sample -- <path.glsl>`
>   converts+compiles+renders an arbitrary `.glsl/.frag/.fs/.txt`, with **live hot-reload** (edit on
>   disk → re-renders) + on-screen errors; `--smoke <path>` validates one file headlessly. Bundled
>   catalog still works.
> - **Real-world conversion: 61.2% (98/160)** over the gitignored 160-shader scratch corpus (17.5%
>   v1 baseline). Trajectory: 17.5 → 23.4 → 26.0 → 34.0 → 34.6 → 44.4 → 51.2 → 55.0 → **61.2**.
> - **Inputs accepted:** ShaderToy `mainImage`, plain-GLSL `void main()`, `mainImage`+wrapper-`main`,
>   Godot 4-arg `mainImage`, vec3/vec4-returning `mainImage`, and multi-tab export JSON (`--multipass`).
> - **Proven against the owner's 4 ShaderToy shaders** (Seascape/Ms2SD1, Rainforest/4ttSWf, XsK3RR,
>   tsScRK): all convert + compile to OpenGL (fetched transiently, not committed).
> - **Reference docs:** `COVERAGE.md`, `MAPPING.md`, `GLSL-HLSL-NOTES.md` (confirmed matrix-order / mod /
>   Y-flip traps match Microsoft's docs).
>
> **OWNER-CLARIFIED DIRECTION (2026-06-20):** this is a proof of concept. The eventual plan is to
> **lift the converter CORE into the product library** and have the **existing `ShadowDuskCLI`/`mgfxc`
> accept `.glsl` ShaderToy input** (route through the library). The PoC is already shaped for that
> lift: clean `ShadowDusk.ShaderToy` library, one public `Convert(glsl)` entry, zero product coupling.
> Keep it that way; promotion is a wire-in, not a rewrite.
>
> **Open next-steps (none mid-flight):**
> 1. ~~**Promote core → library + CLI `.glsl` input**~~ **DONE (Phase 47, 2026-06-20):** the converter
>    library + 380-test suite are now `src/ShadowDusk.ShaderToy/` + `tests/ShadowDusk.ShaderToy.Tests/`
>    in `ShadowDusk.slnx`, and `ShadowDuskCLI` accepts ShaderToy/GLSL input. See
>    `plan/PHASE-47-shadertoy-frontend-promotion.md` + the CLI appendix.
> 2. **Host-uniform passthrough, WARNED version** (owner asked "what's the harm"): expose an
>    undeclared identifier as an effect parameter ONLY where the type is inferable, with a loud Warning
>    ("assumed `float`; you must supply X"). Raises the % but with lower-quality "best-effort" output;
>    deliberately not done silently (would break the never-silently-wrong rule). Owner inclined to try.
> 3. NuGet packaging — deferred by owner.
>
> The realistic single-pass ceiling is ~61% on an unfiltered sample; the remainder is genuinely out of
> scope (multipass-only/feedback, host-specific uniforms, `#include` w/o source, cubemap/3D/mip-bias,
> texelFetch, VR) — each a clean named reject. The G1–G9 backlog + as-built sections below are accurate
> history; the older "Results so far" / "Why ~26%" blocks are superseded snapshots kept for narrative.

> ## Results so far (2026-06-19) — bet proven; compiles, loads, AND renders
>
> The tool is **built, green, and render-proven**. The central bet holds: ShaderToy/GLSL image
> shader → `.fx` → the **unchanged, real ShadowDusk pipeline** → every XNA-family backend, and a
> converted shader **renders correctly in a real MonoGame GL `Effect`**.
>
> - **`tools/shadertoy2fx/`** — a standalone managed converter (`ShadowDusk.ShaderToy` library +
>   `shadertoy2fx` CLI + a `ShadowDusk.ShaderToy.Runtime` helper), **no native dependency**, **not**
>   wired into the pipeline, **not** in `ShadowDusk.slnx`. Preprocessor → lexer → parser → AST →
>   type-inference → HLSL emitter → harness generator, behind `ShaderToyConverter.Convert(glsl)`.
>   Builds 0-warning under `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`.
> - **Tests: 161/161 green** — unit trap-tests (matrix-order incl. `*=`, `mod`-sign, vector `==`
>   scalarization, intrinsic renames, splat/truncation), preprocessor (28), custom-uniform (10),
>   uniform-detection, options, **golden regression** over 34 goldens, and **loud-reject** coverage.
> - **Language supported (v1+):** the constrained ShaderToy subset PLUS a full C preprocessor
>   (`#if/#ifdef/#elif/#else/#endif` with a const-expr evaluator + `defined()`, object- and
>   function-like macros, `#undef`) AND custom top-level `uniform` declarations (scalar/vector/matrix
>   + `sampler2D`) exposed as consumer-driven effect parameters. Unsupported shapes (structs, arrays,
>   multipass/Buffer, `##`/`#include`, sampler3D/cube, …) stay **loud located rejects**, never silent.
> - **RENDER-PROVEN**: a converted shader loads in a real MonoGame DesktopGL `Effect` and renders
>   with analytic pixel assertions passing — gradient right-side-up vs ShaderToy's bottom-left
>   `fragCoord` convention (Y-flip correct), and a **host-set custom uniform renders exactly
>   through**. The `render-proof/` driver is the gate; PNGs are committed eyeball evidence.
> - **COMPILE SWEEP 102/102**: all 34 emitted `.fx` (incl. the 234-line real CC0 neon shader)
>   compile through the real pipeline → **OpenGL 34/34, DirectX_11 34/34, FNA 34/34**.
> - **RUNNABLE SAMPLE (capstone, 2026-06-19)**: `tools/shadertoy2fx/sample/` is a real MonoGame
>   DesktopGL game that, **at runtime with no build step and no `mgfxc`**, runs the full path for
>   each bundled shader: `ShaderToyConverter.Convert` → `EffectCompiler.Compile` (OpenGL, **in
>   memory**) → `new Effect(GraphicsDevice, mgfxBytes)` → animated/interactive fullscreen render via
>   `ShaderToyEffect`. It bundles 4 animated/interactive shaders (`time_animation`,
>   `mouse_interaction`, `atan_polar`, CC0 `neon`); SPACE/arrows cycle + recompile at runtime, the
>   mouse drives `iMouse`, ESC quits, the window title shows the shader + its uniforms. A `--smoke`
>   mode renders one offscreen frame per shader, writes a PNG each to `sample/output/`, asserts
>   non-trivial (not all-black), and exits 0 — **green 4/4 on this machine**; committed PNGs are
>   eyeball evidence. Builds 0-warning under the inherited warnings-as-errors. Out-of-band: **no
>   NuGet**, **not** in `ShadowDusk.slnx`. This proves ShadowDusk's in-memory runtime compile, not
>   just an offline `.fx`.
> - **Coverage trajectory (160 real third-party shaders, gitignored scratch, none committed),
>   conversion → end-to-end-compile:** v1 baseline 17.5% → +preprocessor 23.4% → +custom uniforms
>   **26.0% convert / 22.1% end-to-end** (compile-of-converted ~85%). The `COVERAGE.md` blast-radius
>   ranking drives what to add next; the remaining ceiling for single-pass image shaders is structs,
>   arrays, and a long tail of exotic GLSL — many real shaders are **multipass** (out of v1 scope).
>
> **Still open (honest):** (a) **multipass** (Buffer A–D / feedback) is the big unbuilt feature and
> the main reason real-world coverage caps in the ~25-50% range for arbitrary shaders; (b) render
> validation is analytic-pixel + eyeball, not yet a diff against ShaderToy's own WebGL reference for
> a broad corpus; (c) productization — a **runnable sample app now exists** (`sample/`, runtime
> in-memory compile + interactive render, see the Results bullet above), but NuGet packaging of the
> runtime helper and end-user docs are still not started (NuGet intentionally deferred). The
> matrix/`mod`/Y-flip traps are render-confirmed for the cases tested.

**Status:** Experiment IN PROGRESS (started + compile-proven 2026-06-19). A **standalone, separate tool** that converts a
**ShaderToy GLSL** shader into an **HLSL `.fx`** source file. It is **deliberately NOT part of the
compiler pipeline**: its only output is `.fx` *text*. Once that `.fx` exists, the **existing,
already-proven ShadowDusk pipeline** compiles it to whatever the consumer's game targets
(MonoGame GL/DX, KNI, FNA) with **zero new pipeline code and no new native dependency.**

> **The whole bet in one sentence:** if we can faithfully turn a ShaderToy shader into a valid
> `.fx`, then "ShaderToy → MonoGame/KNI/FNA" comes for free, because `.fx` is the **one true
> input** the pipeline already compiles to every backend. This phase tests that bet **cheaply**,
> as a source-to-source transpiler, without touching the faithful pipeline and without vendoring
> `glslang`.

---

## Why coverage is ~26% and the gap-closing backlog (2026-06-19)

A categorized run of the 160-shader scratch corpus shows the ~26% conversion rate is **almost
entirely fixable subset gaps, not multipass**. Only **~6 of 160** shaders are genuinely out of v1
scope (5 non-`mainImage`/multipass, 1 VR). The other ~114 failures bucket as below — so the
*addressable* ceiling on this corpus is ~95%. This is the ordered "get it all working" backlog:

| # | Gap | Shaders | Plan | Status |
|---|---|---:|---|---|
| G1 | Top-level **mutable globals** (`float g;` / `vec2 g = …;` at file scope) | 33 | Emit as HLSL `static` global (per-invocation semantics) | **DONE (2026-06-19)** |
| G2 | Top-level **qualifier decls** (`out vec4`/`layout`) + the `void main()`+`out fragColor`/`gl_FragColor`+`gl_FragCoord` plain-GLSL-fragment **entry mode** | 17 | Accept a second entry convention; map `out`/`gl_FragColor` color to the PS return | **DONE (2026-06-19)** |
| G3 | **Undeclared identifier** (L1 reject) | 16 | Add more known built-ins/aliases (`iChannelResolution`, glslViewer `u_*`); the genuinely-undeclared stay loud rejects | **DONE (2026-06-19)** |
| G4 | Custom **uniform with initializer** (`uniform float x = 1.;`) | 6 | Accept; use the initializer as the parameter default | **DONE (2026-06-19)** |
| G5 | **`#version` / `#extension` / `#pragma` / glslViewer `#iChannel`** directives | 6 | Strip/ignore the harmless ones; keep `#include` a loud reject (no resolver) | **DONE (2026-06-19)** |
| G6 | **structs** | 5 | Parse + emit HLSL `struct`; member type inference | **DONE (2026-06-19)** |
| G7 | **unknown types / arrays / unknown intrinsics / parse tail** | ~25 | Case-by-case: const + local arrays, more intrinsics, parser hardening | **DONE (2026-06-19)** |
| G8 | **LOW-RISK / HIGH-YIELD batch**: sized arrays x3 contexts + brace/constructor init, bitwise ops (+ compound assign), `gl_FragCoord` body built-in, `uint`/`uvec`→`int`, redundant `sampler2D iChannelN`, redundant built-in w/ initializer + multi-declarator uniforms, OF header token | ~11 | Accept each faithfully; non-const array size / ISF builtins / switch / includes stay loud rejects | **DONE (2026-06-19)** |
| G9 | **SECOND fixable batch**: ignore stage-I/O `in`/`varying`/`attribute` (+ `layout(location) in`) with coordinate-varying -> screen-UV alias; OpenFL `#pragma header` + `openfl_*` globals; Godot/GdShaders 4-arg `mainImage`; libretro VERTEX/FRAGMENT stage split; `switch` -> if/else lowering | ~6 | Alias only conventional coord names (else undeclared reject); seed FRAGMENT narrowly; switch fall-through stays a loud reject | **DONE (2026-06-20)** |
| — | **Multipass (Buffer A–D), VR, sound** | ~6 | OUT OF v1 SCOPE — multipass is a separate runtime-orchestration project | not planned (v1) |

The headline coverage number will be re-measured after each gap closes. Multipass remains the one
genuinely-large unbuilt feature and the reason arbitrary-corpus coverage can't approach 100% in v1.

### G1/G3/G4/G5 as-built (2026-06-19)

The four highest-value gaps closed together. Over the 160-shader gitignored scratch corpus (153 with a
`void mainImage`; none committed), this lifted **conversion 26.0% → 34.0%**, **end-to-end 22.1% → 29.4%**
(compile-of-converted held at ~86.5%). Suite 161 → 185 green, 0 warnings; golden compile-sweep
**38/38 on OpenGL / DirectX_11 / FNA**; render-proof still 3/3 (exit 0).

- **G1 — top-level mutable globals.** A non-`const` top-level global of a supported type (`float g;`,
  `vec2 p = vec2(0.0);`, comma multi-declarators) is accepted and emitted as an HLSL
  `static <type> <name> [= <init>];` (GLSL fragment-global = per-invocation mutable, which `static`
  matches). It is internal state, NOT a host parameter (excluded from `UsedUniforms`). An
  unsupported-type global (`double g;`) stays a loud reject. *(Parser `ParseMutableGlobalRest`,
  `GlobalVarDecl` AST, `HlslEmitter.EmitGlobalVar`, `TypeInference` registration.)*
- **G3 — alias / built-in coverage.** The full ShaderToy built-in set was already modeled. Added the
  exact-type host aliases `time`/`fGlobalTime` → `iTime` and `u_frame`/`iGlobalFrame` → `iFrame`
  (alongside the existing `u_time`/`iGlobalTime`). A **type-mismatched** alias (e.g. glslViewer
  `vec2 u_resolution` vs `vec3 iResolution`) is NOT folded — it is exposed verbatim as a custom uniform.
  A genuinely-undeclared identifier still rejects loudly (L1 intact). *(`UniformAliases`.)*
- **G4 — custom uniform with initializer.** `uniform <type> <name> = <const-expr>;` (valid GLSL 1.20+)
  is accepted; the initializer is translated and emitted as the HLSL parameter's default, and the
  uniform is still reported in `UsedUniforms`. A sampler-with-initializer stays a loud reject. *(Parser
  + `CustomUniformDecl.Initializer` + harness `EmitCustomUniforms` default path.)*
- **G5 — harmless directives.** `#version`/`#extension`/`#pragma`/`#line` and the glslViewer/Bonzomatic
  channel-binding & input metadata directives (`#iChannel0 "..."`, `#iKeyboard`, `#iMouse`, … —
  recognized by the leading-`i` ShaderToy-input convention) are silently dropped. `#include` stays a
  loud reject (no file resolver); `##`/`#`/variadic macro rejects unchanged. *(`Preprocessor`.)*

### G6/G7 as-built (2026-06-19)

Structs (G6) and arrays / added-intrinsics / parser-tail (G7) closed together. Over the same 160-shader
gitignored scratch corpus (153 with `void mainImage`; none committed), this lifted **conversion
34.0 % → 34.6 %** (53 converted), **compile-of-converted 86.5 % → 88.7 %** (47/53), **end-to-end
29.4 % → 30.7 %** (47/153). Unit suite 214 green (0 warn); golden compile-sweep **44/44 on
OpenGL / DirectX_11 / FNA** (the struct + array goldens compile on FNA fx_2_0 too — **no SM3-limit case
in this corpus**); render-proof 3/3 (exit 0). The lift is modest because the dominant remaining blockers
are multipass / `texelFetch` / complex custom constructs, not structs/arrays; correctness (never
silent-wrong) was the priority, not raw coverage.

- **G6 — user structs.** A top-level `struct Name { <type> member; ... };` of supported member types
  (scalar/vector/matrix or a previously-declared struct) is ACCEPTED. The converter emits an HLSL
  `struct` (member types re-spelled) PLUS a generated factory `Name make_Name(...)`, and rewrites the
  GLSL constructor `Name(a,b)` -> `make_Name(a,b)` (HLSL has no struct constructor). Struct-typed
  locals/params/returns and member access `s.field` work; member types are registered in
  `TypeInference` so **a matrix-typed member still hits the matrix-multiply trap** (`s.rot * v` ->
  `mul(v, s.rot)`, proven by `struct_basic.glsl`). Member access is emitted verbatim (no swizzle
  normalization, so a field named e.g. `sp` is not mangled). Nested/inline-struct members, struct array
  members, a combined `struct{..}var;` form, an empty struct, a name collision, and a forward-referenced
  struct stay loud, located rejects. *(`StructDecl`/`StructMember` AST, `Parser.ParseStruct`,
  `TypeInference` struct table + `InferSwizzle`/`InferCall`, `HlslEmitter.EmitStruct` + factory rewrite.)*
- **G7 — arrays.** A fixed-size array at const/mutable global and local scope is ACCEPTED:
  `const float k[3] = float[](a,b,c);` / `vec3[2](...)` -> `static const float k[3] = { a, b, c };`,
  and `float arr[4];` locals. The GLSL array constructor `type[](...)` / `type[N](...)` becomes an HLSL
  brace list; the element type is inferred so an indexed element type-checks for the traps. Unsized /
  runtime-sized arrays, a declared-size vs constructor-element-count mismatch, a non-constant array
  size, and array params/returns stay loud rejects. *(`ArrayConstructorExpr` AST, `ArraySize` on the
  decl nodes, `Parser.ParseArraySuffix`/`ParseArrayConstructorRest`/`ValidateArrayInit`,
  `TypeInference` array element table, `HlslEmitter` brace-list emission.)*
- **G7 — added intrinsics.** `fwidth` -> same-named HLSL intrinsic (valid in the `ps_3_0` harness);
  `matrixCompMult(a,b)` -> componentwise `(a * b)` emitted DIRECTLY (NOT through the matrix-order trap).
  `roundEven` (no faithful HLSL round-half-to-even) and the mip-bias `texture(s,uv,bias)` form (its
  `tex2Dbias` does not compile on the GL/DX SM4 targets) are loud rejects rather than silent-wrong or
  GL/DX-incompatible output. *(`IntrinsicTable`, `HlslEmitter.EmitCall`.)*
- **G7 — parser hardening.** The GLSL comma (sequence) operator `a, b, c` is now parsed at
  full-expression sites (`for` headers `i++, j--`, comma statements) as a `SequenceExpr`, distinct from
  the comma SEPARATORs in argument lists / declarators; and a type name immediately followed by `(` at
  statement start is treated as a constructor expression, not a malformed declaration. *(`Parser.ParseExpression`
  comma handling, `SequenceExpr` AST + emitter/inference.)*

### G2 as-built (2026-06-19) — plain-GLSL `void main()` entry mode

The converter now accepts a SECOND single-pass entry convention, broadening it from pure-ShaderToy to
general single-pass GLSL image shaders (glslViewer / Bonzomatic / Shadertoy-export). Correctness was the
priority: anything ambiguous stays a loud, located reject.

- **Detection (by entry NAME, no flag).** `mainImage` defined and `main` not → ShaderToy mode
  (unchanged). `main` defined and `mainImage` not → plain-GLSL mode. Neither → "no entry point" reject.
  **Both → ShaderToy mode + Warning** (the `void main()` wrapper is dropped — see *G2.1 as-built*
  below; this was originally an ambiguous reject, revised 2026-06-19 (f) once the both-entries shape
  proved to be ~a third of real-corpus failures). Detection is on parsed
  function names, not substrings, so a `mainImage`-referencing comment does not trip it.
  *(`EntryMode`/`AstScan`, `ShaderToyConverter.DetectEntryMode`/`ResolveMainEntry`.)*
- **`void main()` validation.** Must take no parameters; multiple `main` → reject.
- **Fragment output.** Either the legacy `gl_FragColor` (no declaration) OR a single top-level
  user-declared `out vec4 <name>;` (incl. `layout(location = N) out vec4 <name>;`). The `out vec4`
  declaration is CONSUMED, not emitted as a parameter/global; its name is the local the synthesized PS
  returns as `COLOR0`. A `main()` with neither a user `out vec4` nor any `gl_FragColor` write → loud
  reject; more than one `out vec4` → reject. *(`FragmentOutputDecl` AST,
  `Parser.ParseFragmentOutputDecl`/`ConsumeLayoutQualifier`.)*
- **`gl_FragCoord`.** Maps to the SAME pixel coord the ShaderToy harness uses for `fragCoord` (bottom-
  left Y origin, `.xy` = pixel coord, `.z` = 0, `.w` = 1); the harness PS reuses the identical Y-flip
  so the rendered orientation matches. The fragment output and `gl_FragCoord` are bridged as
  `static float4` globals (the translated `void main()` writes/reads them; the PS sets `gl_FragCoord`,
  calls `main()`, returns the output). *(`HarnessGenerator.EmitPlainGlslPixelShader` + the static-global
  bridge, `TypeInference.DeclareBuiltinGlobal`.)*
- **Resolution / time** come from the existing built-in / custom-uniform / alias handling (G1/G3/G4),
  no special case; an undeclared identifier still rejects (L1). Everything else is the same translator;
  only the entry/harness wrapping differs.
- **Validation.** Unit suite 234 green (0 warn). Golden compile-sweep **47/47 on OpenGL / DirectX_11 /
  FNA** (+3 main-mode goldens, no regressions). Render-proof **4/4 (exit 0)**, including the new
  `main_gradient` plain-GLSL case which asserts the SAME orientation as the ShaderToy `gradient_uv`
  (proving `gl_FragCoord`'s Y maps right in main mode). Scratch re-measure **unchanged at 34.6 % /
  88.7 % / 30.7 %** over the 153-`mainImage` set: this corpus (sampled by a `mainImage` GitHub search)
  has **0 pure plain-GLSL `main()` shaders**, so G2 adds no new conversions HERE; the 6 `main`-only
  files all correctly reject (3 define BOTH a `mainImage` and a `main` = ambiguous, 3 hit unsupported
  constructs). G2's value is reach to the glslViewer/Bonzomatic class, with never-silent-wrong held.
- **New fixtures/tests.** `corpus/authored/main_glfragcolor.glsl`, `main_out_var.glsl`,
  `main_custom_resolution.glsl` (+ goldens); `corpus/reject/both_entry_points.glsl`,
  `main_no_output.glsl`; `EntryModeTests.cs` (detection, `gl_FragColor`/out-var/`gl_FragCoord`,
  ShaderToy-mode-unchanged, both-entries + no-output + no-params rejects); render-proof
  `shaders/main_gradient.glsl` wired into the driver catalog.

### G2.1 as-built (2026-06-19 (f)) — both-entries (`mainImage` + standalone `void main()`) now CONVERTS

The single biggest real-world coverage bug. A large share of real third-party ShaderToy shaders define
BOTH a ShaderToy `void mainImage(out vec4, in vec2)` AND a standalone wrapper
`void main(){ mainImage(gl_FragColor, gl_FragCoord.xy); }` (glslViewer / Bonzomatic / desktop-runner
style, so the same `.glsl` runs outside ShaderToy). That shape was previously the "ambiguous entry
point" reject (G2) and accounted for ~a third of all real-corpus failures.

- **Fix.** When BOTH are present the converter now PREFERS ShaderToy mode (`mainImage` is canonical) and
  **drops the user `void main()`** — it is NOT translated or emitted (our harness synthesizes its own
  fullscreen VS/PS that calls `mainImage` directly, and the wrapper's `gl_FragColor`/`gl_FragCoord`
  write target is only declared in the plain-GLSL harness, so emitting the wrapper would dangle). A
  **Warning** records that the standalone wrapper was ignored in favor of `mainImage`.
  *(`ShaderToyConverter.DetectEntryMode` now takes the diagnostics list and returns `EntryMode.ShaderToy`
  with a Warning; the caller `RemoveAll(f => f.Name == "main")` after `ValidateMainImage`.)*
- **No merge, no regression.** A `main()` that does substantive work beyond calling `mainImage` is still
  dropped (for a ShaderToy-derived file `mainImage` is canonical; the two are never merged). Single-entry
  shaders, plain-GLSL `main`-only mode, and the "no entry point" reject are all UNCHANGED. Dropping the
  wrapper leaves the `mainImage` translation byte-identical to the wrapper-less shader (asserted in
  `EntryModeTests`).
- **Validation.** Unit suite **259 green (0 warn)**. Golden compile-sweep **52/52 on OpenGL /
  DirectX_11 / FNA** (+1 both-entries golden, no regressions). Render-proof **4/4 + multipass (exit 0)**.
  **Scratch re-measure: conversion 44.4 % (71/160), up from 33.8 % (54/160) — a +17-shader gain**,
  confirming the both-entries shape was a top failure cause.
- **Fixtures/tests.** Retired `corpus/reject/both_entry_points.glsl`; added
  `corpus/authored/mainimage_with_main_wrapper.glsl` (+ golden). `EntryModeTests` replaces the
  ambiguous-reject case with: both-entries → ShaderToy chosen + `main` dropped + Warning;
  `mainImage` output identical with/without the wrapper; substantive-`main` still dropped. `RejectCorpusTests`
  drops the `both_entry_points` keyword row.

### G8 as-built (2026-06-19 (g)) — LOW-RISK / HIGH-YIELD batch

Seven correctness-first families from the 160-shader failure analysis, each a real corpus bucket.
Correctness beats coverage: anything not faithfully handleable stays a LOUD, located reject.

- **Sized arrays in all 3 contexts + brace/constructor init.** A `[N]` size suffix is now accepted
  AFTER the base type (the GLSL-canonical position) in global const, local var, AND function **parameter**
  declarations, in addition to the existing name-side `[N]`. A GLSL brace initializer list `{ ... }`
  (GLSL ES 3.00+ aggregate init, new `BraceInitExpr`) joins the `T[](...)`/`T[N](...)` array constructor
  as an accepted array initializer; both emit an HLSL brace list. An array **parameter** spells its size
  on the HLSL declarator name (`inout float k[N]`). A size on BOTH the type and name, a non-constant /
  macro size, an unsized array, and an array RETURN type stay rejects. *(Parser type-side `[N]` in
  `ParseTopLevel`/`ParseLocalVarDecl`/`ParseParam`, `ParseInitializer`, `BraceInitExpr`, `ParamDecl.ArraySize`,
  `HlslEmitter.EmitFunction` array param.)*
- **Bitwise operators.** Lexer tokens + parser grammar for `& | ^ << >>` (correct C precedence: shifts
  below relational, then `&`, `^`, `|`) and the compound-assign forms `&= |= ^= <<= >>=`. Map straight
  through to HLSL (valid on int); `&&`/`||` stay distinct. *(Lexer 3-char `<<=`/`>>=` + 2-char `&=`/`|=`/`^=`,
  `Token` kinds, `ParseAssignment`; emitter pass-through.)*
- **`gl_FragCoord` body built-in.** Now resolves anywhere in a `mainImage`/`main` body as a `float4`
  (`.xy` = fragCoord with the bottom-left-Y convention, `.z` = 0, `.w` = 1). In ShaderToy mode the harness
  publishes a `static float4 gl_FragCoord;` and sets it from the same pixel coord before calling
  `mainImage`, only when referenced. *(`TypeInference.DeclareBuiltinGlobal` in both modes, `HlslEmitter`
  `UsedGlFragCoord`, `HarnessGenerator` static + PS set.)*
- **`uint`/`uvec` → signed int.** `uint`→`int`, `uvec2/3/4`→`int2/3/4` (moved out of `RejectedTypes` into
  the type tables). Behaviorally equivalent under the bitwise ops we pass through; a `uint`-heavy bit-hash
  legitimately hits the FNA/fx_2_0 no-integer-bitwise ceiling (GL/DX compile fine).
- **Redundant `uniform sampler2D iChannelN;`** → accepted-and-ignored (the built-in channel is injected).
  A `sampler2D` of a non-`iChannel` name stays a custom sampler param.
- **Redundant built-in WITH initializer** (`uniform vec3 iResolution = vec3(1920,1080,1);`) → dropped
  (initializer irrelevant). **Multi-declarator uniforms** `uniform float a, b, c;` → comma list supported,
  each declarator classified independently (built-in dropped / alias folded / custom emitted).
  *(Parser `HandleQualifiedTopLevelDecl` refactor into `HandleQualifiedDeclarator` + `ConsumeDeclaratorTail`.)*
- **`OF_GLSL_SHADER_HEADER`** bare openFrameworks header token stripped (whole-word) in the preprocessor;
  `#pragma`/`#pragma header` were already stripped.

**Validation.** Unit suite **298 green (0 warn)**. Golden compile-sweep **OpenGL 61/61, DirectX_11 61/61,
FNA 59/61** (the 2 FNA misses, `bitwise_ops`/`uint_type`, are the D3D9 fx_2_0/SM3 no-integer-bitwise
ceiling — inherent, not a converter bug). Render-proof **4/4 + multipass (exit 0)**. **Scratch re-measure:
conversion 51.2 % (82/160), up from 44.4 % (71/160) — a +11-shader gain.** Fixtures: 9 new
`corpus/authored/*.glsl` (+ goldens), 1 new `corpus/reject/array_nonconst_size.glsl`, new
`Phase46BatchTests` unit class.

### G9 as-built (2026-06-20) — SECOND fixable batch (stage I/O, alternate entries, switch)

Five correctness-first families from the same failure analysis. Anything whose value/semantics can't be
known stays a LOUD, located reject — never silent-wrong.

- **Ignore leftover top-level stage I/O decls.** A top-level `in`/`varying`/`attribute` declaration (and
  the `layout(location = N) in <type> <name>;` form) is web/desktop-export VERTEX-STAGE leftover: the
  harness synthesizes its own vertex shader, so the declaration is **IGNORED** (not rejected, not emitted
  as a parameter/global). For the common case where such a varying is referenced as the fullscreen UV, a
  fixed set of conventional coordinate-varying names (`texCoord`, `vUv`, `vUV`, `v_texcoord`,
  `vTextureCoord`, `vTexCoord`, `v_coord`, `uv`, `texcoord`) is aliased to the harness normalized screen
  UV (`fragCoord / iResolution.xy`, [0,1], bottom-left Y), bridged as a `static float2 sd_ScreenUV;` the
  PS sets. A NON-coordinate ignored varying that is referenced stays a loud **undeclared-identifier**
  reject (no per-vertex value to invent). *(`ScreenCoordVaryings`, `Parser` ignore + alias collection,
  `TranslationUnit.ScreenUvAliases`, `HlslEmitter` rewrite + `UsedScreenUv`, `HarnessGenerator` static/set.)*
- **OpenFL / Haxe `#pragma header` exports.** `#pragma header` is stripped (G5 already strips `#pragma`);
  `openfl_TextureCoordv` (vec2) -> the harness screen UV (same `sd_ScreenUV` bridge), `openfl_TextureSize`
  (vec2) -> `iResolution.xy`. *(`HlslEmitter.EmitIdentifier` special-cases.)*
- **Godot / GdShaders 4-arg `mainImage`.** The alternate signature
  `void mainImage(in vec4 inputColor, in vec2 uv, out vec4 outputColor)` (the `in` may be `const in` or
  omitted) is recognized by its 3-parameter shape as a valid ShaderToy-mode entry: `uv` = Godot SCREEN_UV
  ([0,1]) set from the harness, `inputColor` = the iChannel0 sample at `uv` (iChannel0 always exposed in
  this mode), `outputColor` = the returned color. Any other 3-parameter `mainImage` is a loud reject; the
  standard 2-arg `mainImage` and plain-GLSL `main` paths are unchanged. *(`MainImageShape` enum,
  `ShaderToyConverter.ValidateMainImage`, `HarnessGenerator.EmitGodotPixelShader`.)*
- **libretro / RetroArch (`.slang`) VERTEX/FRAGMENT stage split.** When a source gates both stages on
  `VERTEX`/`FRAGMENT` (`#if defined(VERTEX) ... #elif defined(FRAGMENT) ... #endif` / `#ifdef`) and defines
  NEITHER itself, the preprocessor seeds `FRAGMENT` = 1 (VERTEX left = 0) so the fragment branch (the real
  `mainImage`) survives instead of being stripped to "no entry point". Scoped narrowly to the
  VERTEX/FRAGMENT pair (requires BOTH guards present, NEITHER locally defined) so it cannot misfire on
  ordinary `#if` logic. *(`Preprocessor.UsesVertexFragmentStageSplit` seed.)*
- **`switch` -> if/else lowering.** A `switch (e) { case K: ...; break; default: ...; }` is parsed and
  lowered to an `if`/`else if`/`else` chain (portable to SM3/FNA, which have no native `switch`): the
  selector is hoisted once into `sd_swN`, stacked `case` labels OR into one condition, and `default`
  becomes the final `else`. True **fall-through** (a non-empty case body with no terminating
  `break`/`return`), and a `default` stacked with `case` labels on one body, stay loud rejects.
  *(`SwitchStmt`/`SwitchCase` AST, `Parser.ParseSwitch`/`EndsCaseCleanly`, `HlslEmitter.EmitSwitch`,
  `AstScan` switch walk.)*

**Validation.** Unit suite **327 green (0 warn)**. Golden compile-sweep **OpenGL 62/62, DirectX_11 62/62,
FNA 60/62** (the 2 FNA misses remain `bitwise_ops`/`uint_type`, the inherent fx_2_0/SM3 ceiling — all 5
new fixtures compile on GL/DX/FNA). Render-proof **5/5 + multipass (exit 0)**, with a new
`varying_gradient` case proving the screen-UV alias renders the SAME orientation as the gradient oracle.
**Scratch re-measure: conversion 55.0 % (88/160), up from 51.2 % (82/160) — a +6-shader gain.** Fixtures:
5 new `corpus/authored/*.glsl` (+ goldens), 2 new rejects (`switch_fallthrough`,
`stage_in_noncoord_referenced`), new `Phase46StageIoTests` unit class; the former `switch_statement`
reject moved to `authored/` (now in-subset).

### FINAL coverage wave as-built (2026-06-20) — correctness-held ceiling + named permanent-tail rejects

The last fixable batch from the 160-shader analysis, correctness-first throughout (a converted shader
must actually COMPILE; an honest reject beats silently-wrong output). Fixed:

- **mainImage prototype vs definition.** A forward `void mainImage(...);` (empty body) no longer counts
  as a duplicate definition. The common prototype + `void main()` wrapper + real definition desktop-export
  shape now converts (5 scratch shaders). A true multi-DEFINITION (concatenated multipass file) still
  rejects, with a message that names the likely cause.
- **The "returning" mainImage form.** `vec3/vec4 mainImage(in vec2 fragCoord)` that RETURNS the color
  (the file's own `void main()` assigns it) is recognized + wired; the harness calls
  `mainImage(fragCoord)` and returns it (a `vec3` padded to `float4(rgb,1)`). Render-proven by
  `returning_gradient` (same gradient + orientation as the standard form). 4 scratch shaders.
- **Function overloading.** Same-name helpers with different signatures are emitted in full; HLSL
  resolves each call by argument type, exactly as GLSL. A true identical redefinition is still an error.
- **Array sizes from `#define` / `const int` / const-expression.** `[NUM]` (with `const int NUM = 41;`),
  `[MAX]` (`#define`d), and `[NUM_TRIANGLES * 3]` (a const-int arithmetic expression) all evaluate to a
  literal HLSL size at convert time. A genuinely runtime size stays a loud reject.
- **Struct array members.** `struct S { float w[4]; vec3 t; }` emitted directly (HLSL allows it); the
  `make_S` factory copies the array member element-by-element (no whole-array assignment in FX9/SM3).
- **Single-argument matrix constructors.** `matN(scalar)` (diagonal; `mat3(1)` = identity) and
  `matN(matM)` (upper-left submatrix + identity completion) expand to an explicit `floatNxN(...)` grid,
  consistent with the trap-2 transpose convention (the two transposes cancel).
- **Self-referential macro C-rule.** The expander now follows the standard "blue-paint" rule (a macro's
  own name in its expansion is left as a plain identifier, not re-expanded), turning a runaway-reject
  false positive into correct output. The 2 affected scratch shaders are host-`$`-templated and now fail
  loudly + precisely on the unresolvable `$placeholder` (the correct out-of-scope outcome).

**Permanent tail — named, located rejects (never a guess):** `sampler2D` function PARAMETERS (valid HLSL
but uncompilable on the legacy-FX9 GL/DX path — proven by rendering, the same class as the mip-bias
reject), `textureCube` / `texture3D` (cubemap/3D), `getLastFrameColor` (feedback / multipass), the GL
stage built-ins `gl_FragDepth` / `gl_FrontFacing` / `gl_TexCoord` / `gl_FragData`, host-specific
undeclared globals (`iCurrentCursor`, ISF `RENDERSIZE`, app values — "depends on a host-provided value"),
and host-template `$placeholder` tokens. We do NOT auto-expose an arbitrary unknown as a uniform (that
would be guessing); declare it as a `uniform` to drive it.

**Validation.** Unit suite **371 green (0 warn)**. Golden compile-sweep **OpenGL 69/69, DirectX_11 69/69,
FNA 67/69** (the 2 FNA misses remain `bitwise_ops`/`uint_type`; all 7 new fixtures compile on GL/DX/FNA).
Render-proof **6/6 + multipass (exit 0)** with the new `returning_gradient`. **Scratch re-measure:
conversion 61.2 % (98/160), up from 55.0 % (88/160) — a +10-shader gain.** Of the 10 newly-converted
scratch shaders, **7 compile cleanly on GL/DX/FNA**; the other 3 reach pre-existing §5 transpiler edge
cases (B4 vector truncation, a struct-typed ternary, a shader that redeclares `iTime` as its own mutable
global) and fail LOUDLY at compile (non-zero exit), never silent-wrong. Fixtures: 7 new
`corpus/authored/*.glsl` (+ goldens), 6 new rejects (`sampler_param`, `intrinsic_texturecube`,
`feedback_lastframe`, `gl_fragdepth_builtin`, `host_specific_uniform`, `host_template_placeholder`), new
`Phase46CoverageWaveTests` unit class; the `PreprocessorTests` recursive-macro case corrected from a
runaway-reject assertion to the C-rule expansion.

**Realistic single-pass ceiling.** ~61 % of this broad real-world corpus is the practical ceiling for a
faithful single-pass converter. The permanent remainder is genuinely out of scope: multipass / feedback /
buffer-graph shaders, cubemap / 3D / mip-bias texture sampling, `texelFetch` / depth / integer-bitwise-on-
FNA, host-specific uniforms whose values we cannot invent, host-`$`-templated shaders, sampler-passing
helpers (uncompilable on the GL/DX FX9 path), and a small tail of pre-existing transpiler edge cases. For
each, a loud well-named reject is the correct outcome.

### Multipass batch-export mode as-built (2026-06-19) — batch-convert + documented wiring, NOT an orchestrator

The one "genuinely-large unbuilt feature" called out above (multipass: Buffer A–D / feedback) is now
addressed **by design as a batch converter, not a runtime engine.** Owner-directed scope: **do NOT
build a ShaderToy runtime / orchestrator / emulator.** We "accept the syntax" (convert each tab with
the EXISTING single-pass converter, no behavior change) and hand the consumer the pieces + a documented
~15-line wiring example they drop into their own game's Draw loop. **The render-graph is the consumer's
job, the way MonoGame already works.**

- **Export model + parser.** `ShaderToyProject.Parse(json)` (System.Text.Json) reads the ShaderToy API
  JSON (`{ ver, info, renderpass:[...] }`) into a typed model: passes (`name`/`type`/`code`/`inputs`/
  `outputs`), inputs (`ctype`/`channel`/`sampler{wrap,filter,...}`), outputs (`id`/`channel`). Pass
  type ∈ image|buffer|common|sound|cubemap; channel ctype ∈ texture|buffer|keyboard|music|musicstream|
  mic|webcam|volume|cubemap|video. (`src/ShadowDusk.ShaderToy/Multipass/ShaderToyProject.cs`.)
- **`MultipassConverter.Convert(project, options) → MultipassResult`.** Prepends the single `common`
  tab to every other pass; converts each `buffer`/`image` tab via the unchanged
  `ShaderToyConverter.Convert` (per-pass `.fx` + diagnostics); **skips `sound`/`cubemap` with a
  Warning**; resolves each channel (buffer → source pass, or **self = feedback**; texture → external
  `src`; other ctypes → Warning "unsupported, leave unbound"); records sampler wrap/filter; records the
  canonical order (buffers A..D in name order, then Image; Common never rendered).
  (`Multipass/MultipassConverter.cs`, `Multipass/MultipassResult.cs`.)
- **Artifacts.** `MultipassManifest.ToJson` emits the machine-readable `manifest.json` (ordered passes,
  each pass's `.fx` + channel→source wiring + feedback flags + sampler modes); `ToWiringMarkdown` emits
  the human `WIRING.md` (buffer graph + a concrete ~15-line MonoGame `RenderTarget2D` example tailored
  to the graph). (`Multipass/MultipassManifest.cs`.)
- **CLI.** `shadertoy2fx --multipass <export.json> -o <outdir>` writes each pass's `.fx` +
  `manifest.json` + `WIRING.md`; **loud non-zero exit** if any pass fails to convert (per-pass errors
  in MGCB form on stderr). The existing single-file mode is **unchanged**.
- **Fixtures/tests (OUR OWN authored export JSON, no third-party shader).**
  `tests/.../corpus/multipass/chain2.json` (Common tab + Buffer A gradient + Image tint reading
  iChannel0=Buffer A + a `sound` pass + a `keyboard` channel to exercise skip/warn) and `feedback.json`
  (Buffer A reads itself = feedback trail + Image + an external-texture channel). `MultipassTests.cs`
  asserts: JSON parses; pass count/order (buffers then Image; multi-buffer sorted A,B); Common prepended
  (the Image's `tint()` resolves only because Common is prepended; without it, loud reject); buffer
  wiring A→Image; feedback detected on the self-referencing buffer; sound/cubemap skipped with a
  warning; unsupported ctype warned; each emitted per-pass `.fx` non-null and **compiles on OpenGL via
  the real ShadowDusk CLI** (Integration theory). Per-pass `.fx` + `manifest.json` are **goldened**
  (`SHADERTOY2FX_UPDATE_GOLDENS`).
- **Render proof.** `render-proof/MultipassChain2Proof.cs` is a small, explicitly-labeled hand-wired
  example (the exact loop a consumer writes): convert `chain2` → compile each `.fx` → allocate a
  `RenderTarget2D` for Buffer A → render A → bind A as iChannel0 of Image → render Image offscreen →
  read back and **assert the analytic tint of A's gradient** (R=uv.x, G=storedY*0.5, B=0.125) → save
  `output/multipass_chain2.png`. **5/5 analytic asserts pass, exit 0.**
- **Validation (this machine).** `dotnet test tools/shadertoy2fx/shadertoy2fx.slnx` green (0 warnings,
  TreatWarningsAsErrors + EnforceCodeStyleInBuild on); golden compile-sweep of the 4 per-pass `.fx`
  **4/4 on OpenGL / DirectX_11 / FNA**; the single-pass render-proof still **4/4** plus the new
  multipass chain2 example **PASS**. No existing converter source or existing golden was changed — the
  multipass mode is purely additive.

## Why this shape (and why it is separate)

We considered three ways to get ShaderToy GLSL into the engine (full discussion lives in this
doc's history; summary here):

1. **GLSL → glslang → SPIR-V → existing GL tail → `.mgfx`.** Reuses the SPIR-V→GL tail but needs
   `glslang` vendored across 4 RIDs + WASM, and reaches **GL only**.
2. **GLSL → glslang → SPIR-V → SPIRV-Cross-HLSL → `.fx` → pipeline.** Same `glslang` native cost,
   but reaches **all** backends.
3. **GLSL → managed source transpiler → `.fx` → pipeline.** **No native dependency at all**, and
   reaches **all** backends. The cost moves from "vendor a binary forever" to "write translation
   code that lives in our normal C# packages."

**This phase is option 3.** For a project whose heaviest recurring burden is native packaging,
deleting the native dependency entirely is the decisive advantage. The price is real compiler
work with sharp correctness edges (see *Translation traps*), but it is **pure managed C#** with
no RID matrix, no WASM build, and no self-contained-packaging story to maintain. If the managed
transpiler's long tail proves too costly, option 2 (glslang + SPIRV-Cross-HLSL) is the documented
fallback — but we do not pay for it unless option 3 fails.

**Separate-tool discipline.** The transpiler must not be wired into `CompilationPipeline` or
`EffectCompiler`. It is its own project (CLI + library), and its contract ends at "emit `.fx`
text." This keeps the faithful pipeline untouched (no risk to the `mgfxc`-equivalence promise)
and keeps the experiment disposable: if it does not pan out, nothing in the product depends on it.

**Depends on:** nothing in the pipeline at build time. It *consumes* the existing pipeline only as
a downstream step a user runs (`shadertoy2fx in.glsl > out.fx`, then `mgfxc`/`EffectCompiler`).

---

## Overview — what the tool does

A ShaderToy shader is **not standalone-compilable** anything. It is a fragment-function body
against an implicit harness:

- entry point is `void mainImage(out vec4 fragColor, in vec2 fragCoord)` — there is no `main()`;
- the uniforms `iResolution`, `iTime`, `iMouse`, `iChannel0..3`, … are **predefined**, never declared;
- there is **no vertex shader** at all;
- optional **"Common"** tab code is shared/prepended.

The tool produces a self-contained HLSL `.fx` that wraps this into a real effect:

```
ShaderToy GLSL (image tab [+ common tab])
        │
        ├─ inject the ShaderToy uniform set as HLSL globals  (iTime float, iResolution float3, …)
        ├─ inject iChannel0..3 as Texture2D + SamplerState
        ├─ translate the GLSL body → HLSL  (the transpiler core)
        ├─ synthesize a fullscreen-triangle vertex shader (HLSL)
        ├─ synthesize a pixel shader that calls the translated mainImage with the right fragCoord
        └─ wrap in `technique { pass { VertexShader = …; PixelShader = …; } }`
        ▼
   self-contained .fx  →  (existing ShadowDusk pipeline)  →  .mgfx / .fxb for GL / DX / FNA
```

The emitted `.fx` is ordinary HLSL/FX9. From that point it is indistinguishable from any other
`.fx` the pipeline already handles.

---

## Scope & Non-Goals

**In scope (v1):**
- **Single-pass "image" shaders only** — one `mainImage`, optionally with a "Common" tab prepended.
- The standard ShaderToy uniform set, mapped to HLSL globals + texture/sampler pairs:
  `iResolution (float3)`, `iTime (float)`, `iTimeDelta (float)`, `iFrame (int)`,
  `iMouse (float4)`, `iDate (float4)`, `iChannelTime[4] (float)`, `iChannelResolution[4] (float3)`,
  `iSampleRate (float)`, `iChannel0..3` → `Texture2D` + `SamplerState`.
- A managed GLSL→HLSL translator covering the **constrained ShaderToy subset** (types,
  operators, the common intrinsic set, control flow, user functions, `texture()` calls).
- A synthesized fullscreen-triangle VS and the `mainImage` wrapper PS.
- A `technique`/`pass` wrapper so the `.fx` is complete.
- **Fail loudly** (clear diagnostic, non-zero exit) on any construct outside the supported subset
  — never emit subtly-wrong HLSL silently (project constraint 5).
- Delivery as a **separate** CLI (`shadertoy2fx`) + a small library, in its own folder, **not** in
  `ShadowDusk.slnx`'s product graph initially (treat like the `validation/*` drivers: real but
  out-of-band).

**Out of scope / Non-Goals:**
- **Multipass** ShaderToy shaders (Buffer A–D, feedback/ping-pong, the "Common"+multiple image
  buffers model). That needs render-target orchestration at *runtime*, not just translation — a
  much bigger, v2+ undertaking.
- **Non-texture iChannels**: audio, video, cubemap, keyboard, webcam channels. `iChannelN` is
  modeled as a 2D texture only.
- **Wiring into the compiler pipeline.** The tool emits `.fx`; it never becomes a frontend of
  `EffectCompiler` (that would be option 1/2, explicitly not this phase).
- **The runtime render helper.** Producing a loadable effect is *necessary but not sufficient* to
  see pixels — the consumer's game must set `iTime`/`iResolution`/`iMouse`/`iChannelN` each frame
  and draw a fullscreen triangle. That helper is a **separate deliverable** (see *Runtime helper*),
  not part of this conversion tool.
- Any change to existing GL/DX/FNA output (this phase adds a sibling tool, touches no pipeline code).

---

## Architecture & key decisions

- **Source-to-source, managed, no native dep.** The translator is hand-written C# (a small
  GLSL-subset lexer/parser + an HLSL emitter). It ships as ordinary managed code — no `glslang`,
  no SPIR-V, no RID matrix. This is the entire reason to prefer this over options 1/2.
- **Constrained subset, loud boundary.** We do **not** attempt full GLSL. We support the subset
  ShaderToy image shaders actually use, and we *reject* (with a precise message + the offending
  construct) anything else. A reject is a correct outcome, not a failure of the tool — it protects
  the "never silently wrong" rule.
- **Uniform model.** ShaderToy's fixed uniforms become HLSL globals (the pipeline already packs
  globals into the constant buffer / `*_uniforms_vec4[]` model). `iChannelN` become `Texture2D` +
  `SamplerState` pairs named so a runtime helper can bind them predictably. Only emit the uniforms
  the shader actually references (lean parameter list).
- **Fullscreen pass.** Synthesize a standard fullscreen-triangle VS in HLSL that outputs clip-space
  position and a `fragCoord`-equivalent (pixel coordinates derived from `iResolution`). The PS
  computes `fragCoord` and calls the translated `mainImage`.
- **One `.fx`, every backend.** The emitted `.fx` is backend-neutral HLSL/FX9. The existing
  pipeline decides GL vs DX vs FNA. **Caveat:** FNA's target is fx_2_0 / SM3, which has real
  instruction-count and loop limits — complex ShaderToy shaders may compile fine for GL/DX but
  **legitimately exceed SM3 on FNA**. That is an inherent fx_2_0 limit (the pipeline already fails
  loudly there), not a tool bug; document it.

### Translation traps (the sharp edges — get these right or it renders wrong)

These are the specific GLSL→HLSL semantic differences that must be handled, not just syntax-mapped:

- **Matrix multiply order is reversed.** GLSL is column-major: `M * v`. HLSL row-major: `mul(v, M)`
  (and `mul(M, v)` for the other side). Get this wrong and every rotation/transform is subtly broken.
  This is the single highest-risk trap.
- **`mod` differs for negatives.** GLSL `mod(x,y)` = `x - y*floor(x/y)`; HLSL `fmod` truncates
  toward zero. Emit the GLSL-equivalent expression, not a bare `fmod`, when sign can be negative.
- **`gl_FragCoord` origin.** GL origin is bottom-left; D3D top-left. The Y of `fragCoord` must be
  flipped relative to `iResolution.y` so output matches ShaderToy's reference orientation.
- **Vector/matrix type spelling.** `vec2/3/4` → `float2/3/4`, `mat2/3/4` → `float2x2/3x3/4x4`,
  `ivec*`/`bvec*` likewise; **component access and constructors** mostly carry over but swizzle
  edge cases need checking.
- **Intrinsic renames.** `mix`→`lerp`, `fract`→`frac`, `atan(y,x)`→`atan2(y,x)` (and `atan(x)`→`atan`),
  `dFdx/dFdy`→`ddx/ddy`, `texture(s,uv)`→`s.Sample(...)` (or `tex2D` for fx_2_0 reach),
  `mix`/`clamp`/`smoothstep`/`step` semantics verified, `inversesqrt`→`rsqrt`, etc. Maintain an
  explicit, tested mapping table; **anything not in the table is a loud reject.**
- **Integer/`%` semantics, `bool` vectors, `discard`** — verify each against HLSL.
- **Precision qualifiers** (`highp`/`mediump`/`lowp`) — strip (HLSL has no direct equivalent in FX9).

A concrete, exhaustive mapping table (type, operator, intrinsic, with the reject-list) is the first
real deliverable — it sizes the whole effort.

---

## Phase 0 — the cheap probe (do this FIRST, before writing the parser)

Prove the bet by hand before building the machine:

1. Pick **one** representative single-pass ShaderToy image shader.
2. **Hand-translate** it into a `.fx` using the harness + mapping above (no tool yet).
3. Compile that `.fx` with **today's** ShadowDusk for **OpenGL and DirectX** (and FNA if it fits SM3).
4. Load + render it (fullscreen triangle, hand-driven uniforms) and eyeball against the ShaderToy page.

If the hand-built `.fx` renders correctly on GL and DX, the bet holds and the transpiler is "just"
automating a proven recipe. If it does not, we learn the real blockers (coordinate convention,
uniform binding, an intrinsic gap) for the price of one afternoon — before committing to the parser.

---

## Tasks

- [ ] **Phase 0 probe**: hand-translate one ShaderToy shader to `.fx`, compile GL+DX with the
      existing pipeline, render and compare to the ShaderToy reference. Record findings.
- [ ] Write the **GLSL→HLSL mapping table** (types, operators, intrinsics) + the explicit
      **reject-list** of unsupported constructs. This sizes the subset.
- [ ] Scaffold a separate project pair: `tools/shadertoy2fx` (library + CLI), **not** added to the
      product graph in `ShadowDusk.slnx` initially (out-of-band like `validation/*`).
- [ ] Implement the **harness generator**: ShaderToy uniform set → HLSL globals + `Texture2D`/
      `SamplerState` pairs (only the referenced ones); the fullscreen-triangle VS; the `mainImage`
      wrapper PS with correct `fragCoord` (incl. Y-flip); the `technique`/`pass` wrapper.
- [ ] Implement the **transpiler core**: a GLSL-subset lexer/parser + HLSL emitter honoring every
      *Translation trap* (matrix order, `mod` sign, intrinsic renames, type spelling).
- [ ] Implement **loud rejection**: any unsupported construct → precise diagnostic (construct +
      location) + non-zero exit; never emit silently-wrong HLSL.
- [ ] Support the optional **"Common" tab** (prepend shared code before translation).
- [ ] **Tests**: a corpus of ~10–15 single-pass ShaderToy shaders → transpile → compile with the
      existing pipeline for GL **and** DX (FNA where it fits SM3); assert compile success; golden
      the emitted `.fx` text for determinism.
- [ ] **Validation/oracle**: capture each corpus shader's ShaderToy WebGL reference image; render the
      transpiled-then-compiled effect (GL) and pixel-diff against the reference with a documented
      tolerance. Record that the oracle here is **ShaderToy WebGL**, not `mgfxc` (a different,
      honestly-weaker bar — state it).
- [ ] Document the FNA/SM3 instruction-limit caveat and the GL-is-most-faithful note.
- [ ] Run `/platform-check` on the new tool (it is build/CLI-time, must stay cross-platform).

## Acceptance Criteria

- [ ] **Phase 0 probe passes**: a hand-built `.fx` from a real ShaderToy shader compiles with the
      existing pipeline and renders recognizably like the ShaderToy reference on GL and DX.
- [ ] `shadertoy2fx in.glsl` emits a **self-contained, valid `.fx`** for the supported subset.
- [ ] That `.fx`, fed to the **unchanged** existing pipeline, compiles to **OpenGL and DirectX**
      `.mgfx` (and FNA `.fxb` when within SM3 limits) with **no pipeline code change**.
- [ ] The compiled effect **loads** in a real MonoGame/KNI runtime and renders within the documented
      tolerance of the ShaderToy WebGL reference (GL path is the gold reference).
- [ ] Unsupported constructs produce a **clear, located diagnostic** and a non-zero exit — never a
      silently-wrong `.fx`.
- [ ] The tool adds **zero** native dependencies and makes **zero** changes to existing GL/DX/FNA
      output (a byte-diff of the existing corpus is unchanged).

## Definition of Done

A separate `shadertoy2fx` tool converts a single-pass ShaderToy image shader into a valid HLSL
`.fx`, which the **existing** ShadowDusk pipeline then compiles to MonoGame/KNI (GL/DX) and, within
SM3 limits, FNA — with no pipeline changes and no native dependency. A small corpus is transpiled,
compiled, and render-compared (tolerance) against the ShaderToy WebGL references, with GL as the
gold reference. Unsupported shaders fail loudly. The experiment has answered: **can we get
"ShaderToy → any XNA-family backend" for free by transpiling to `.fx`?** — with evidence either way.

---

## Runtime helper (separate deliverable — flagged, not in this phase)

Producing a loadable effect is **necessary but not sufficient** to see pixels. To actually render a
ShaderToy in-game, the consumer must, each frame: set `iTime`/`iResolution`/`iTimeDelta`/`iFrame`/
`iMouse`/`iChannelN`, bind a fullscreen triangle, and draw with the effect. Without that, the `.fx`
loads but draws nothing — which would violate the project's "it just works" directive if we shipped
the conversion alone.

So a real ShaderToy story needs a tiny **`ShaderToyEffect` runtime helper** (a small sample or
companion library) wrapping the loaded `Effect`:

```csharp
var toy = new ShaderToyEffect(content.Load<Effect>("myShader"));
toy.Update(gameTime, mouse, viewportSize);  // sets iTime/iResolution/iMouse/iFrame/iChannelN
toy.Draw(device);                            // binds fullscreen triangle + applies the pass
```

This is **out of scope for the conversion tool** (which only emits `.fx`) but must exist before the
feature is "usable in MonoGame/KNI" end to end. Tracked here so it is not forgotten when the
experiment succeeds.

---

## Open questions / risks

- **Subset long tail.** Clever ShaderToy shaders reach for intrinsics/constructs outside the common
  subset. Mitigation: loud reject + grow the mapping table from real failures. If the tail proves
  too large to hand-translate, fall back to **option 2** (glslang + SPIRV-Cross-HLSL) for breadth —
  documented but not built unless needed.
- **Matrix-order / `mod` / Y-flip correctness.** The highest-risk traps; a wrong call renders
  plausibly-but-wrong. Mitigation: targeted unit tests per trap + the render-diff corpus.
- **Oracle weakness.** There is no `mgfxc`/`fxc` oracle for "ShaderToy → engine" (it is not an
  `mgfxc` input), so the bar is ShaderToy's own WebGL output, which itself varies by driver. State
  the weaker bar honestly; do not claim `mgfxc`-equivalence here.
- **FNA/SM3 ceiling.** Complex shaders that compile on GL/DX may legitimately exceed fx_2_0 limits.
  Inherent, not a bug — surface the pipeline's existing loud failure and document it.
- **Cross-backend faithfulness.** GL output is closest to the WebGL reference; DX/FNA may differ a
  hair due to D3D conventions. Treat all-backend reach as a bonus; validate GL as gold, DX/FNA as
  "renders correctly," not "pixel-identical to WebGL."
- **Scope creep into multipass.** Buffer A–D is the gateway drug to a much bigger runtime project.
  Keep v1 strictly single-pass; multipass is a deliberate, separately-scoped future phase.
