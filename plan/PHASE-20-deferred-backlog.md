# Phase 11 — Deferred Backlog

**Status:** Backlog  
**Blocking anything?** No. All items here were explicitly deferred from earlier phases and are not prerequisites for Phases 8–10. Review before a 1.0 release.

This document collects every unchecked item from completed (`DONE/`) phase plans and items deferred from phases 6+, organized by source phase.

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

### 11-6-D: Uniform remapping for MonoGame OpenGL runtime compatibility

**Context:** Researched in Phase 6 (see `docs/glsl-uniform-naming.md`).

MonoGame's OpenGL runtime expects uniforms in MojoShader convention (`vs_uniforms_vec4[N]` / `ps_uniforms_vec4[N]` float4 arrays), not the HLSL variable names that SPIRV-Cross produces by default.

Three strategies (evaluate during Phase 8 / Phase 10):
1. **Post-process GLSL** — parse SPIRV-Cross output, remap individual uniform declarations to `vs_uniforms_vec4[N]` array slots matching SM 3.0 register layout. Most compatible; requires non-trivial GLSL post-processing.
2. **Patch MonoGame runtime** — emit GLSL with HLSL names; ship a modified MonoGame OpenGL runtime that looks up by name. Breaks drop-in compatibility.
3. **UBO binding points** — emit GLSL 3.30+ `std140` uniform blocks; requires MonoGame runtime changes.

Strategy 1 is required for the drop-in `mgfxc` replacement design constraint.

---

## From Phase 8 — Compiler Library

**NuGet packaging verification (not run during Phase 8):**

- [ ] `7.4` Run `dotnet pack src/ShadowDusk.Compiler` — confirm `.nupkg` is produced with `PackageId=ShadowDusk.Compiler` and `PackageVersion=0.1.0`.
- [ ] `7.5` Create a scratch `ConsoleApp` outside the solution; add a local NuGet `--source` reference to the packed `.nupkg`; call `EffectCompiler.CompileAsync` on `Minimal.fx`; confirm bytes are written to disk successfully.

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

## How to resolve items here

1. **Verify and check off:** For items marked *(file exists — verify coverage)*, run the existing test file, confirm the assertion exists, then check it off in both this document and the originating `DONE/PHASE-X` file.
2. **Implement in the right phase:** Items tagged *(Phase 10)*, etc. belong in those phases — pick them up when starting that phase.
3. **Write missing tests:** Items with no file yet go into the appropriate test project; write them as part of a test sweep before release.
