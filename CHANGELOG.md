# Changelog

All notable changes to ShadowDusk are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

ShadowDusk is a cross-platform, in-memory drop-in `mgfxc` replacement: a self-contained
library that compiles `.fx` → `.mgfx` at runtime on Linux, macOS, and Windows, with output
that loads and renders identically to `mgfxc`'s in the real MonoGame/KNI runtime. All seven
`ShadowDusk.*` packages share a single version (see `Directory.Build.props` `<Version>`).

## [Unreleased]

### Added

### Changed

### Fixed

- OpenGL: `trunc()` (which SPIRV-Cross emits when lowering HLSL's truncating `%`/`fmod`) is now
  lowered to `sign(x)*floor(abs(x))`, a GLSL ES 1.00-safe expression. `trunc()` is a GLSL ES 3.00 /
  GL 1.30+ builtin, absent from the versionless legacy dialect ShadowDusk targets; strict GLSL ES
  1.00 front ends (ANGLE on macOS DesktopGL) rejected it as an undeclared identifier where lenient
  desktop drivers did not (Apos.Shapes issue #34).

## [0.14.1] - 2026-07-25

### Added

- Thin-ellipse slice in the OpenGL Apos.Shapes render gate (`validation/VsDriven -- apos`),
  supplementing the existing circle with a needle-thin ellipse compared same-backend against the
  mgfxc GL golden. Supplementary coverage for the issue #160 shape; the authoritative guard is a
  rewriter unit test.

### Changed

### Fixed

- **OpenGL regression from 0.14.0: thin/eccentric ellipses in iterative SDF shaders rendered from
  garbage distances (issue #160).** The issue #138 GL loop rewrite (`LowerBoundedHeaderlessForLoop`)
  bounded the hoisted `for` header with `< provenMax` instead of `<= provenMax`, so when the
  runtime trip count equalled the loop's ceiling the rewritten loop exited one iteration early and
  skipped the `else` branch that finalizes the solver's result, leaving it read from an
  uninitialized variable. Affected only OpenGL, only shaders whose SPIRV-Cross output takes this
  header-less-loop shape (e.g. Apos.Shapes' `EllipseSDF`); DirectX/DX12/Vulkan were never affected.

## [0.14.0] - 2026-07-24

### Added

- **New target: DirectX 12 (MonoGame `WindowsDX12`), rung-4 proven (Phase 54).**
  `PlatformTarget.DirectX12` compiles to plain SM6 DXIL via DXC and is auto-selected for
  consumers targeting MonoGame's `WindowsDX12` runtime (3.8.5+) — seamless, no flag to pick.
  Render-proven maxd 0 against a real `mgfxc` `DirectX_12` golden, for both the 10-shader
  PS/SpriteBatch corpus and Apos.Shapes/VS-driven custom-vertex-shader effects
  (`validation/BaselineDx12`/`CandidateDx12`/`compare_dx12.py`, `validation/VsDrivenDx12`).
- **Apos.Shapes (Gum's SDF shape renderer) render-proof — DX and GL (Phase 51 A3), then
  expanded to the full shape gallery (Phase 55).** Vulkan already shipped in 0.13.0
  (`validation/VsDrivenVulkan -- apos`, maxd 0). This release closes the remaining single-shape
  DX/GL slices, then supersedes all of them with a 30-cell gallery driven through the REAL
  `Apos.Shapes` NuGet package's `ShapeBatch(GraphicsDevice, Effect?)` effect-injection
  constructor — every `Draw*`/`Fill*`/`Border*` shape kind, not one hand-built circle:
  - **DirectX 11 and DirectX 12.** Both pixel-diffed against a real, locally-generated `mgfxc`
    golden at **maxd 0** across all 30 cells for DX11 (`d3dcompiler_47` oracle and
    `vkd3d-shader`), and 28/30 at maxd 0 for DX12 (2 cells at 1/255, an open, unexplained
    finding — see `tests/fixtures/shaders/third-party/Apos.Shapes/NOTICE.md`). Along the way,
    found that Apos.Shapes' own embedded DX11 effect is compiled by `vkd3d-shader`, not
    `mgfxc` — comparing the `d3dcompiler_47` oracle against it was comparing two independent
    compilers, not a ShadowDusk fidelity gap; fixed by comparing against the real local
    `mgfxc` golden instead.
  - **Vulkan.** Full 30-cell gallery at **maxd 0** against Apos.Shapes' own (DXC-family)
    embedded golden.
  - **OpenGL.** No trustworthy `mgfxc` oracle exists for this shader revision on GL (a
    confirmed MojoShader codegen bug renders every non-textured shape solid black, unrelated
    to ShadowDusk) — the gallery renders through ShadowDusk's compile only, confirming all 30
    shapes produce visible output. The original single-shape GL proof (against an older,
    MojoShader-safe fixture revision) stays in place: **max Δ 2/255** (documented
    transcendental-math GLSL-dialect drift on the shader's OkLab round-trip).

  Wired into `run-windows-render-gates.ps1`. FNA stays permanently excluded (SM3
  instruction-slot ceiling).
- **Fixed: GL profile emitted `isnan()` into versionless GLSL; rejected on macOS (issue
  #149).** Found while closing the GL slice above: ShadowDusk's own GL candidate for
  `apos-shapes.fx` contained 28 `isnan(` occurrences and no `#version` directive (the real
  mgfxc golden has zero of either). Desktop NVIDIA/AMD/Intel drivers tolerated it; Apple's
  strict GL compiler did not, breaking any GL shader using `min`/`max`/`clamp` on macOS — real
  downstream breakage (Apos.Shapes 0.7.6). Fixed by defaulting SPIRV-Cross's
  `RELAX_NAN_CHECKS` compiler option on for the whole OpenGL profile: zero `isnan(` now, zero
  byte changes anywhere else in the corpus. See `plan/DONE/ISSUE-149-gl-isnan-versionless-glsl.md`.

### Changed

### Fixed

- **GL loop shapes outside GLSL ES 1.00 Appendix A: both shapes SD0402 covers are now
  auto-fixed where provably safe, not just warned about (issue #138).** GLSL ES 1.00
  (WebGL1 / KNI Reach) requires a loop's increment to live in the for-header and its bound
  to be a compile-time constant; SPIRV-Cross emits two shapes that violate this, and both
  used to fail to *load* there (desktop GL, WebGL2, and KNI HiDef were unaffected) while
  Phase 53 only added a compile-time warning (`SD0402`) for them.
  - **Constant-bounded, empty increment** (`for (int i = 0; i < N; ) { …; i++; continue;
    }`, the index advanced in the body). `MonoGameGlslRewriter` now hoists the increment
    into the header whenever it can prove the rewrite safe (no other write to the index, no
    other `continue` in the body). Confirmed end-to-end on the real vendored
    `Nez/GaussianBlur.fx`: compiling it through the CLI no longer emits `SD0402` at all.
  - **Header-less, runtime-looking trip count** (`for (;;) { if (i < bound) {…} else
    break; }`). Turns out "runtime" doesn't always mean unprovable: when `bound`'s own
    value is traceable to a compile-time-constant expression (a literal, or a ternary
    between two literals), the shader's real ceiling is still knowable even though
    SPIRV-Cross renamed it into a runtime-looking temporary. `MonoGameGlslRewriter` now
    gives the header that real, exact bound and hoists the increment the same way — not an
    approximation, since the derived bound IS the shader's true maximum. Confirmed on the
    real vendored `Apos.Shapes/apos-shapes.fx` (its Newton-iteration SDF): compiling it
    through the CLI no longer emits `SD0402` either, and the existing GL render-proof
    (`validation/VsDriven -- apos`) still matches the `mgfxc` golden at the same max Δ
    2/255 — pixels unchanged, as expected from an exact rewrite.

  A genuinely unfixable case remains: a loop bounded by a plain runtime uniform with no
  compile-time ceiling anywhere in the shader. There's no safe constant to derive there, so
  it keeps warning via `SD0402` — pinned by a fresh regression fixture,
  `examples/Sd0402UniformBoundedLoop.fx`, compiling clean while still warning through the
  real CLI.

## [0.13.0] - 2026-07-23

### Added

- **One-call shader validation: `Validate()` / `ValidateAsync()`.**
  `Console.WriteLine(await compiler.ValidateAsync(fx))` prints everything wrong with a
  shader: every error and every warning, per target, with source locations and the
  underlying compiler's complete verbatim text. Defaults to OpenGL + DirectX, putting the
  classic "compiles for DirectX, fails for OpenGL" report on one screen; an overload takes
  explicit targets (FNA, Vulkan). It runs the real compile pipeline per target, so what
  validates is exactly what compiles, and works on desktop and in the browser alike.
- **`CompiledShader.Warnings`** — successful compiles now carry their non-fatal diagnostics
  instead of discarding them: the underlying compiler's own warnings (DXC, d3dcompiler, and
  vkd3d's message buffer, previously thrown away on success) plus the new GL portability
  findings. The CLI prints them as MGCB-parseable `warning` lines on stderr with exit 0, and
  the ShaderFiddle sample lists them in its diagnostics panel. Warnings raised by an earlier
  technique also survive a later hard failure in the same effect instead of being dropped.
- **GL portability lint (`SD0400`–`SD0402`)**: compile-time warnings for constructs that
  compile fine but are known to fail or misbehave at *runtime* on narrower GL stacks, where
  the only previous signal was the engine's generic draw-time "Shader Compilation Failed"
  with the real driver log hidden in `Debug.WriteLine`.
  `SD0400` ([#141](https://github.com/kaltinril/ShadowDusk/issues/141)): a gradient op inside
  a divergent loop, silently 0.0 on ANGLE Direct3D11 (WebGL in every Windows browser).
  `SD0401`: a pass with no vertex shader whose pixel shader reads interpolants SpriteBatch's
  built-in vertex shader never writes, a strict-driver link failure at the first draw.
  `SD0402` ([#138](https://github.com/kaltinril/ShadowDusk/issues/138)): loop shapes outside
  GLSL ES 1.00 Appendix A that may fail to load on WebGL1 / KNI Reach.
  Warnings only; the lint never rejects a shader.
- **The published documentation now includes the [Diagnostic Codes](https://kaltinril.github.io/ShadowDusk/diagnostics.html)
  registry**, so any code seen in build output can be looked up.
- **A VS-driven Vulkan render gate with a real reference-compiler oracle**
  (`validation/VsDrivenVulkan`, issue #145): pixel-diffs ShadowDusk against the `mgfxc 3.8.5`
  golden on a real MonoGame DesktopVK device at **max Δ 0**, both for a non-identity
  asymmetric transform and for the upstream `Apos.Shapes` reproducer through its own
  13-element vertex layout. This is the first Vulkan comparison against the reference
  compiler; it works because explicit registers keep mgfxc's slot arithmetic in range. Both
  Vulkan gates are now default-ON in `run-windows-render-gates.ps1`.
- **A corpus-wide, device-free Vulkan structural gate** plus **MonoGame's own 17 test effects
  vendored into the corpus** (Ms-PL, tag `v3.8.5`), which carry the reference compiler's
  acceptance set. Every fixture must now either produce a structurally valid Vulkan container
  or fail with a real diagnostic, never an exception. They found two real defects on the day
  they landed.

### Changed

- **Compile errors are verbatim, everywhere, by default.** Diagnostic text that could not be
  parsed into file:line:col entries (DXC SPIR-V codegen failures, disproportionately the
  OpenGL leg) used to collapse into a fixed `X0000: "Shader compilation failed"`, with the
  real text hidden in a field no surface printed. The compiler's own words are now the
  message; the primary error prefers the first error-severity diagnostic, so a leading
  warning can no longer masquerade as the failure, and always carries the complete raw
  output, which the CLI and the ShaderFiddle sample print by default. There is no verbosity
  flag to find, deliberately.
- **The OpenGL/Vulkan leg no longer forces `-WX` (warnings-as-errors).** mgfxc's fxc front
  end never passed `/WX`, so ShadowDusk's GL leg was *stricter than the reference compiler*:
  warning-grade HLSL such as an implicit truncation compiled for DirectX but hard-failed for
  OpenGL, a confirmed "DX works, GL doesn't" divergence class. Those warnings now surface
  through `CompiledShader.Warnings` instead of failing the compile. Output bytes for
  previously-compiling shaders are unchanged.

### Fixed

- **Vulkan: matrices were packed row-major, so every VS-driven effect rendered nothing**
  (issue [#145](https://github.com/kaltinril/ShadowDusk/issues/145)). `-Zpr` was applied to
  every DXC compile, Vulkan included, but MonoGame uploads a `Matrix` parameter for HLSL's
  column-major default, so a Vulkan vertex shader read `mul(pos, worldViewProj)` transposed
  and threw its geometry out of clip space: loads fine, draws without error, renders nothing.
  mgfxc's Vulkan command line carries no `-Zpr`; now neither does ShadowDusk's. OpenGL keeps
  the flag (its rewriter compensates) and DirectX never used DXC.
- **Vulkan: legacy `tex2D` shaders access-violated inside `GraphicsDevice_DrawIndexed`**
  (issue #145). The `Texture2D` synthesized for a legacy `sampler` was excluded from the
  register-pairing rewrite, so the image auto-numbered to binding 0/1 while its sampler
  shifted to 32/33; MonoGame recovers the texture slot as `binding - 32` and indexed its
  texture array at -32. Pairs are now always co-located, explicit `register` indices are
  reserved so an auto-assigned pair cannot collide with them, and every texture
  dimensionality is covered (`TextureCube`/`Texture3D` had the same crash shape).
- **Vertex shaders now get the stage-agnostic GL body lowerings** (issue
  [#137](https://github.com/kaltinril/ShadowDusk/issues/137)). The rewriter returned early
  for the vertex stage, so a VS using `round()` shipped `roundEven()` (absent from GLSL ES
  1.00) and a VS with an inlined early-return helper shipped the raw `do { … } while(false)`
  Appendix A forbids, both silent Effect-load failures on Mesa/WebGL1 with compile exit 0.
- **Derivative-using fragment shaders ship `#extension GL_OES_standard_derivatives : enable`**
  as the first line of the emitted GL source, where mgfxc puts it (issue
  [#139](https://github.com/kaltinril/ShadowDusk/issues/139)). Strict ESSL 1.00 compilers
  reject derivative builtins without it. The scan covers `fwidth` too.
- **A `round()` nested inside another `round()`'s argument is now fully lowered** (issue
  [#140](https://github.com/kaltinril/ShadowDusk/issues/140)); the inner call previously
  survived as `roundEven()`, the exact load failure the lowering exists to prevent.
- **Vulkan: a texture/sampler pair could still be handed a binding another texture already
  held.** Explicit `register` indices were only honoured by the auto-assign path, so a pair
  that *inherited* its index from an explicitly-registered half could collide with a
  different pair — two textures on one binding, the invalid descriptor layout that
  access-violates in MonoGame's descriptor writer. Pair co-location now outranks a
  disagreeing explicit register (the runtime binds by slot index, not by the source's
  register number), and explicitly-registered textures are assigned first so the guard cannot
  be defeated by declaration order. Two textures still share an index when they name the same
  sampler, which is the same-sampler-in-two-`#if`-branches shape where only one branch
  survives the compile. No shader in the test corpus changes output: every fixture is
  byte-identical through the rewrite on all four targets.
- **Vulkan: `Gather*` calls, `Texture2DArray`/`TextureCubeArray`/`Texture2DMS` declarations,
  and `SamplerComparisonState` are now seen by the pairing pass.** Being invisible to the
  scan meant those pairs were left at separate bindings and their explicit registers were
  never reserved. `Texture2DArray` and `SamplerComparisonState` appear in MonoGame's own
  vendored test effects (both already carry agreeing registers, so no corpus output changes);
  the rest are covered defensively.
- **A `while` loop following any block no longer loses its `SD0402` warning.** The do-while
  tail check accepted *any* `while` preceded by `}`, so `if (…) { … } while (…) { … }` was
  misread as a do-while's trailing clause and the finding silently dropped.
- **The compiler's diagnostic text no longer prints twice.** The "the message already says
  this" check compared the raw blob and the message without normalizing line endings or
  blank lines, so it never matched on multi-line diagnostics — exactly the unparseable-text
  path the verbatim work was built for. Multi-line messages are also indented under their
  parseable first line now, so compiler-controlled text can no longer start a stderr line and
  be misread by a build-log parser as a separate diagnostic.
- **`Validate` reports name the file for line-less findings**, matching the CLI.
- **Warnings without a line number now name their source file.** The GL portability warnings
  are derived from emitted GLSL and carry no line mapping, so the CLI printed a bare
  `warning SD0401: …` with no way to tell which effect produced it in a build compiling many.
- **Anonymous techniques (`technique { pass { … } }`) are accepted** — legal FX that mgfxc
  compiles, and what 8 of MonoGame's 17 own test effects use, previously rejected with
  `FX0001: Expected technique name`. Relatedly, the FNA writer no longer rejects an empty
  technique name, which `d3dcompiler_47` at `fx_2_0` compiles cleanly.
- **A native process crash on the FNA path is now a diagnostic.** `SamplerComparisonState`
  made vkd3d's SM1 lowering hit "Unreachable code reached" and took the whole process down
  with an access violation; it is rejected up front with the new `FX0013` instead.
- **Vulkan container faithfulness:** vertex shaders now carry the attribute table mgfxc emits
  (recovered from the SPIR-V input semantics), a combined image-sampler sets both the texture
  and sampler slot masks, and the shipped SPIR-V no longer carries `-fspv-reflect`'s
  `SPV_GOOGLE_*` extensions.

## [0.12.1] - 2026-07-21

### Added

- Vendored the derivative-based-antialiasing revision of Apos.Shapes' shader
  (`apos-shapes-aa.fx`, upstream `d507a73`) into the third-party corpus (GL + DX compile
  pins), plus a structural pin that fails if any gradient op ever again lands inside a
  loop with a divergent exit in emitted GL GLSL (issue #136).
- `validation/AngleDerivativeProbe`: a self-asserting headless-browser gate that renders
  the emitted fragment control-flow shapes on real ANGLE Direct3D11 (the WebGL backend of
  every Windows browser) and fails if ShadowDusk's shape loses derivatives — the first
  gate that sees the browser backend the issue-#136 bug lives on. Wired into
  `run-windows-render-gates.ps1` as default-ON, alongside the real-KNI OpenGL desktop and
  VS-driven drivers (previously manual-only, now impossible to forget for GL-affecting
  changes).

### Changed

### Fixed

- **`dFdx`/`dFdy` no longer return 0 in Windows browsers (ANGLE D3D11)** — issue #136,
  reported by Jean-David Moisan (Apostolique). ANGLE's D3D11 backend silently zeroes every
  gradient op inside a loop with a divergent exit (a conditional `break` or `discard`), and
  the issue-#107 for-loop lowering of SPIRV-Cross's entry-point `do { … } while(false);`
  wrapper put the whole fragment body inside exactly such a loop, disabling derivative-based
  antialiasing (Apos.Shapes SDF shapes) on KNI BlazorGL with no compile or link error. The
  GLSL rewriter now **unwraps** the wrapper when it can prove it safe: plain brace block,
  each loop-level `break` → duplicated tail + `return;` — straight-line `main` with real
  early exits, valid in every GLSL profile including ESSL 1.00, and the same shape
  mgfxc/MojoShader emits. The unwrap recurses through the plain blocks it creates, so an
  **inlined helper that both early-returns and takes a derivative** (its wrapper nests
  inside the entry wrapper) unwraps too — the shape the fix's adversarial review found
  still poisoned. A tail whose duplication would move a gradient op or implicit-LOD
  sample into divergent flow (undefined per GLSL §8.13.1) is never unwrapped; those and
  all other unprovable shapes keep the WebGL1-safe for-loop fallback. Desktop GL/KNI
  output remains render-equivalent (Windows render gates + KNI desktop GL/VS-driven
  drivers + Linux CI GL gates).

## [0.12.0] - 2026-07-18

The headline: **a Vulkan backend**. MonoGame 3.8.5 (stable 2026-07-15) ships `DesktopVK`, a
native Vulkan desktop platform — and ShadowDusk now compiles for it: the same faithful
pipeline, one DXC compile straight to SPIR-V, wrapped in the real MonoGame Vulkan `.mgfx`
container. Additive and seamless like every backend before it: existing OpenGL / DirectX /
FNA / WASM output is byte-identical, and the MGFX v10 default is unchanged.

### Added

- **Vulkan output target** (`PlatformTarget.Vulkan` / CLI `/Profile:Vulkan`) — Phase 32,
  contributed via PR #126 (Victor Chelaru / vchelaru). HLSL compiles through the pinned DXC
  frontend directly to SPIR-V (`vs_6_0`/`ps_6_0`) and is emitted in MonoGame 3.8.5's own
  Vulkan `.mgfx` container (profile byte 80, v11-shaped shader records, the SPIR-V wrapped in
  the descriptor-layout header MonoGame's native Vulkan pipeline reads), with reflection from
  the SPIR-V itself. Texture+sampler pairs bind as single combined image-sampler descriptors,
  matching MonoGame's runtime. **Render-proven on real hardware:** the shader corpus loads and
  renders correctly (10/10) in real MonoGame 3.8.5 `DesktopVK`
  (`validation/CandidateVulkan`; local gate `./validation/run-windows-render-gates.ps1
  -IncludeVulkan`). A pixel-diff against `mgfxc`'s own Vulkan output is blocked upstream —
  that output currently crashes in real DesktopVK due to a confirmed MonoGame `SlotOffset`
  bug. MonoGame-only: KNI ships no Vulkan platform (a KNI+Vulkan request fails loudly with
  `SD0025`).

### Changed

- Documentation: **DirectX 12 (MonoGame 3.8.5 `WindowsDX12`) is now recorded as an explicit
  not-yet-supported target** across the support matrix and site pages (planned,
  research-first — see `plan/PHASE-52-monogame-3.8.5-support.md`). The validation matrix's
  accumulated update trail moved to a compact bottom-of-page history, consumer-facing pages
  were trimmed of internal status noise, and the `ShadowDusk.Compiler` / `ShadowDusk.Cli`
  package READMEs now list the Vulkan target.

### Fixed

- **OpenGL codegen fidelity: `pow(x, 2.0)` strength-reduced to a multiply** (issue
  [#127](https://github.com/kaltinril/ShadowDusk/issues/127)). GLSL leaves `pow` undefined for a
  negative base (drivers lowering it to `exp2(y*log2(x))` return NaN), while fxc constant-folds
  `pow(x, 2)` into a multiply — so HLSL that squares a possibly-negative value via `pow` (e.g.
  Apos.Shapes' `LinearGradient` squaring normalized-direction components) was well-defined through
  mgfxc but a latent driver-dependent hazard through ShadowDusk's GL output. `MonoGameGlslRewriter`
  Rule 10 now emits the multiply (exact, and the reference compiler's semantics); simple-operand
  bases only, so no expression is ever duplicated unsafely.
- **OpenGL codegen fidelity: `1.0 / (a / b)` folded to `b / a`** (issue
  [#127](https://github.com/kaltinril/ShadowDusk/issues/127)). SPIRV-Cross preserves the HLSL
  reciprocal-of-quotient shape literally (fxc folds it), costing an extra rounding step at every
  such site (all 8 `SmoothDiscontinuity` call sites in apos-shapes.fx). Rule 11 emits the single
  correctly-rounded division; value-equivalent across the zero/infinity edge cases, applied only
  when the division is provably the group's root operator. Both rules are pinned by rewriter unit
  tests and an end-to-end regression test compiling the vendored `apos-shapes.fx` on GL
  (`AposShapes_OpenGl_EmitsNoPowSquare_NoReciprocalOfQuotient_Issue127`); the full suite, the
  Windows DX render gates, and every GL render gate (corpus vs mgfxc, VS-driven at 1/255, KNI
  desktop GL, state/cbuffer/texture/reserved-word) stayed green.

## [0.11.0] - 2026-06-28

Android joins the supported runtime-compile platforms: ShadowDusk now compiles `.fx` -> `.mgfx`
**in memory, at runtime, on an Android device** (the "shader fiddle on a phone" shape), through the
same faithful HLSL -> DXC -> SPIR-V -> SPIRV-Cross -> GLSL -> MGFX pipeline used everywhere else, via
the seamless `new EffectCompiler()`. This is additive and seamless: all existing OpenGL / DirectX /
FNA / WASM output is byte-identical, the MGFX v10 default is unchanged, and desktop/WASM consumers are
unaffected (the Android natives are RID-scoped, so they are never deployed into a non-Android app).

### Added

- **On-device runtime shader compilation on Android (arm64-v8a)** (Phase 50). A .NET-for-Android
  MonoGame app can take a user's shader **text**, compile it to a MonoGame `.mgfx` on the device, and
  load it into a live `Effect`, with no host precompile and no content pipeline. The two native pieces
  the OpenGL pipeline needs (`libdxcompiler.so` and `libspirv-cross.so`, built for `android-arm64`)
  now ship inside `ShadowDusk.HLSL` and `ShadowDusk.GLSL` under `runtimes/android-arm64/native/`, so
  "add the package, call the API" is the entire setup, exactly as on desktop. Proven on-device by
  compiling and rendering a pixel shader on an Android emulator.
- A consumer guide, **"On-Device (Android Runtime Compile)"**, in the published documentation site,
  with the integration recipe (`new EffectCompiler().CompileAsync(fx, new CompilerOptions { Target =
  PlatformTarget.OpenGL })` -> `result.Value.Data` -> `new Effect(GraphicsDevice, mgfx)`) and notes for
  embedding the compiler in an Android shader fiddle.

### Changed

- `EffectCompiler` **auto-selects the pure-managed `SpirvReflector` on Android** (the native
  DXIL-oracle reflection path is unavailable there). The selection is automatic and produces the same
  reflection result, so consumers do nothing and desktop behavior is unchanged.

### Fixed

## [0.10.0] - 2026-06-28

Fidelity fixes driven by real shipping shaders from the Gum / Apos.Shapes ecosystem (requested by
vchelaru, Gum's author): several real-world effects that previously failed now compile and render on
the targets they ship for, across FNA, OpenGL, and KNI WebGL. Every change is additive: the seamless
MGFX v10 default and all existing OpenGL / DirectX / FNA output stay byte-identical (pinned by the
cross-host byte-identity gate), and the new behavior only enables previously-failing shaders.

### Added

- Real, MIT-licensed **Apos.Shapes and Gum** `.fx` shaders vendored under
  `tests/fixtures/shaders/third-party/` as **compile-level** regression inputs (Phase 49), each
  classified by an actual GL / DX / FNA compile probe. These guard the compiler against the exact
  shaders the Gum / Apos.Shapes ecosystem ships; provenance and per-shader target classification are
  in `docs/test-shader-corpus.md`.

### Changed

- **`__KNIFX__` is now defined for a KNIFX-targeted compile** (`--target-runtime kni-knifx` /
  `CapabilityProfile.KniGL_4_02`), matching KNI's own effect compiler, so a shader that branches on
  `#ifdef __KNIFX__` (e.g. Apos.Shapes selecting its SM4 profile) takes the correct branch. The
  seamless universal MGFX default deliberately does **not** define it, so default output is unchanged.

### Fixed

- **FNA: macro-defined techniques are now recovered** (Phase 41 GAP-1). An effect whose techniques
  come only from a `TECHNIQUE(...)` `#define` (the stock-MonoGame / Gum idiom) previously failed
  `SD0010` on FNA because techniques were counted before macro expansion. The zero-technique recovery
  (preprocess then re-parse) now extends to the FNA path, so **7 MonoGame stock effects compile on
  FNA** (SpriteEffect, AlphaTestEffect, DualTextureEffect, and the Penumbra hull/light/shadow/texture
  effects). Effects that still fail now do so for honest shader-model reasons (register pressure /
  sub-SM2 profiles), not technique-blindness. Only effects that returned zero bytes before are
  affected, so existing FNA output is byte-identical.
- **OpenGL: multi-render-target pixel shaders now compile** (Phase 41 GAP-2). A deferred-rendering
  effect whose pixel shader returns a struct with `COLOR0` / `COLOR1` output semantics (e.g. Nez
  `DeferredSprite.fx`) failed on the GL target with `Semantic COLOR is invalid`. A GL-only struct
  output `COLOR` to `SV_Target` rewrite (applied only to the OpenGL compile, so DirectX bytes stay
  identical) fixes it, and true multi-target slot 0 now emits `gl_FragData[0]` instead of
  `gl_FragColor` so writing one target no longer corrupts the others.
- **KNI WebGL: a one-shot `do { ... } while(false)` loop no longer breaks loading**
  ([#107](https://github.com/kaltinril/ShadowDusk/issues/107)). A helper with a nested `if` that early
  returns made SPIRV-Cross emit a `do/while(false)`, which compiles and loads on desktop GL but is not
  guaranteed by GLSL ES 1.00, so the effect **failed to load in KNI WebGL / Reach**. The GL rewriter
  now lowers it to an equivalent WebGL-safe bounded `for` loop (pixels unchanged); render-proven in
  real KNI WebGL on both Reach (WebGL1) and HiDef (WebGL2). DirectX / FNA bytecode is unchanged.
- **KNIFX now loads and renders on KNI WebGL and mobile GLES** (the opt-in KNIFX container). A
  KNIFX-targeted compile previously advertised only the desktop OpenGL backend, so KNI WebGL rejected
  it ("profile is not compatible with the graphics backend 'WebGL'"). The KNIFX writer now emits a
  multi-backend GL-family directory (desktop OpenGL keeps its faithful body byte-for-byte; GLES and
  WebGL share a body KNI's runtime converts to GL ES at load, the same proven path that already loads
  MGFX v10 in KNI WebGL). One `.knifx` now loads on every KNI GL host; desktop KNI render is unchanged.

## [0.9.0] - 2026-06-22

Robustness pass on the **ShaderToy GLSL -> `.fx` converter** (`ShadowDusk.ShaderToy`): several real
ShadowToy shaders that previously failed to convert now compile, and the cases that genuinely cannot be
faithfully translated reject with a clear, located message instead of an opaque downstream parser error.
The converter is a pure-managed front end that emits `.fx`; the core compile pipeline and all existing
`.mgfx` / `.fxb` output are unchanged (zero golden churn).

### Added

- **`ShadowDusk.ShaderToy` is now a published NuGet package** (the seventh `ShadowDusk.*` package).
  It is the standalone, **pure-managed, zero-native** ShaderToy/GLSL → `.fx` converter
  (`ShaderToyConverter.Convert`), so anyone can convert ShaderToy shaders **in-process** (e.g. an
  XNA/KNI web shader fiddle or an in-app importer) without the CLI. It is **optional and separate**:
  `ShadowDusk.Compiler` does not depend on it, so existing consumers are unaffected. (The converter
  also continues to ship embedded in the `ShadowDuskCLI` tool's `.glsl` input.)
- Regression fixtures and unit tests for every case below: authored corpus shaders
  (`matrix_from_vector`, `mirror_happy_accident`, `infinite_cube_starfield`, `abstract_waterfall`,
  `chimera_final_pass`) auto-converted and golden-compared, plus reject fixtures
  (`unsigned_int_literal`, `texture_cubemap_coord`) and targeted unit suites
  (`MatrixConstructorTests`, `ForLoopScopingTests`, `ConstGlobalTests`, `TextureLodTests`).

### Changed

- **Clearer, located rejects for constructs outside the float-based subset.** An unsigned-integer
  literal (`123U`, which drives uint/uvec bit-hash arithmetic) now rejects **at the literal** instead of
  the stray `U` surfacing later as a confusing "expected `)`" parse error, and `texture(sampler, vec3)`
  (a cubemap sample) rejects with a message naming the cubemap rather than truncating silently.

### Fixed

- **`mat2` constructed from a `vec4` now converts** (e.g. `mat2(someVec4)`): the four-component vector is
  flattened into the `float2x2` instead of failing to emit.
- **Reused `for`-loop induction variables now convert** under HLSL's legacy for-scope rule. GLSL scopes a
  `for`-init declaration to its loop; legacy HLSL leaks it to the enclosing scope, so a second
  `for (int i = ...)` in the same function tripped `-Wfor-redefinition` under `-WX`. The converter now
  scope-renames reused induction variables per loop (first occurrence keeps its name), so multi-loop
  raymarchers convert.
- **Multi-declarator `const` globals now parse** (e.g. `const float A = 1., B = 2.;`). The additional
  declarators were previously swallowed by the comma operator, surfacing as a misleading "Undeclared
  identifier" error on later use.
- **A base-level `textureLod(s, uv, 0.)` lowers to a plain `tex2D`.** The legacy `tex2Dlod` intrinsic
  does not rewrite to a modern Texture method on the OpenGL/DirectX targets (`FX0012`); since the
  single-pass harness binds each iChannelN without mipmaps, mip 0 is the only level, so the two are
  equivalent and the shader now compiles on every backend. A non-zero LOD keeps the explicit `tex2Dlod`.

## [0.8.0] - 2026-06-18

### Added
- `SECURITY.md` at the repo root: the project's trust model (compiling a `.fx` runs code; the
  shader author and the compiler-runner are the same developer, so the library is a build-time/in-app
  tool, not a sandbox), the consumer's isolation responsibility for any service that compiles
  third-party `.fx`, the supply-chain integrity model for the natives we ship, and how to report a
  vulnerability.
- KNI **DirectX** render validation (`validation/KniWinFormsDX`): ShadowDusk's DX output renders
  pixel-equivalent to the `mgfxc` DirectX golden in real KNI v4.02 WinForms.DX11.
- `validation/run-windows-render-gates.ps1`: one command that runs the Windows-GPU render proofs CI
  cannot (DirectX corpus, vertex-texture-fetch, KNI-DX, and FNA under `-IncludeFna`), required before a
  release.
- CI render gates for the in-process OpenGL validation drivers
  (`.github/workflows/validation-render.yml`, Mesa llvmpipe under xvfb).
- 15 real, MIT-licensed **Nez** `.fx` shaders vendored under
  `tests/fixtures/shaders/third-party/Nez/` as **compile-level** regression inputs (issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106) / Phase 45), plus author-original
  regression fixtures for the pre-parser fixes below. These guard the FX9 pre-parser against
  real-world shaders; they are not new render-equivalence proofs (provenance + per-shader target
  classification in `docs/test-shader-corpus.md`).
- `validation/ReservedWordGl`: a GL render driver that render-proves the reserved-word uniform
  binding fix below pixel-identical to `mgfxc`.
- `tests/fixtures/shaders/examples/Issue106Repro.fx`: the verbatim shader from the issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106) report (a helper using `==`, `<=`, a
  nested `if`, and an early `return`), pinned as a permanent regression fixture and compile-asserted
  on OpenGL, DirectX, and FNA.

### Changed
- The in-process MGFX/KNIFX/FNA golden-comparison and cross-host byte-identity tests now run on the
  fast PR lane (previously only on the heavier integration lane), so a writer/transpiler/render-state
  regression that still compiles is caught on every pull request, not just at release time.
- The KNI WebGL render proof was refreshed on the current KNI v4.02 runtime, and the browser harness now
  stamps the KNI runtime version into every generated results file.
- Documentation accuracy pass: corrected Vulkan's status (it compiles to a SPIR-V `.mgfx` but has no
  shipping runtime to render-validate against, i.e. experimental/unvalidated, not "future"), the
  fixture-corpus count, and assorted status/cross-reference drift surfaced by a full project review.

### Fixed
- **FX pre-parser: a whole class of valid shaders that previously failed now compiles** (issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106) / Phase 45). The global-parameter
  annotation heuristic matched the bare token shape `Identifier Identifier <` anywhere in the stream,
  so a **relational, shift, or ternary expression** in a shader body (e.g.
  `return value <= 0.5f ? 0 : 1;`) was misread as an FX annotation and failed with `FX0001`. The path
  is now gated on the genuine annotation-block shape, and several related pre-parser bugs are fixed in
  the same pass: modern `sampler_state` + `.Sample`, `ColorWriteEnable = Red | Green | Blue` masks,
  legacy `texture < ... >` annotations, a texture variable named `Texture`, a vertex shader returning
  `: COLOR`, array-indexed relational/ternary assignment, and sampler register/annotation variants
  (B1-B9). Purely additive: it only enables previously-failing shaders, so existing output stays
  byte-identical (pinned by the cross-host byte-identity gate).
- **OpenGL: a uniform whose name collides with a GLSL reserved word now binds correctly** (issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106), B10). `float noise;` is valid HLSL that
  `mgfxc`/`fxc` accept, but SPIRV-Cross renames the colliding uniform (`noise` to `_noise`) for legal
  GLSL, so the GL cbuffer/parameter join — matching by name — missed it and failed loudly with
  `SD0012`. The join now falls back to an offset bridge that recovers the parameter by byte offset
  (keeping its original name), render-proven pixel-identical to `mgfxc` in real MonoGame GL. The
  primary name match is unchanged and runs first, so every shader that compiles today is byte-for-byte
  identical; this only enables the reserved-word case (re-enabling the real Nez `Noise.fx` on GL).
- Closed soft-skip-as-green holes in the validation drivers/tests: `validation/TextureBreadthValidation`
  now honors `SHADOWDUSK_REQUIRE_GL` (a missing GL device fails loudly instead of reporting success),
  and the `mgfxc` cross-validation test fails (rather than silently passing) when a known-good fixture
  stops compiling.

## [0.7.0] - 2026-06-14

The seamless default is unchanged: MGFX **v10** is still the default container and output is
**byte-identical to 0.6.0** for every existing call (`CrossHostByteIdentity` stays green). This
release adds an opt-in **capability-profile** API for naming a full output target (graphics backend
+ container/version) in one value, a matching CLI flag, and runtime detection. It also validates the
KNIFX optimized-matrix (`columnsActual`) fidelity against KNI's own compiler (KNIFXC).

### Added

- **Capability-profile output-target selector.** New `CapabilityProfile` (a **closed set** of
  render-proven (runtime, format) contracts: `MonoGameGL_3_8_2`, `MonoGameDX_SM5`, `MonoGameGL_3_8_5`
  for MGFX v11, `KniGL_4_02` for KNIFX v11, and `Fna_Fx2`) plus `CompilerOptions.Profile`. A profile
  fully specifies the output target, **including the graphics backend**, so setting `Profile` alone
  picks both format and backend; it overrides `Target` / `Container` / `MgfxVersion`. The default
  (`null`) reproduces today's behavior exactly.
- **CLI `--target-runtime <name>`** (also `/target-runtime:<name>`): selects the output target by
  name (`monogame-gl`, `monogame-dx`, `monogame-gl-v11`, `kni-knifx`, `fna`), mapping to a
  `CapabilityProfile`. Overrides `/Profile` and `--mgfx-version`. Unknown values fail with `X0008`.
- **Runtime detection.** `RuntimeProfileDetector` classifies the loaded XNA framework assembly
  (MonoGame / KNI / FNA) and recommends a proven `CapabilityProfile` to pass to
  `CompilerOptions.Profile`. Conservative: it returns the universally-loadable MGFX v10 (fx_2_0 for
  FNA) and never silently upgrades a consumer to a newer container.
- **Shader-feature capability axis.** `ShaderFeatures` + `ShaderFeatureSupport`, which **rejects
  (`SD0201`)** any GL feature no shipping runtime consumes yet, so an unsupported feature can never
  silently compile into bytes no runtime can load. (No shipping runtime supports these today.)

### Changed

- A `CapabilityProfile` **implies its graphics backend**, so a set `CompilerOptions.Profile`
  determines the backend (overriding `Target`); the runtime-detection advisory composes with this so
  one recommended profile picks both format and backend.
- **KNIFX `columnsActual` validated against a KNIFXC golden.** Full matrices match KNI's own compiler
  exactly; the partially-used-matrix case (ShadowDusk emits `columnsActual = columns`) is a
  render-safe, storage-only divergence, not a correctness difference. KNIFX output is unchanged.

## [0.6.0] - 2026-06-14

The seamless default is unchanged: MGFX **v10** is still the default container and you never
set a flag to get correct output. This release adds two **opt-in / experimental** container
writers for newer runtimes (MonoGame MGFX v11 and KNI KNIFX v11), recovers macro-declared
techniques on DirectX, and fixes two OpenGL vertex-shader fidelity bugs.

### Added

- **Faithful MGFX v11 writer (opt-in, experimental).** `CompilerOptions.MgfxVersion = 11`
  (CLI `--mgfx-version 11`) now emits a **correct** MonoGame v11 container — where it was
  previously **corrupt** (a v10 body labeled version 11, which a real v11 reader cannot
  parse). MonoGame 3.8.5's `Effect` loader expects a per-shader `SourceFile` and `Entrypoint`
  string in the shader stream (PR #8813); ShadowDusk now writes them. They are diagnostic-only
  (they appear in shader error messages) and do not affect rendering. **Render-proven in real
  MonoGame 3.8.5**: the corpus loads + renders 10/10, max delta 0 vs the v10 render. **v10
  remains the default and never reads them** — `MgfxVersion` is a non-required escape hatch
  (default 10).
- **KNIFX v11 container target (opt-in, experimental).** New public `EffectContainer` enum
  (`Mgfx` default, `Knifx`) and `CompilerOptions.Container` property (default `Mgfx`). Set
  `Container = EffectContainer.Knifx` to emit KNI's newer KNIFX v11 container for KNI v4.02+
  consumers — signature `KNIF`, a multi-backend directory, a packed-int body, and the GL
  GLSL-version directory KNI's runtime requires. **Render-proven in real KNI v4.2.9001 desktop
  GL**: the corpus loads + renders 10/10, max delta 0 vs the v10 render. Additive, not a
  replacement for the v10 default; `MgfxVersion` is ignored when `Container == Knifx`, and
  `Container` is ignored for `PlatformTarget.Fna` (always D3D9 fx_2_0). `CompiledShaderBlob`
  gained three init-only properties with mgfxc's own safe fallbacks (`ShaderModel = (3,0)`,
  `SourceFile`/`Entrypoint = "<unknown>"`).

### Changed

- **DirectX: macro-declared techniques are now recovered.** Stock-MonoGame-style effects that
  declare their technique via the `TECHNIQUE(...)` macro (e.g. `BasicEffect.fx`) now compile
  on DirectX/Vulkan through a gated zero-technique fallback (DXC-preprocess then re-parse).
  OpenGL and FNA explicitly decline this path; existing behavior is otherwise unchanged.
- **vkd3d: include-heavy effects compile without noise** — `#line` directives are blanked so
  they no longer surface as diagnostics.
- **`ShadowDusk.HLSL` package: removed dead public types** as dead-code cleanup
  (`RenderStateMapper`, `MappedRenderState`, the empty `FxFileParser` stub,
  `ReflectionInput.SpirVBlob`, `ReflectionPipeline.ReflectAsync`; `IDxcShaderCompiler` gained a
  `Preprocess` method). Behavior-neutral — no emitted bytes change. The product surface
  (`IShaderCompiler` in `ShadowDusk.Compiler`) is unaffected; this only matters if you
  referenced `ShadowDusk.HLSL` internals directly.

### Fixed

- **OpenGL vertex-shader geometry fidelity ([#70](https://github.com/kaltinril/ShadowDusk/issues/70)).**
  Two silent GL bugs in custom-vertex-shader effects are corrected, moving the default v10 GL
  output toward `mgfxc`-equivalence: a `float4x4` uniform was reconstructed **transposed** (so
  a non-identity `mul(v, M)` rendered an exploded/garbled mesh), and legacy `: POSITION` vertex
  outputs were not mapped to `gl_Position` (silently broken geometry). Both are now
  render-proven **max delta 0** against the `mgfxc` golden in real MonoGame. This intentionally
  changes the v10 GL bytes for VS-driven effects (12 OpenGL byte-identity fixtures updated;
  **zero** DirectX/FNA fixtures changed) — previously broken output is now correct.

> MGFX v11 and KNIFX v11 are opt-in and experimental; the seamless default remains MGFX v10,
> which loads on every MonoGame 3.8.2+ and KNI runtime with no consumer action. FNA (fx_2_0
> `.fxb`) output is byte-identical to the previous release.

## [0.5.1] - 2026-06-12

### Added

- **Every package now ships a README** on its nuget.org page (previously only
  `ShadowDusk.Wasm` had one — the other five showed nuget.org's "missing a README"
  banner).

### Fixed

- **macOS: native-library resolution now keys on the process architecture, not the OS
  architecture.** Under Rosetta 2 (an x64 build running on an Apple-silicon Mac) the
  resolvers for all three natives (DXC, vkd3d-shader, SPIRV-Cross) probed the arm64
  binaries — which can never load into an x64 process — instead of the x64 ones sitting
  beside them, so compiles failed with `X0099`. Caught by the release pipeline's new
  smoke-run gate during the 0.5.0 publish: the run stopped before creating the GitHub
  Release, so the broken self-contained osx-x64 CLI binary never shipped. The 0.5.0
  NuGet packages remain fine for typical consumers (NuGet's own native layout sidesteps
  the buggy probe); 0.5.1 makes the self-contained osx-x64 CLI work on Apple-silicon
  Macs and completes the GitHub Release that 0.5.0 never got.

## [0.5.0] - 2026-06-12

### Added

- **`InitializeAsync()` + synchronous `Compile()`** on the compiler surface
  (`IShaderCompiler` / `EffectCompiler` / `WasmShaderCompiler`) — issue
  [#28](https://github.com/kaltinril/ShadowDusk/issues/28): compile `.fx` from a
  **synchronous** call site (e.g. MonoGame/KNI `Content.Load<Effect>`) after a one-time
  async warm-up, with no sync-over-async deadlock on single-threaded Blazor WASM.
  `await compiler.InitializeAsync()` once (on WASM it loads all the compiler WASM
  modules; on desktop it is a documented no-op), then `compiler.Compile(source, options)`
  runs the entire pipeline on the calling thread. Sync and async share **one** pipeline
  core, so their output is byte-identical (asserted over the full fixture corpus for
  OpenGL, DirectX, and FNA, on desktop and in a real browser). Calling the synchronous
  `Compile` on WASM before `InitializeAsync` returns a clear `SD1903` error telling you
  to initialize first — never an opaque runtime abort. `CompileAsync` is unchanged for
  existing consumers. The backend interfaces (`IDxcShaderCompiler`,
  `IDxbcShaderCompiler`) and reflection pipelines gained matching synchronous entries.
- **In-browser DirectX and FNA compilation.** `WasmShaderCompiler` now compiles
  `PlatformTarget.DirectX` (SM5 DXBC `.mgfx`) and `PlatformTarget.Fna` (D3D9 `.fxb`) in
  the browser, so every shipping target (OpenGL, DirectX, FNA) works on every host. The
  browser runs the **same pinned vkd3d-shader 1.17** the desktop packages bundle,
  compiled to WebAssembly (0.43 MB gzipped) — never a substitute compiler — and the
  emitted bytes are identical to desktop output, asserted over the full DirectX + FNA
  fixture corpus both in Node and in a real headless browser against the committed
  cross-host manifest.

### Changed

- **DirectX compiles default to the cross-platform vkd3d-shader backend on every OS.**
  A bare DirectX compile (including the CLI's default `DirectX_11` profile with no
  backend flag) previously defaulted to the Windows-only `d3dcompiler_47` and
  hard-failed `SD0210` on Linux and macOS. The default is now host-independent — the
  same vkd3d backend everywhere, so default DX output is byte-identical across OSes.
  `d3dcompiler_47` remains fully supported as the opt-in correctness oracle (CLI escape
  hatch `/DxbcBackend:<vkd3d|d3dcompiler>`), and vkd3d's stderr debug chatter is
  suppressed so the CLI keeps `mgfxc`'s silent-success contract.
- **Vertex-stage texture sampling on the GL target now fails at compile time with a
  clear diagnostic** instead of emitting GLSL that MonoGame's GL runtime cannot bind
  (it was silently broken at runtime in two independent ways).
- Sample: `ShaderFiddle.Web` gained an export station — compile once in the browser and
  download the compiled artifact for each target (OpenGL/DirectX `.mgfx`, FNA `.fxb`).

### Fixed

- **GL: effects with a custom vertex shader rendered upside-down when drawing to the
  backbuffer** (the normal game case — only render-target rendering was correct), and
  `UseHalfPixelOffset` was ignored. ShadowDusk baked a static Y-flip into the vertex
  shader where MonoGame expects `mgfxc`'s dynamic `posFixup` uniform (the runtime flips
  the sign for backbuffer vs render target and applies the half-pixel offset).
  ShadowDusk now emits the exact `posFixup` contract, validated pixel-identical
  (max delta 0) to `mgfxc` in real MonoGame 3.8.2 in **both** backbuffer and
  render-target modes.
- **MGFX: pass render states, annotations, and `sampler_state` filter/address states
  are now written in MonoGame 3.8.2's exact wire format.** A pass carrying render
  states (e.g. `AlphaBlendEnable = TRUE;`) or annotations could desync or fail the real
  `Effect` reader, and sampler filter/address modes were silently dropped on MGFX
  targets. All three are now byte-faithful to the real reader, golden-validated and
  render-validated in real MonoGame.
- **GL: effects with multiple cbuffers, a cbuffer shared by VS and PS, or uniform
  arrays now get a correct uniform/parameter model.** Same-stage cbuffers merge into
  one register space, per-stage records bind correctly (a buffer shared by VS and PS is
  no longer deduped into an unbindable record), and array parameters carry per-element
  records so `Effect.Parameters` behaves as with `mgfxc`. Shapes the GL model does not
  yet cover (int/bool/mat3/struct uniform members) now fail loudly at compile time
  (`SD0210`/`SD0012`) instead of emitting wrong GLSL.
- **GL on Mesa (Linux): explicit-LOD/gradient sampling** (`SampleLevel`, `SampleGrad`,
  projective forms) failed on strict drivers because the rewriter emitted generic
  `textureLod`/`textureGrad` in versionless GLSL. These now lower to the legacy builtin
  names under MojoShader's guarded `GL_ARB_shader_texture_lod` header, matching
  `mgfxc`.
- **A first-use race in all three native-library loaders** (DXC, vkd3d-shader,
  SPIRV-Cross): a concurrent first compile could P/Invoke before the import resolver
  was registered, surfacing as an intermittent `DllNotFoundException` under test
  parallelism. Also revived the SPIRV-Cross resolver, which matched the wrong library
  name and never fired (the library had loaded only via default probing).
- Preprocessor/lexer robustness on real-world `.fx`: `#include` diamonds (the same
  header reachable via two paths) no longer error; directives inside comments are
  ignored; the HLSL lexer no longer silently swallows minus signs or unknown
  characters. SPIR-V reflection now populates struct `Members` (parity with the DXIL
  oracle), and colliding `SDxxxx` diagnostic codes were renumbered behind a registry
  test.
- WASM: the DXC module load retries after a transient fetch failure, and the vkd3d
  shim is hardened (allocation null-checks, bounded string reads, clean retry after a
  failed init) — a flaky first fetch no longer wedges the in-browser compiler.

### Verified

- **The CLI and the in-process library emit byte-identical output** — proven over the
  fixture corpus by a parameterized suite that runs every fixture through both
  invocation modes (the CLI is a delivery shape of the library, now machine-checked).
- **The pre-1.0 verification sweep closed every deferred verify item from the
  foundation phases** with 32+ new tests (negative diagnostics coverage, golden
  parameter-table matches against the `mgfxc` goldens, include-resolver and GLSL
  Y-flip checks), plus scripted pack / global-install / self-contained-publish
  verification of the CLI.

## [0.4.0] - 2026-06-11

### Added

- **macOS shader compilation works.** The upstream `Vortice.Dxc` package ships no macOS
  DXC native, so every OpenGL/WebGL compile on a Mac threw `DllNotFoundException`.
  ShadowDusk now bundles its **own `libdxcompiler.dylib`** for osx-x64 and osx-arm64,
  built from the exact DXC commit the bundled Windows/Linux natives report
  (1.7.2212.40 / `e043f4a1` — same compiler, never a substitute), SHA-256-pinned and
  loaded automatically. The full integration suite is green on macOS in CI.

### Changed

- **DirectX 11 (`.mgfx`) compiles now run end-to-end on Linux and macOS** (Phase 18
  Track A). DXBC reflection no longer P/Invokes Windows-only `D3DReflect`
  (d3dcompiler_47): it is a pure-managed reader of the DXBC container's `RDEF`/`ISGN`/
  `OSGN` chunks (`RdefReader`), proven deeply equal to `D3DReflect`'s output for both
  the d3dcompiler_47 and vkd3d backends, with **zero change to emitted `.mgfx` bytes**
  (full-corpus A/B, DirectX + OpenGL). With the vkd3d backend (which already shipped
  for all four desktop RIDs), no Windows-only native remains on the DX11 path.
- The DXC compiler is now constructed lazily inside the pipeline: DirectX 11 compiles
  never load the DXC native (FNA already did not), so they work on hosts where it is
  unavailable (e.g. macOS, pending the Phase 37 A DXC dylib). OpenGL/Vulkan behavior is
  unchanged.

### Fixed

- **Linux shader compilation no longer fails with `Internal Compiler error`.** Every DXC
  compile on Linux failed (`error X0000`): Vortice.Dxc's managed wrapper marshals DXC's
  `LPCWSTR*` arguments as UTF-16 on every OS, but DXC's non-Windows builds use the
  platform's 4-byte `wchar_t` (UTF-32), so the native compiler read garbage arguments.
  ShadowDusk now invokes `IDxcCompiler3::Compile` with platform-correct argument encoding
  (and an explicit UTF-8 source buffer). The native compiler binary is unchanged; Windows
  output is byte-identical. The same fix is what makes the new macOS dylib work.
- The in-browser render-validation harness (the WebGL-vs-DesktopGL pixel compare behind
  the KNI/WebGL support claims) now runs in CI on every change, on a software-GL baseline
  with documented per-shader tolerances — 10/10 corpus shaders load and render
  equivalently in real KNI WebGL1, and the issue #7 HiDef/WebGL2 guard runs with it.

### Verified

- **Cross-host determinism is machine-verified.** CI now asserts a committed SHA-256
  manifest of compiled output (102 fixture×target entries: OpenGL, DirectX via vkd3d, FNA)
  independently on Windows, Linux, and macOS — the emitted bytes are identical on every
  OS, so the Windows render-validation results apply byte-for-byte everywhere.
- **The consumer experience is machine-verified.** A CI job on all three OSes packs the
  packages, installs `ShadowDusk.Compiler` into a scratch project from a local feed, and
  compiles real shaders through it (including the bundled-natives check that a 0.2.0-style
  empty package can never ship again). `THIRD-PARTY-NOTICES.txt` now also covers the
  bundled DXC dylibs (LLVM Release License).

## [0.3.0] - 2026-06-10

### Added

- **FNA support: the new `PlatformTarget.Fna` output target.** Compiles D3D9-style `.fx`
  to the legacy D3D9 Effects binary (`.fxb`) FNA loads — no `fxc.exe`, no Wine, on every
  desktop OS. Render-validated in real FNA 26.06: the validation corpus (PS-only,
  VS-driven, multi-pass, in-pass render states) draws pixel-equivalent (max Δ ≤ 1/255) to
  `fxc /T fx_2_0` output. Purely additive — existing OpenGL/DirectX output is unchanged.
- **vkd3d-shader natives for all four desktop RIDs now ship inside `ShadowDusk.HLSL`**
  (win-x64, linux-x64, osx-x64, osx-arm64; pinned vkd3d 1.17, SHA-256-verified at
  restore). The FNA target and the opt-in `DxbcBackend.Vkd3d` DirectX backend are
  self-contained from the package — add the package, compile, no manual install.
- `THIRD-PARTY-NOTICES.txt` (vkd3d-shader attribution + LGPL-2.1 text) ships in the
  `ShadowDusk.HLSL` package.
- Docs: new "Choosing a target" guide (OpenGL vs DirectX vs FNA, and why output is raw
  `.mgfx`/`.fxb` rather than `.xnb`).

### Changed

- The release pipeline now refuses to publish if the packed `ShadowDusk.HLSL` package is
  missing any of the four vkd3d natives or the license notice — a stopped release beats
  shipping the FNA target broken.

### Fixed

- **FNA: brace-form sampler blocks (`sampler s = sampler_state { Texture = (tex); … };`)
  now bind their texture correctly** — previously the binding was silently lost and the
  effect rendered wrong with no diagnostic.
- **FNA: all render states FNA honors are now emitted into the `.fxb`** (11 previously
  missing states), and states FNA would throw on are rejected loudly at compile time
  (`SD0303`) instead of failing at runtime.
- FNA: matrix parameters now carry the same parameter class `fxc` emits (column-major
  fidelity, pinned by a new golden); shader-model/stage mismatches are caught at compile
  time; SM1 profiles are rejected with a clear error.

## [0.2.0] - 2026-06-07

### Added

- **`ShaderError` is now the diagnostics contract on every host.** A failed
  `IShaderCompiler.CompileAsync` returns `ShaderError[]` with `File`, `Line`, `Column`,
  and the compiler's `Message` verbatim — usable as a `.fx` validator (ignore the bytes,
  read the errors). This already worked on desktop; the **in-browser (WASM) path now carries
  the same line/column** (see Fixed), so a KNI/Blazor tool can highlight the offending line
  with no API change.

### Changed

- **Sample `ShaderFiddle.Web` highlights compile errors.** Bad shader lines get a wavy
  underline, the line-number gutter shows the message on hover, and each diagnostic is
  clickable to jump to its line — a demonstration of the line/column diagnostics above.

### Fixed

- **In-browser (WASM) compile errors now report the source line and column.** Previously a
  failed in-browser compile surfaced a single opaque error (`[object WebAssembly.Exception]`)
  with no location, while desktop reported file/line/column. DXC captured the diagnostics, but
  the WASM module *threw* them and `-fwasm-exceptions` made the text unreadable in JS. The
  faithful DXC→WASM module now **returns** its diagnostics, so the in-browser path runs them
  through the same reformatter as desktop and yields `ShaderError`s with real `Line`/`Column`.
  Compiled output is byte-identical to before (success-path SPIR-V unchanged, 10/10).

## [0.1.1] - 2026-06-07

Maintenance release: the CLI is rebranded to `ShadowDuskCLI`, plus release-pipeline and CI
reliability fixes. The product libraries (`Core` / `HLSL` / `GLSL` / `Compiler` / `Wasm`)
are functionally identical to 0.1.0 — they remain platform-agnostic .NET packages that work
for Linux, macOS, and Windows consumers from a single install.

### Changed

- **CLI renamed `mgfxc` → `ShadowDuskCLI`.** The `dotnet tool` command and the self-contained
  binary now ship under ShadowDusk's own brand rather than the name of the tool they replace.
  The NuGet package id is unchanged (`ShadowDusk.Cli`). To use it as a drop-in for MonoGame's
  content pipeline, point MGCB's `ExternalTool` at `ShadowDuskCLI` (or alias it to `mgfxc`).

### Fixed

- **Release now produces the per-RID self-contained CLI binaries.** The single-file publish
  names the apphost after the assembly, so the GitHub Release verify/archive steps now target
  `ShadowDuskCLI`; 0.1.0's `Publish CLI` jobs failed looking for a `mgfxc` binary.
- **macOS CI no longer hangs.** The ImageTests GL fixture initialized GLFW on macOS, leaving a
  non-background Cocoa thread that kept the test host from exiting after a green run. The GL
  render proxy is now correctly treated as N/A on macOS (Apple deprecated OpenGL; the proxy is
  covered on Linux + Windows), so macOS is back in the release gate and completes in seconds.
- **Quieter, tighter CI.** Doc-only pushes skip the build matrix, the WASM/browser workflow is
  on-demand, and CI job timeouts were tightened from 25–30 min to 10–12 min.

## [0.1.0] - 2026-06-07

First public release. A single faithful HLSL → `.mgfx` pipeline
(HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL → managed reflect + MojoShader-dialect rewrite +
MGFX writer, or vkd3d-shader → DXBC for DirectX), delivered as a library, a CLI tool, and a
WASM-capable build — the same pipeline on every host, with no substitute compilers.

### Added

- **Cross-platform in-memory `.fx` → `.mgfx` compile.** `ShadowDusk.Compiler`
  (`EffectCompiler : IShaderCompiler`) compiles HLSL `.fx` shaders to MonoGame `.mgfx`
  bytes in-process on Linux, macOS, and Windows — no `fxc.exe`, no `mgfxc`, no Wine, no
  Windows SDK. `IShaderCompiler.CompileAsync(fx)` returns `.mgfx` bytes; no temp files or
  child process required by the API.
- **OpenGL / DesktopGL backend.** HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL with a managed
  MojoShader-dialect rewriter and MGFX writer. SPIRV-Cross rides inside the package via the
  `Silk.NET.SPIRV.Cross.Native` transitive dependency, and DXC via `Vortice.Dxc` — so
  `dotnet add package ShadowDusk.Compiler` and call the API is the entire setup for the GL
  path on a clean machine.
- **DirectX DXBC backend.** Compiles HLSL → SM5 DXBC in-process (no `fxc.exe`/`mgfxc`)
  behind the `IDxbcShaderCompiler` seam, with two backends chosen by
  `CompilerOptions.DxbcBackend`: the **default** `d3dcompiler_47` (Microsoft's HLSL
  compiler — a system DLL already present on Windows; most `fxc`-faithful) and the **opt-in,
  cross-platform** `vkd3d-shader` (`DxbcBackend.Vkd3d`) for compiling DX shaders on
  Linux/macOS where `mgfxc` cannot run. Both render pixel-equivalent to `mgfxc` (Phase 18).
  DXC is not used for DX11 (it emits DXIL/SM6, not DXBC/SM ≤ 5); its `ps_6_0`/`vs_6_0` output
  is retained for the DX12/KNI path. *(Cross-platform `vkd3d` is not yet packaged in the
  NuGet — see Known limitations.)*
- **`mgfxc`-compatible CLI tool.** `ShadowDusk.Cli` ships as a `dotnet tool` named `mgfxc`
  (`dotnet tool install -g ShadowDusk.Cli`) — same CLI flags, same `.mgfx` output format,
  same exit codes, and MGCB-parseable stderr diagnostics, so existing MonoGame content
  pipelines switch with zero code changes (via `ExternalTool` config or PATH override).
- **WASM / in-browser compile engine.** `ShadowDusk.Wasm` (`WasmShaderCompiler`) targets
  `net8.0-browser` and runs the same faithful pipeline in the browser via `[JSImport]`
  bindings to WASM-compiled DXC and SPIRV-Cross — emitting `.mgfx` bytes identical to the
  CLI/desktop path. A pure-managed `SpirvReflector` reflects SPIR-V without a DXIL oracle.
  The in-browser shader-fiddle (`samples/ShaderFiddle.Web`) is a sample of this reach.
- **KNI HiDef / WebGL2 (GLSL ES 3.00) output.** A single `.mgfx` loads and renders in both
  KNI Reach (WebGL1 / GLSL ES 1.00) and KNI HiDef (WebGL2 / GLSL ES 3.00) — the rewriter
  emits `mgfxc`'s `#define ps_oC0 gl_FragColor` form that KNI's runtime converts to a typed
  `out vec4`, with zero consumer input and no new flag or format.
- **GL texture breadth.** Cube maps work on every GL target; 3D textures and explicit
  LOD / gradient sampling work on Desktop and HiDef (WebGL1 cannot, by platform limit). The
  MGFX sampler `Type` byte now carries the reflected texture dimension (2D / Cube / 3D), and
  the rewriter emits per-dimension sampling builtins.
- **VS-driven effects (custom vertex shaders).** Effects that ship their own vertex shader
  (a `float4x4` transform with `POSITION` / `COLOR0` / `TEXCOORD0` attributes) compile
  faithfully on the GL path — the `MonoGameGlslRewriter` emits the symmetric
  `vs_uniforms_vec4` block, the legacy `attribute`/`varying` stage I/O, and the full
  matrix-uniform expansion — not just pixel-shader-only post-process effects.
- **Forward-compatibility with newer MonoGame.** ShadowDusk's default MGFX **v10** output
  loads and renders correctly in MonoGame **3.8.4.1** (the latest stable 3.8.x) as well as
  the pinned **3.8.2.1105** baseline — pixel-identical on the same bytes, within tolerance
  of the `mgfxc` goldens — so a consumer's existing `.mgfx` keeps working forward with no
  action required. A forward-compat regression guard backs this.
- **Self-contained single-file CLI.** `dotnet publish -r <rid> --self-contained` produces a
  working `mgfxc` binary that bundles the native dependencies it needs.

### Validated

- **OpenGL fidelity in the real MonoGame runtime (Phase 17).** All 10/10 shaders of the SM3
  PS-only corpus load in a real MonoGame DesktopGL `Effect` and render pixel-equivalent to
  `mgfxc` — the strongest rung of the evidence ladder: in-engine behavioral equivalence.
- **DirectX fidelity in the real MonoGame runtime (Phase 18).** All 10/10 DX `.mgfx` of the
  SM5 PS-only corpus load in real MonoGame WindowsDX and render pixel-equivalent to `mgfxc`,
  via both the `d3dcompiler_47` oracle and the cross-platform vkd3d-shader backend.
- **VS-driven fidelity in the real MonoGame runtime (Phase 28).** A VS-driven `.fx` (custom
  vertex shader + `float4x4` transform) compiled by ShadowDusk loads in real MonoGame
  DesktopGL **and** WindowsDX and renders pixel-identical (max delta 0) to its `mgfxc`
  golden, on both the `d3dcompiler_47` oracle and the cross-platform vkd3d backend for DX.
- **In-browser render proof (Phases 22–24).** Corpus `.mgfx` load and render in real
  headless KNI WebGL (Reach and HiDef/WebGL2), and the faithful in-browser DXC → WASM path
  emits `.mgfx` byte-identical to the CLI for the corpus.
- **Deterministic, byte-identical output across hosts.** Same ShadowDusk version + same
  source + same target produces the same `.mgfx` bytes on desktop, CLI, and WASM.

### Known limitations

- **DirectX from a pure NuGet add is not yet fully self-contained.** The GL + DXC in-memory
  path ships self-contained from NuGet today; the DirectX vkd3d-shader native is a restored,
  non-redistributed artifact not yet packaged as a `runtimes/<rid>/native/` asset. The
  0.1.0 line advertises GL-from-NuGet as the self-contained path.
- **VS-driven effects** are covered for the SpriteBatch-compatible attribute set
  (`POSITION` / `COLOR0` / `TEXCOORD0`); additional vertex semantics (`NORMAL` / `TANGENT` /
  skinning) and Metal/Vulkan vertex-shader paths are follow-ons.
- **Metal (MSL) and Vulkan backends** are not yet implemented (stubs only).
- **The MGCB content-processor plugin** is a scaffold; the PATH-based `mgfxc` override is the
  shipping MGCB integration path.

[Unreleased]: https://github.com/kaltinril/ShadowDusk/compare/v0.14.1...HEAD
[0.14.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.14.0...v0.14.1
[0.14.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.13.0...v0.14.0
[0.13.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.12.1...v0.13.0
[0.12.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.12.0...v0.12.1
[0.12.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.11.0...v0.12.0
[0.11.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.10.0...v0.11.0
[0.10.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.9.0...v0.10.0
[0.9.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.8.0...v0.9.0
[0.8.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/kaltinril/ShadowDusk/releases/tag/v0.1.0
