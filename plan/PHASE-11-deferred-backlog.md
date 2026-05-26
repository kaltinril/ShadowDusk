# Phase 11 — Deferred Backlog

Items deferred from completed phases that don't belong in any active phase plan.
Each item should be picked up when the prerequisite infrastructure exists.

---

## From Phase 6 — SPIRV-Cross GLSL Transpilation

### 11-6-A: Wire SpirvCrossGlslTranspiler into ShaderCompiler orchestrator

**Blocked by:** `ShaderCompiler` orchestrator does not exist in `ShadowDusk.Core` yet (not created as of Phase 6).

When the orchestrator is created (likely during Phase 7 or 8):
1. After DXC emits SPIR-V for vertex and pixel stages (`PlatformBlob.Kind == BlobKind.Spirv`), call `SpirvCrossGlslTranspiler.Transpile()` for each stage using the `ReadOnlyMemory<byte>` overload.
2. Reinterpret: `MemoryMarshal.Cast<byte, uint>(platformBlob.Bytes.Span)` is already handled by the `ReadOnlyMemory<byte>` overload — no manual conversion needed.
3. Store `GlslSource` results on the per-pass result record (type TBD — `PassBlob` does not exist yet; must be designed as part of the orchestrator phase).
4. Propagate `ShaderError` up — do not swallow.

### 11-6-B: `Transpile_PassthroughVertex_YFlipIsApplied` integration test

**Blocked by:** Requires the SPIRV-Cross native library to be present, and requires knowing the exact GLSL expression SPIRV-Cross emits for the Y-flip so the assertion is stable.

When native library is available:
1. Compile `tests/fixtures/shaders/passthrough_vs.fx` through `DxcShaderCompiler` targeting OpenGL.
2. Pass vertex SPIR-V to `SpirvCrossGlslTranspiler.Transpile()`.
3. Assert that `gl_Position.y` appears negated in the output (check for `-gl_Position.y` or equivalent sign-flip expression emitted by SPIRV-Cross for the `FlipVertexY` option).

### 11-6-C: Run Phase 6 integration tests and platform check

**Blocked by:** SPIRV-Cross native library not yet downloaded on the development machine.

Steps:
1. Run `.\tools\restore.ps1` (Windows) or `./tools/restore.sh` (Linux/macOS) to populate `tools/spirv-cross/`.
2. Rebuild so MSBuild copies the native library to the output directory.
3. Run `/test --filter "Category=Integration&Platform=OpenGL"` — all 6 `GlslTranspilerTests` should pass.
4. Run `/platform-check` to confirm no new platform-specific assumptions were introduced in Phase 6.

### 11-6-D: Uniform remapping for MonoGame OpenGL runtime compatibility

**Context:** Researched in Phase 6 (see `docs/glsl-uniform-naming.md`).

MonoGame's OpenGL runtime expects uniforms in MojoShader convention (`vs_uniforms_vec4[N]` / `ps_uniforms_vec4[N]` float4 arrays), not the HLSL variable names that SPIRV-Cross produces by default. Phase 6 deferred this to Phase 7.

**Prerequisite for:** Phase 7 `.mgfx` binary writer producing OpenGL-compatible output.

Three strategies (evaluate when Phase 7 begins):
1. **Post-process GLSL** — parse SPIRV-Cross output, remap individual uniform declarations to `vs_uniforms_vec4[N]` array slots matching SM 3.0 register layout. Most compatible; requires non-trivial GLSL post-processing.
2. **Patch MonoGame runtime** — emit GLSL with HLSL names; ship a modified MonoGame OpenGL runtime that looks up by name. Breaks drop-in compatibility.
3. **UBO binding points** — emit GLSL 3.30+ `std140` uniform blocks; requires MonoGame runtime changes.

Strategy 1 is required for the drop-in `mgfxc` replacement design constraint.
