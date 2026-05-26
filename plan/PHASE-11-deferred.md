# Phase 11 — Deferred Items

**Status:** Backlog  
**Blocking anything?** No. All items here were explicitly deferred from earlier phases and are not prerequisites for Phases 6–10. Review before a 1.0 release.

This document collects every unchecked item from completed (`DONE/`) phase plans, organized by source phase. Items that have an existing file in the test suite are marked with `(file exists — verify coverage)`.

---

## From Phase 2 — FX9 Pre-Parser

- [ ] Stripped HLSL output compiles without syntax errors when passed to DXC.
  *(originally deferred: "verified by integration test in Phase 3." Phase 4 DXC integration likely covers this — run `dotnet test --filter Category=Integration` and check it off if passing.)*

---

## From Phase 3 — Preprocessor / Macro Injection

**Tests — not yet written:**

- [ ] `4.4` FileSystemIncludeResolver integration test: resolve a real `.fxh` file from disk.
  *(deferred to Phase 9 integration suite — add to `ShadowDusk.Integration.Tests/Preprocessor/`)*
- [ ] `7.4` DxcIncludeHandler smoke test: construct handler with `InMemoryIncludeResolver`,
  verify `LoadSource` returns the correct blob bytes.
  *(deferred: "requires live DXC COM init; add in Phase 4 DXC integration tests" — add to `ShadowDusk.Integration.Tests/Dxc/DxcShaderCompilerIntegrationTests.cs`)*

**Wiring validation — likely already done by Phase 4, but never explicitly verified:**

- [ ] `8.2` `Preprocessor.Flatten()` is called after Phase 2 and before DXC invocation.
- [ ] `8.3` `PreprocessedSource.DxcMacroFlags` is merged into the DXC compile arguments list.
- [ ] `8.4` `DxcIncludeHandler` instance is constructed from `IIncludeResolver` and forwarded
  to `IDxcCompiler3.Compile()`.
  *(All three: verify by reading DxcShaderCompiler.cs — if the wiring is in place, check them off in `DONE/PHASE-3-preprocessor-macro-injection.md`.)*

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
- [ ] DirectX_11 vertex stage: `vs_5_0`; no `-spirv`
- [ ] DirectX_11 pixel stage: `ps_5_0`; no `-spirv`
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
- [ ] Minimal vertex shader → non-empty DXBC (DirectX_11)
- [ ] Syntax error → `Result.Failure` with `FxcFormattedMessage` containing `(line,col`
- [ ] Undefined variable in pixel shader → failure with line/col in FXC format
- [ ] Vulkan target vertex shader → non-empty SPIR-V
- [ ] Compile with `-D` macro succeeds and macro is visible to DXC
- [ ] Cancellation before invocation → `OperationCanceledException` (not `ShaderError`)

---

## From Phase 5 — Shader Reflection

**SPIRV-Cross binding slot verifier — implement in Phase 6:**

- [ ] `7.3.2` Use SPIRV-Cross P/Invoke (`spvc_context_create` → `spvc_compiler_create_shader_resources`).
  Enumerate `separate_images` and `separate_samplers`.
- [ ] `7.3.3` Compare DXIL and SPIR-V slots. Emit `ShaderError` with `"SD0101"` on mismatch.
  *(Pick these up at the start of Phase 6 — SPIRV-Cross P/Invoke will be in place.)*

**Golden snapshot acceptance test — implement in Phase 9:**

- [ ] `9.3.1` `MgfxParameterMatchTests.cs` — compile a MonoGame reference shader, run `ReflectionPipeline`,
  compare output against a golden JSON snapshot from MonoGame's own `mgfxc`.
- [ ] `9.3.2` Snapshot comparison must be exact (name, class, type, rows, columns, elements).
  *(Add to Phase 9 integration suite once Phase 7 MGFX writer is available.)*

---

## How to resolve items here

1. **Verify and check off:** For items marked *(file exists — verify coverage)*, run the existing test file, confirm the assertion exists, then check it off in both this document and the originating `DONE/PHASE-X` file.
2. **Implement in the right phase:** Items tagged *(Phase 6)*, *(Phase 9)*, etc. belong in those phases — pick them up when starting that phase.
3. **Write missing tests:** Items with no file yet go into the appropriate test project; write them as part of a test sweep before release.
