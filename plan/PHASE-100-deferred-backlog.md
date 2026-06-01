# Phase 100 — Deferred Backlog (far-future)

**Status:** Deferred / Backlog. Numbered 100 to park it well beyond the active roadmap.
**Blocking anything?** No. Everything here was explicitly deferred from earlier phases and is not a prerequisite for current work. Review before a 1.0 release.

This document is the single deferred bucket. It collects every unchecked item from completed (`DONE/`) phase plans and items deferred from phases 6+ (organized by source phase), **plus** the Phase 19 *WASM browser-runtime* tail (the part of Phase 19 that needs a browser/emscripten toolchain unavailable in-session) — see the **From Phase 19** section near the end. *(Formerly two docs: "Phase 20 — Deferred Backlog" and a standalone "Phase 100 — WASM Browser-Runtime Validation"; merged into this one Phase 100 on 2026-05-30.)*

---

## From Phase 2 — FX9 Pre-Parser

- [ ] Stripped HLSL output compiles without syntax errors when passed to DXC.
  *(originally deferred: "verified by integration test in Phase 3." Phase 4 DXC integration likely covers this — run `dotnet test --filter Category=Integration` and check it off if passing.)*

---

## From Phase 3 — Preprocessor / Macro Injection

**Tests — not yet written:**

- [ ] `4.4` FileSystemIncludeResolver integration test: resolve a real `.fxh` file from disk.
  *(deferred to Phase 10 integration suite — add to `ShadowDusk.Integration.Tests/Preprocessor/`)*
- [ ] `7.4` DxcIncludeHandler smoke test: construct handler with `InMemoryIncludeResolver`,
  verify `LoadSource` returns the correct blob bytes.
  *(deferred: "requires live DXC COM init; add in Phase 4 DXC integration tests" — add to `ShadowDusk.Integration.Tests/Dxc/DxcShaderCompilerIntegrationTests.cs`)*

**Wiring validation — verified against `CompilationPipeline.cs` (Phase 8):**

- [x] `8.2` `Preprocessor.Flatten()` is called after Phase 2 and before DXC invocation. *(Stage 2 in CompilationPipeline)*
- [x] `8.3` Platform macros are injected into the preprocessed HLSL text via `PlatformMacros.ToTextPrepend`; baked into `PreprocessedSource.Text` rather than passed as separate DXC flags — functionally equivalent.
- [x] `8.4` Preprocessor flattens all `#include` directives before DXC sees the source; no `DxcIncludeHandler` is needed at the DXC call site. *(Comment in CompilationPipeline.cs confirms this.)*

---

## From Phase 4 — DXC Integration

**Unit tests (file exists — verify coverage):**

Files: `tests/ShadowDusk.HLSL.Tests/Dxc/DxcFlagBuilderTests.cs`
       `tests/ShadowDusk.HLSL.Tests/Dxc/DxcDiagnosticReformatterTests.cs`

`DxcFlagBuilderTests` checklist:
- [ ] OpenGL vertex stage: `-spirv`, `-fvk-use-dx-position-w`, `-fvk-use-dx-layout`, `vs_5_0`; no `-fvk-invert-y`
- [ ] OpenGL pixel stage: `-spirv`, `-fvk-use-dx-layout`, `-auto-binding-space 1`, `ps_5_0`; no `-fvk-invert-y`
- [ ] Vulkan vertex stage: all OpenGL vertex flags plus `-fspv-reflect`, `vs_6_0`
- [ ] Vulkan pixel stage: all OpenGL pixel flags plus `-fspv-reflect`, `ps_6_0`
- [ ] DirectX_11 vertex stage: `vs_6_0`; no `-spirv`
- [ ] DirectX_11 pixel stage: `ps_6_0`; no `-spirv`
- [ ] Macros appended as `-D Name=Value` (keyed) and `-D Name` (flag)
- [ ] Entry point appears as `-E <entryPoint>` before profile argument
- [ ] `-Zpr` always present
- [ ] `-WX` present by default; absent when `AllowWarnings = true`

`DxcDiagnosticReformatterTests` checklist:
- [ ] Well-formed Clang diagnostic line → correct file/line/col/message
- [ ] FXC-formatted output: `Filename.fx(line,col-col): error X0000: message`
- [ ] Warning severity mapped to `ShaderError.Severity.Warning`
- [ ] Unknown/non-matching lines preserved as raw
- [ ] Empty input returns empty list
- [ ] Multi-line error block with note lines handled correctly

**Integration tests (file exists — verify coverage):**

File: `tests/ShadowDusk.Integration.Tests/Dxc/DxcShaderCompilerIntegrationTests.cs`

- [ ] Minimal vertex shader → non-empty SPIR-V (OpenGL)
- [ ] Minimal pixel shader → non-empty SPIR-V (OpenGL)
- [ ] Minimal vertex shader → non-empty DXIL (DirectX)
- [ ] Syntax error → `Result.Failure` with `FxcFormattedMessage` containing `(line,col`
- [ ] Undefined variable in pixel shader → failure with line/col in FXC format
- [ ] Vulkan target vertex shader → non-empty SPIR-V
- [ ] Compile with `-D` macro succeeds and macro is visible to DXC
- [ ] Cancellation before invocation → `OperationCanceledException` (not `ShaderError`)

---

## From Phase 5 — Shader Reflection

**SPIRV-Cross binding slot verifier:**

- [ ] `7.3.2` Use SPIRV-Cross P/Invoke (`spvc_context_create` → `spvc_compiler_create_shader_resources`).
  Enumerate `separate_images` and `separate_samplers`.
- [ ] `7.3.3` Compare DXIL and SPIR-V slots. Emit `ShaderError` with `"SD0101"` on mismatch.
  *(Pick these up when Phase 8 wires the full pipeline — SPIRV-Cross P/Invoke will be in place.)*

**Golden snapshot acceptance test — implement in Phase 10:**

- [ ] `9.3.1` `MgfxParameterMatchTests.cs` — compile a MonoGame reference shader, run `ReflectionPipeline`,
  compare output against a golden JSON snapshot from MonoGame's own `mgfxc`.
- [ ] `9.3.2` Snapshot comparison must be exact (name, class, type, rows, columns, elements).

---

## From Phase 6 — SPIRV-Cross GLSL Transpilation

### 11-6-A: Wire `SpirvCrossGlslTranspiler` into `CompilationPipeline`

**✅ Resolved by Phase 8.** `CompilationPipeline` calls `SpirvCrossGlslTranspiler.Transpile()` for each VS/PS SPIR-V blob (Stage 5), forwards the GLSL text bytes into `CompiledShaderBlob`, and propagates transpilation errors as compilation failures. All 67 integration tests pass.

### 11-6-B: `Transpile_PassthroughVertex_YFlipIsApplied` integration test

**Blocked by:** Requires the SPIRV-Cross native library to be present.

When native library is available:
1. Compile `tests/fixtures/shaders/passthrough_vs.fx` through `DxcShaderCompiler` targeting OpenGL.
2. Pass vertex SPIR-V to `SpirvCrossGlslTranspiler.Transpile()`.
3. Assert that `gl_Position.y` appears negated in the output (check for `-gl_Position.y` or equivalent sign-flip expression emitted by SPIRV-Cross for the `FlipVertexY` option).

### 11-6-C: Run Phase 6 integration tests and platform check

**Blocked by:** SPIRV-Cross native library not yet downloaded on the development machine.

Steps:
1. Run `.\tools\restore.ps1` (Windows) or `./tools/restore.sh` (Linux/macOS) to populate `tools/spirv-cross/`.
2. Rebuild so MSBuild copies the native library to the output directory.
3. Run `dotnet test --filter "Category=Integration&Platform=OpenGL"` — all 6 `GlslTranspilerTests` should pass.
4. Run `/platform-check` to confirm no new platform-specific assumptions were introduced in Phase 6.

### 11-6-D: Uniform remapping for MonoGame OpenGL runtime compatibility — ✅ RESOLVED (Phase 17, 2026-05-30)

**Context:** Researched in Phase 6, resolved in Phase 17 (see `docs/glsl-uniform-naming.md`).

MonoGame's OpenGL runtime expects uniforms in MojoShader convention (`vs_uniforms_vec4[N]` / `ps_uniforms_vec4[N]` float4 arrays), not the HLSL variable names that SPIRV-Cross produces by default.

**Resolution:** Strategy 1 (post-process GLSL) was implemented as `MonoGameGlslRewriter` (`src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs`), gated to the PS-only OpenGL path via the `monoGameGl` flag in `CompilationPipeline`. The SPIRV-Cross `type_Globals` UBO is rewritten to a flat `uniform vec4 ps_uniforms_vec4[N];` array (+ samplers→`ps_s{slot}`, varyings, `gl_FragColor`, `texture2D`, drop `#version`), and the pipeline names the cbuffer `ps_uniforms_vec4` with one register per free param in SM 3.0 register layout. Verified in-engine: all 10 SM3 shaders load in real MonoGame DesktopGL and match the `mgfxc` goldens with parameters set by name. See `docs/glsl-uniform-naming.md` for the full rule table, verification, and known limitations (VS stage + PS matrix free-uniforms remain future work, Phase 17 §8.3).

Strategies 2 (patch MonoGame runtime) and 3 (UBO binding points) were rejected — both break drop-in compatibility with stock `mgfxc`-compiled `.mgfx`.

### 17-VS: VS-driven MonoGame effects (OpenGL)

**Context:** Carried forward from Phase 17 §8.3 (which proved in-engine equivalence for the **PS-only** SM3 corpus). The MonoGame GL path is gated by the `monoGameGl` flag in `CompilationPipeline` to **PS-only** effects; vertex-bearing passes keep the unmodified SPIRV-Cross dialect so their VS↔PS varying contract and the Phase-16 anchor tests don't regress.

To support custom effects with their own vertex shader under the MonoGame GL runtime:
1. **Symmetric uniform remap for the VS** — `MonoGameGlslRewriter` currently passes `ShaderStage.Vertex` through unchanged; it needs the `vs_uniforms_vec4[N]` equivalent of the PS rewrite, and the pipeline must name/emit the `vs_uniforms_vec4` cbuffer.
2. **VS-side stage I/O contract** — emit the legacy `attribute`/`varying` declarations MonoGame's GL runtime links against (the VS produces the varyings the PS consumes by name), and the GL vertex-attribute table for `POSITION`/`COLOR0`/`TEXCOORD0`.
3. **PS matrix free-uniforms** — complete the `mat4` member expansion in `MonoGameGlslRewriter` (today emits `ps_uniforms_vec4[i]/*TODO mat*/`); a VS almost always takes a `float4x4` transform, so this is a prerequisite.
4. Extend the `validation/` harness to a VS-driven shader and confirm in-engine equivalence vs an `mgfxc` golden.

See `docs/glsl-uniform-naming.md` → *Known limitations*.

---

## From Phase 8 — Compiler Library

**NuGet packaging verification — ✅ RESOLVED 2026-05-31 (branch `selfcontained-inmemory-nuget`).** Running these surfaced a real drop-in bug and fixed it:

- [x] `7.4` `dotnet pack` produces `ShadowDusk.Compiler` 0.1.0. **Bug found:** the package declared deps on `ShadowDusk.Core/HLSL/GLSL` **1.0.0 (unpublished)** and bundled **no native DXC/SPIRV-Cross**, so `dotnet add package ShadowDusk.Compiler` would fail to restore and then throw `DllNotFoundException`. **Fix:** made Core/HLSL/GLSL packable + versioned 0.1.0 so the ShadowDusk.* set resolves, and the native-bundling NuGets (`Vortice.Dxc`, `Silk.NET.SPIRV.Cross.Native`) now flow transitively. (The OpenGL DXIL-reflection default is unchanged — it's cross-platform via dxcompiler, and `new EffectCompiler()` is the DXIL baseline of `SpirvReflectionByteIdentityTests`.)
- [x] `7.5` Verified with a scratch console app **outside the repo**, restoring only from the package feed: `EffectCompiler.CompileAsync` on `Grayscale.fx` produced a valid 519-byte `.mgfx` (`MGFX` header) **in memory**. (Linux/macOS run validation → Phase 30 CI; the native deps are cross-platform via Vortice.Dxc + Silk.NET.)

**ShaderIRBuilder direct unit tests (require `InternalsVisibleTo`):**

- [ ] Add `[assembly: InternalsVisibleTo("ShadowDusk.Compiler.Tests")]` to `ShadowDusk.Compiler.csproj`.
- [ ] `Build_ShaderIndicesAreZeroBased` — 2-pass technique; assert Pass 0: VertexShaderIndex=0, PixelShaderIndex=1; Pass 1: VertexShaderIndex=2, PixelShaderIndex=3.
- [ ] `Build_EmptyAnnotationsAllowed` — pass with no annotations; assert `AnnotationInfo` list is empty, no exception thrown.

---

## From Phase 9 — CLI Entry Point

**Manual verification steps not yet run:**

- [ ] `9.4` Run `dotnet pack src/ShadowDusk.Cli` — confirm NuGet package is produced with `ToolCommandName` set to `mgfxc`.
- [ ] `9.5` Install locally: `dotnet tool install -g ShadowDusk.Cli --add-source ./nupkg`, then run `mgfxc` with no args — confirm usage on stderr with exit 1.
- [ ] `9.6` Run `dotnet publish src/ShadowDusk.Cli -r win-x64 --self-contained` — confirm single-file binary executes and bundles native DLLs.

**Deferred to integration phase (PHASE-15):**

- [ ] Real `.fx` fixture library beyond `Minimal.fx` — add representative shaders covering textures, constant buffers, multiple techniques.
- [ ] Per-platform integration test filter: tag tests with `[Trait("Platform","OpenGL")]` so `dotnet test --filter "Category=Integration&Platform=OpenGL"` works.

**Deferred platform work:**

- [ ] Metal/MSL pipeline stage in `PipelineRunner` — currently returns `X0010`; implement once Metal support (post-Phase 6) is complete.
- [ ] Full MGCB content processor plugin (`ShadowDusk.MgcbPlugin`) — separate undertaking post-Phase 8.

---

## From Phase 15 — Integration Tests

**CLI-process invocation mode — infrastructure exists, no tests wire it up:**

The Phase 15 plan §3.1 specified two invocation modes: `DirectPipeline` (in-process) and `CliProcess` (out-of-process via the published `mgfxc` binary). All 103 current tests run via `DirectPipeline`. The CLI-process path is fully implemented in `TestHelpers.CompileViaCliAsync`, plus `CliFixture` and `CliBinaryFixture`, but unused.

- [ ] Add a `[Theory]` variant of `CompileFixtureTests.Compile_ProducesValidMgfxHeader` parameterised over both `InvocationMode` values so every fixture × platform also exercises the published CLI binary.
- [ ] Wire `CliBinaryFixture` as a class fixture so the CLI is published once per test class instead of per test.
- [ ] Confirm exit codes, stderr formatting, and `.mgfx` output match between the two invocation paths (drop-in equivalence guarantee).
- [ ] Decide whether `CliFixture` (skip-on-missing) and `CliBinaryFixture` (publish-on-demand) should coexist or be unified — only one of them needs to remain.

**Cross-platform validation:**

- [ ] Tests run unmodified on Linux and macOS — explicitly deferred to Phase 30 (CI). Acceptance criterion from Phase 15 §9: *"Tests run without modification on Windows, Linux, and macOS (validated in Phase 10 CI)"* — Phase 10 was renumbered to Phase 30.

---

## From Phase 19 — WASM Browser-Runtime Validation (emscripten modules + real in-browser run)

*Carved out of [Phase 19](DONE/PHASE-19-wasm-runtime-compilation.md) on 2026-05-30: the managed compile **engine** is done & desktop-verified; this is the **runtime** tail, gated on an external toolchain (emscripten) + an actual browser the dev environment can't exercise.*

**Depends on:** Phase 19 (injectable backends, the pure-managed `SpirvReflector`, the DXIL-free GL reflection path, and the browser-compiling `WasmShaderCompiler` with its `[JSImport]` contract in `src/ShadowDusk.Wasm/Phase19.js`), plus [Phase 25](PHASE-25-security-hardening.md) (untrusted web input) and [Phase 30](PHASE-30-cross-platform-ci.md) (headless-browser CI).
**Blocks:** the *runtime* half of the Part-1 (reach) promise for the browser — a shader actually compiling + rendering client-side, no server.

Phase 19 is byte-transparent on desktop (the `SpirvReflector` reflection-source swap yields `.mgfx` byte-identical to the DXIL path, 10/10). The only unproven variable is whether the *in-browser* DXC/SPIRV-Cross **binary versions** emit the same SPIR-V/GLSL as the desktop natives — which is what this work pins down.

**Native WASM modules**
- [ ] Build (or source) **DXC** compiled to WebAssembly (emscripten); export JS `compileToSpirv(hlslSource: string, args: string[]): Uint8Array` matching the `shadowdusk-dxc` contract in `Phase19.js`. Pin to the desktop DXC version.
- [ ] Build (or source) **SPIRV-Cross** compiled to WebAssembly; export `transpileToGlsl(spirv, flipVertexY, fixupDepthConvention, glslVersion, glslEs, vulkanSemantics): string` matching the `shadowdusk-spirv-cross` contract. Pin to the desktop SPIRV-Cross version.
- [ ] Measure download size, memory, cold-start; decide whether Mode 2 ships by default or stays opt-in.

**Mode 1 — precompiled bytes load in WebGL**
- [ ] Minimal MonoGame/KNI **WebGL** harness that calls `new Effect(gd, bytes)` on a Phase-9-compiled OpenGL `.mgfx` and renders a known quad / the corpus.
- [ ] Confirm the Phase-17 DesktopGL `.mgfx` also loads + renders in WebGL; **document any DesktopGL-vs-WebGL divergence** (research doc §15.2).

**Mode 2 — in-browser compilation (end-to-end)**
- [ ] With the modules wired, compile ≥1 corpus shader **fully in-browser** (source → `.mgfx` → `Effect`) with no shader-compile/link errors in the console.
- [ ] Assert the in-browser `.mgfx` bytes are **identical** to the CLI output for the same source + OpenGL target (closes the "modulo binary version" caveat Phase 19 left open).

**Validation / CI**
- [ ] Headless-browser smoke test in [Phase 30 CI](PHASE-30-cross-platform-ci.md) for Mode 1; gate Mode 2 behind a flag if heavy.
- [ ] Run untrusted `.fx` through [Phase 25](PHASE-25-security-hardening.md) input validation (browser path takes arbitrary user shader text).

**Definition of done (this section):** a shader compiled **entirely in-browser** by `ShadowDusk.Wasm` renders correctly in a real MonoGame/KNI **WebGL** build via `new Effect(gd, bytes)`, no server, bytes matching the CLI. The polished user-facing demo on top is [Phase 22](PHASE-22-wasm-shader-fiddle-sample.md).

---

## How to resolve items here

1. **Verify and check off:** For items marked *(file exists — verify coverage)*, run the existing test file, confirm the assertion exists, then check it off in both this document and the originating `DONE/PHASE-X` file.
2. **Implement in the right phase:** Items tagged *(Phase 10)*, etc. belong in those phases — pick them up when starting that phase.
3. **Write missing tests:** Items with no file yet go into the appropriate test project; write them as part of a test sweep before release.
