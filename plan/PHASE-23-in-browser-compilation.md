# Phase 23 — In-Browser Compilation (mode 2 end-to-end), un-deferred from Phase 100

**Status:** Active (un-deferred 2026-05-31). Promotes the "Native WASM modules" tail out of [Phase 100](PHASE-100-deferred-backlog.md) into a real, sequenced phase because [Phase 22](PHASE-22-wasm-shader-fiddle-sample.md)'s showcase needs it — the deferral was blocking the *reach* promise it's meant to prove.

**Depends on:** Phase 19 (the managed engine: injectable backend seams, the pure-managed `SpirvReflector`, the DXIL-free GL reflection path, `WasmShaderCompiler` + the `[JSImport]` contract), Phase 22 (the consumer app), Phase 25 (untrusted web input), Phase 30 (headless-browser CI). Requires the **emscripten 3.1.34** toolchain (the exact version the .NET 8 WASM runtime is built with) and a real browser for the run-validation tail.

**Blocks:** the runtime half of the Part-1 (reach) promise in the browser — a `.fx` actually compiling **and** rendering client-side, no server. Closes the "modulo in-browser binary version" caveat Phase 19 left open.

---

## The crux: a split seam, because DXC and SPIRV-Cross are not alike

The project owner's requirement is **"C# only — no JS glue if avoidable."** Investigation (three review agents, 2026-05-31) shows *"if avoidable"* is the load-bearing clause: it's avoidable for one of the two native tools and **not** for the other.

| Pipeline stage | Desktop today | Browser seam (this phase) | JS? |
|---|---|---|---|
| HLSL → SPIR-V (**DXC**) | `Vortice.Dxc` **COM** (`IDxcCompiler3`) | **`[JSImport]`** to an emscripten module — unavoidable (see below) | ❌ one JS module |
| SPIR-V → GLSL (**SPIRV-Cross**) | raw `[DllImport]` C API (`spvc_*`) | **`NativeFileReference` `libspirv-cross.a` + the SAME `[DllImport]`** | ✅ pure C# |
| SPIR-V reflection | pure-managed `SpirvReflector` | unchanged | ✅ |
| FX parse / preprocess / GLSL rewrite / MGFX write | managed | unchanged | ✅ |

**Why SPIRV-Cross can be pure-C# (no JS):** it builds to a static `libspirv-cross.a`; its C API is ABI-stable and takes no managed callbacks; emscripten archives are exactly what .NET 8's `NativeFileReference` consumes. ShadowDusk's existing desktop `SpvcNative`/`SpvcLoader` P/Invoke surface is reused as-is. **`JsSpirvToGlslTranspiler` gets deleted.**

**Why DXC must stay `[JSImport]`:** Microsoft's DXC is an **LLVM/Clang fork that is explicitly not statically linkable** (COM self-DLL-loading internals + a proprietary `dxil.dll` signing blob not built from source — [Hexops devlog](https://devlog.hexops.org/2024/building-the-directx-shader-compiler-better-than-microsoft/), [DXC #4766](https://github.com/microsoft/DirectXShaderCompiler/issues/4766)). Every working "DXC in the browser" ships it as a self-contained emscripten **JS module**, never a linkable `.a`. And desktop DXC isn't `[DllImport]` either — it's COM via Vortice — so "mirror the desktop P/Invoke path" was never possible for DXC on any platform.

**Net:** this phase cuts the browser JS surface from **two modules to one** (`shadowdusk-dxc`), with SPIRV-Cross fully pure-C#. **True zero-JS is blocked only by DXC's own architecture** — reaching it requires either a static-linkable DXC fork (a research project of its own) or replacing DXC (see the fork below).

---

## The DXC fork (decision required) — A vs B

There is **no maintained prebuilt DXC-wasm** (the lone one, `A2K/javascript-hlsl-compiler`, is 2019, unlicensed, ~v1.0 DXC — unusable). So the `shadowdusk-dxc` module must come from one of:

- **Option A — build the *pinned* desktop DXC → WASM (emscripten 3.1.34).**
  - ✅ **Fidelity-safe:** same compiler + version → preserves Phase 19's *byte-identical-to-CLI* guarantee.
  - ❌ Multi-day, out-of-session: it's an LLVM fork; needs emscripten + patches for COM (WinAdapter), C++ exceptions (`-fwasm-exceptions`), and threading/FS assumptions. Large wasm (tens of MB).
- **Option B — pivot the browser frontend to Slang-wasm (`shader-slang`).**
  - ✅ Cheap & maintained: a real 5 MB in-browser WASM build exists today (v2026.10), HLSL-syntax input, SPIR-V output — no LLVM build.
  - ❌ **Strategic change + fidelity risk:** Slang's SPIR-V conventions (cbuffer byte layout via `-fvk-use-dx-layout`, the `-auto-binding-space 1` flat binding namespace, decorations) likely differ from DXC's, which `SpirvReflector` + the MojoShader GLSL chain depend on. Adopting it means re-proving equivalence and **probably relaxing "bytes identical to CLI" → "renders identically"** (which is, notably, the actual CLAUDE.md bar). Still `[JSImport]` (Slang-wasm is also an emscripten module).

**De-risking B before committing (in flight):** a no-build **Slang fidelity spike** — compile a corpus PS shader with `slangc` and DXC, disassemble both, and diff the cbuffer-offset / binding / decoration invariants `SpirvReflector` reads; then (if feasible) run Slang's SPIR-V through ShadowDusk's actual reflector + SPIRV-Cross and diff the GLSL/`.mgfx` against the DXC golden. The spike's verdict decides A vs B **without building anything**:
- *Reconcilable* (offsets/bindings match or differ by a cheap, known delta) → **B** (adopt Slang; generalize `SpirvReflector` as needed; cheap reach).
- *Deep mismatch* → **A** (the multi-day DXC-wasm build is the only fidelity-safe path) — or ship SPIRV-Cross-pure-C# now and leave DXC-wasm as a tracked follow-up.

> This A/B decision is the project owner's — it trades fidelity guarantee against effort by orders of magnitude. Do **not** adopt B silently; CLAUDE.md is explicit that swapping a producer and assuming the image still matches is exactly the failure mode to avoid.

---

## Hard constraints (confirmed)

1. **Emscripten version MUST be 3.1.34** — the version the .NET 8 WASM runtime is built with (proven by `Microsoft.NET.Runtime.Emscripten.3.1.34.Sdk.*` shipping in the 8.0.x band; .NET 9 moves to 3.1.56). A mismatch fails at link/load time, not cleanly. Pin it in `tools/restore.*`. *(SPIRV-Cross build agent to confirm the working recipe.)*
2. **`wasm-tools` workload required** (`dotnet workload install wasm-tools`) — present on this dev box; CI must install it.
3. **`Silk.NET.SPIRV.Cross.Native` has no `browser-wasm` build** — desktop RIDs only. We build/host `libspirv-cross.a` ourselves, exactly like the vkd3d-shader recipe (`tools/restore.*`, git-ignored, CI-cached by hash).
4. **`SpvcNative` callbacks:** none — every `spvc_*` call is value/pointer in/out, so the .NET 8 WASM "callbacks need function pointers" constraint does not bite.

---

## Tasks

> Legend: **◻ desktop/CI-verifiable** · **🖥️ browser-gated** · **⏳ emscripten-build-gated** · **❓ decision-gated (A/B)**

### Track A — SPIRV-Cross as a native WASM dependency (pure C#, no JS) — the easy half
- [ ] ⏳ A1. `tools/restore.*`: install/activate **emsdk 3.1.34**; add `Restore-SpirvCrossWasm` that builds `libspirv-cross.a` (the **C API**, static: `-DSPIRV_CROSS_SHARED=OFF -DSPIRV_CROSS_STATIC=ON -DSPIRV_CROSS_ENABLE_C_API=ON`), verifies presence, and prints the recipe + the 3.1.34 pin when absent. Mirror `Restore-Vkd3dShader`.
- [ ] ◻ A2. `ShadowDusk.GLSL.csproj`: add `<NativeFileReference Include=".../libspirv-cross.a" Condition="Exists(...) and browser-wasm" />` + `<WasmBuildNative>true</WasmBuildNative>`, conditioned so a no-artifact build stays green (mirror vkd3d's conditional include).
- [ ] ◻ A3. `SpvcLoader.Register()`: make it a **no-op under `OperatingSystem.IsBrowser()`** (static symbols bypass `SetDllImportResolver`). Fix the latent `SpvcNative.LibName="spirv-cross"` vs resolver-name `"spirv-cross-c-shared"` mismatch while here.
- [ ] ◻ A4. `WasmShaderCompiler`: inject the real `SpirvCrossGlslTranspiler`; **delete `JsSpirvToGlslTranspiler`**, the `SpirvCrossInterop` `[JSImport]`, and the `shadowdusk-spirv-cross` half of `Phase19.js`.
- **Gate:** link success is CI-verifiable without a browser; *correct GLSL output* needs the Track C browser run.

### Track B — DXC frontend (one JS module) — ❓ A/B-gated
- [ ] ❓/⏳ B1. Produce `dxcompiler.{js,wasm}` (Option A: pinned DXC→emscripten 3.1.34, `MODULARIZE`, exporting `compileToSpirv(hlsl, args[]) → Uint8Array` per the `shadowdusk-dxc` contract) **OR** the Slang-wasm module + an adapter (Option B). Add `Restore-DxcWasm`.
- [ ] ◻ B2. Keep `JsDxcShaderCompiler` + the `DxcFlagBuilder` reuse (args already byte-identical to desktop). Provide the host wiring (`JSHost.ImportAsync`/`setModuleImports('shadowdusk-dxc', …)`).

### Track C — End-to-end in-browser compile (the differentiator) — 🖥️
- [ ] 🖥️ C1. Host page registers `shadowdusk-dxc`, calls `WasmShaderCompiler.CompileAsync` on ≥1 corpus shader (OpenGL).
- [ ] 🖥️ C2. **Assert the in-browser `.mgfx` bytes equal the CLI output** for the same source + OpenGL target (Option A) — or, under Option B, assert **behavioral** equivalence vs the `mgfxc` golden and document the byte divergence.
- [ ] 🖥️ C3. No shader compile/link errors across the corpus in the console.

### Track D — Mode 1 (precompiled bytes load in WebGL) — 🖥️, lowest-risk, can land first
- [ ] 🖥️ D1. KNI WebGL `new Effect(gd, bytes)` on a CLI-compiled OpenGL `.mgfx`; render the corpus. *(Phase 22's sample already exercises this — fold its findings in, incl. the KNI MGFX-v10-vs-KNIFX-v11 load-parity question.)*
- [ ] 🖥️ D2. Confirm Phase-17 DesktopGL `.mgfx` loads+renders in WebGL; document any DesktopGL-vs-WebGL divergence.

### Track E — Sizing, security, CI
- [ ] 🖥️ E1. Measure download size / memory / cold-start (DXC/Slang wasm dominates); decide mode-2 default-on vs opt-in.
- [ ] E2. Run untrusted `.fx` through [Phase 25](PHASE-25-security-hardening.md) input validation.
- [ ] E3. [Phase 30 CI](PHASE-30-cross-platform-ci.md): headless-browser smoke for mode 1; install `wasm-tools` + pin emscripten 3.1.34; account for AV-scan slowness (CLAUDE.md Phase 21 note).

### Sequencing
A (1→4) and B (1→2) are parallel and mostly desktop/CI-verifiable. **C depends on A4 + B2.** D is independent (needs only a CLI `.mgfx`) and is the lowest-risk first landing. The **Slang spike gates B1's A-vs-B choice.**

---

## Definition of Done

A corpus shader compiles **entirely in-browser** by `ShadowDusk.Wasm` — DXC via the single `shadowdusk-dxc` JS module; **SPIRV-Cross via the statically-linked `libspirv-cross.a` through the same `[DllImport]` as desktop (no JS)** — and renders correctly in a real MonoGame/KNI **WebGL** build via `new Effect(gd, bytes)`, **no server**, with **≥1 corpus shader's in-browser `.mgfx` bytes identical to the CLI output** for the same source + OpenGL target (Option A) — or, under Option B, **behaviorally equivalent** to the `mgfxc` golden with the byte divergence documented. The polished Fiddle app remains Phase 22.

---

## Residual risk / out of scope

- **Zero-JS is not reached** while DXC stays a JS module. A genuine no-JS DXC needs a static-linkable DXC fork built for `browser-wasm` 3.1.34 — a separate spike (call it 23.1), not a blocker here.
- **Emscripten drift:** moving to .NET 9 means rebuilding `libspirv-cross.a` with ≈3.1.56; document the pin beside the vkd3d recipe.
- **DirectX/DXBC in WASM** stays out of scope (Phase 4.1) — no native P/Invoke / no WASM vkd3d.

## Key files

- `src/ShadowDusk.Wasm/{WasmShaderCompiler,JsShaderBackends}.cs`, `Phase19.js`, `ShadowDusk.Wasm.csproj`
- `src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs`, `Interop/SpvcNative.cs`, `Interop/SpvcLoader.cs`, `ShadowDusk.GLSL.csproj`
- `src/ShadowDusk.Core/Reflection/SpirvReflector.cs`
- `src/ShadowDusk.HLSL/Dxc/DxcShaderCompiler.cs` (Vortice COM — *not* `[DllImport]`), `Dxc/DxcFlagBuilder.cs`
- `src/ShadowDusk.Compiler/EffectCompiler.cs`, `Internal/CompilationPipeline.cs` (the injectable seam + `reflectFromSpirv`/`monoGameGl` gates)
- `tools/restore.ps1`, `tools/restore.sh` (the `Restore-Vkd3dShader` template)

## Sources
- [MS Learn — Blazor WASM native dependencies (8.0)](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-native-dependencies?view=aspnetcore-8.0)
- [NuGet — Microsoft.NET.Runtime.Emscripten.3.1.34.Sdk.win-x64 8.0.2](https://www.nuget.org/packages/Microsoft.NET.Runtime.Emscripten.3.1.34.Sdk.win-x64/8.0.2) (proves .NET 8 ⇒ emscripten 3.1.34)
- [Hexops — building DXC (static-link obstacles)](https://devlog.hexops.org/2024/building-the-directx-shader-compiler-better-than-microsoft/) · [DXC #4766](https://github.com/microsoft/DirectXShaderCompiler/issues/4766)
- [A2K/javascript-hlsl-compiler](https://github.com/A2K/javascript-hlsl-compiler) (the lone, unusable, 2019 DXC-wasm)
- [Slang playground / WASM build](https://github.com/shader-slang/slang-playground) · [Slang](https://github.com/shader-slang/slang)
- [SPIRV-Cross C API](https://github.com/KhronosGroup/SPIRV-Cross/blob/main/spirv_cross_c.h)
