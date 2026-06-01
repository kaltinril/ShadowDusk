# Phase 23 — In-Browser Compilation (mode 2 end-to-end), un-deferred from Phase 100

**Status:** Active (un-deferred 2026-05-31). Promotes the "Native WASM modules" tail out of [Phase 100](PHASE-100-deferred-backlog.md) into a real, sequenced phase because [Phase 22](PHASE-22-wasm-shader-fiddle-sample.md)'s showcase needs it — the deferral was blocking the *reach* promise it's meant to prove.

**Depends on:** Phase 19 (the managed engine: injectable backend seams, the pure-managed `SpirvReflector`, the DXIL-free GL reflection path, `WasmShaderCompiler` + the `[JSImport]` contract), Phase 22 (the consumer app), Phase 25 (untrusted web input), Phase 30 (headless-browser CI). Requires the **emscripten 3.1.34** toolchain (the exact version the .NET 8 WASM runtime is built with) and a real browser for the run-validation tail.

**Blocks:** the runtime half of the Part-1 (reach) promise in the browser — a `.fx` actually compiling **and** rendering client-side, no server. Closes the "modulo in-browser binary version" caveat Phase 19 left open.

---

## Where this stands (2026-05-31)

A *working* mode-2 chain was built on `phase22-web-inbrowser-compile` (now merged to main) — but it diverges from this doc's original plan in two ways:
1. It took **Option B (Slang-wasm)** as the HLSL→SPIR-V frontend, not DXC.
2. It shipped **SPIRV-Cross as a `[JSImport]` module too** (`spirv-cross.wasm`, node-verified byte-identical to desktop), not the `NativeFileReference` static-link planned below.

Every stage is node-verified; **there has been no real browser run.** Per THE PURPOSE the Slang frontend is **sample-only**. What remains is to make WASM *faithful and consumable by a third party* — captured as three gates and milestones M0–M3.

## "Usable to end users on WASM" = three gates

DoD is **not** "compiles in a browser." It is: *a third-party dev adds the NuGet to their KNI/Blazor app and it just works, faithfully, on shaders we have never seen.*

| Gate | Milestone | What it means | Status |
|---|---|---|---|
| **1 — Faithful frontend** | M0 | in-browser HLSL→SPIR-V trustworthy on **arbitrary** user shaders | ❓ decision (A/B) |
| **2 — Turnkey packaging** | M1 | `add package` + `call API`, **zero** consumer `wwwroot`/`JSHost` wiring | buildable now, frontend-agnostic |
| **3 — Proven render** | M3 | real (headless) browser compiles a corpus `.fx` → renders pixel-equal to `mgfxc` | needs a browser (CI) |

Gates 2 & 3 are unconditional wins. **Gate 1 is the pivotal bet.**

## Does KNI need byte-identical bytes? No — and that reframes Gate 1

KNI/MonoGame `new Effect(gd, bytes)` needs the `.mgfx` **structurally valid and behaviorally correct**, not byte-identical to anything (CLAUDE.md: *"behaviorally equivalent and `Effect`-loadable — NOT byte-identical to `mgfxc`"*). It loads correct bytes from any compiler.

Byte-identity is therefore **ShadowDusk's cheapest cross-host proof, not a runtime need:** if `wasm bytes == desktop bytes` and desktop is render-validated (Phase 17/18), WASM is validated **transitively, for free**. Drop it → you must **independently render-prove** the WASM output. So:

- **Option A — build pinned DXC → WASM (emscripten 3.1.34).** Same compiler+version ⇒ byte-identical ⇒ free transitive proof **and confidence on untested shaders** (it is literally the validated compiler). Best fit for a fiddle's open input. Cost: multi-day LLVM-fork emscripten build; tens-of-MB module (lazy-loaded).
- **Option B — substitute frontend (Slang-wasm, already built).** Different bytes by nature ⇒ no transitive proof. **Building it showed:** on the 10-shader corpus Slang reflects identically to DXC and (after the managed `NormalizeSlangNaming` shim) yields matching GLSL — *reconcilable for the corpus* — **but** 2 DXC flags don't forward through Slang's API and byte-identity across **arbitrary** shaders can't be proven, so a novel user shader can diverge silently. Viable for the product **only** with a real render-equivalence harness + a documented "validated on corpus X, renders-identically not byte-identical" caveat.

**The bet:** byte-identity isn't sacred, but it is the cheap proof *and* the open-input safety net. A's cost is the build; B's cost is perpetual validation + residual open-input risk. **Recommended: time-box an A spike** (build DXC→WASM, assert byte-identical SPIR-V on the corpus) — its outcome is the only thing that decides A vs B on evidence.

## Gate 2 — turnkey packaging (M1, do now, frontend-agnostic)

Faithfulness is moot if the consumer hand-wires assets. Today the **sample** hand-places the `.js`/`.wasm` in *its own* `wwwroot` and registers via `JSHost.ImportAsync`. For a third-party package that must vanish:

```xml
<TargetFramework>net8.0-browser</TargetFramework>
<PackageReference Include="ShadowDusk.Compiler" />
```
```csharp
var mgfx = (await compiler.CompileAsync(fx, ShaderTarget.OpenGL)).Value.MgfxBytes;
var effect = new Effect(graphicsDevice, mgfx);   // no wwwroot, no JSHost
```

Two delivery mechanisms, **not** symmetric:

| | **[JSImport] modules as static web assets** ✅ recommended | **NativeFileReference static-link** (original plan, SPIRV-Cross only) |
|---|---|---|
| Ship | `.wasm`+`.js` in the NuGet `wwwroot` → auto-served at `_content/ShadowDusk.Wasm/…`; lib self-registers | `libspirv-cross.a` linked into consumer's `dotnet.wasm` |
| Consumer wiring | none | none |
| Emscripten | **decoupled** — any version, survives .NET upgrades | **must match the .NET pin**; rebuild every .NET major |
| Lazy-load | yes (DXC loads on first compile) | no |
| DXC support | **only option** (DXC isn't static-linkable) | impossible |

**Recommendation:** ship *all* native bits as `[JSImport]` modules packaged as static web assets, **self-registered by `ShadowDusk.Wasm`**. It is the only path DXC allows, it is version-decoupled (no per-.NET rebuild treadmill), lazy-loadable, and already the built pattern. THE PURPOSE's goal is zero *consumer wiring*, not zero JS modules — static web assets deliver that. *Tension to flag for the owner:* this revises the original "C# only / no JS if avoidable" lean for SPIRV-Cross — static-link stays a valid later **download-size** optimization, but it costs the emscripten-pin treadmill and doesn't change the consumer experience (already zero-wiring either way).

**M1 required actions:**
- [ ] Multi-target `ShadowDusk.Wasm` (and the consumer-facing `ShadowDusk.Compiler`) `net8.0;net8.0-browser`; the browser TFM selects the WASM backends.
- [ ] Move `shadowdusk-dxc.js`, `shadowdusk-spirv-cross.js`, `spirv-cross.wasm` (+ the chosen frontend `.wasm`) from the sample `wwwroot` into **`ShadowDusk.Wasm`'s packaged `wwwroot`** as static web assets.
- [ ] `ShadowDusk.Wasm` self-registers the modules via `JSHost.ImportAsync` against its own `_content/ShadowDusk.Wasm/` base path; delete the consumer-side registration the sample does today.
- [ ] Verify with a scratch consumer **outside the repo**: reference only the package, set `net8.0-browser`, compile a `.fx` with **no manual asset wiring** (mirror the desktop self-contained verification already done for the desktop NuGet).

## Milestones

- **M0 — DXC→WASM spike** — gates Gate 1; decides A vs B on evidence. ❓ owner's call (effort vs fidelity, orders of magnitude apart).
- **M1 — turnkey packaging** — Gate 2; frontend-agnostic; **do now**.
- **M2 — wire the chosen faithful frontend** into the package (DXC module if M0 succeeds; else Option B + a render-equivalence harness).
- **M3 — headless-browser render proof** — Gate 3; Playwright in CI; ties to [Phase 30](PHASE-30-cross-platform-ci.md).

M1 + M3 are unconditional. The detailed compile-seam task tracks (A–E) below remain valid, **except** Track A's "SPIRV-Cross via static-link / delete `JsSpirvToGlslTranspiler`" is **superseded as the default** by the M1 packaging recommendation (static web assets) — kept as a future optimization, not the path.

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

**Update — the spike was effectively run by *building* Option B (2026-05-31):** instead of a no-build disassembly diff, the full Slang path was prototyped (merged `phase22-web-inbrowser-compile`). Result on the corpus: Slang's SPIR-V reflects identically to DXC's and, after the managed `NormalizeSlangNaming` shim, yields matching GLSL — *reconcilable for the corpus* — **but** byte-identity across arbitrary shaders is unprovable (2 DXC flags don't forward), so it is **sample-only** until a render-equivalence harness backs it (see *Does KNI need byte-identical bytes?* above). The remaining diagnostic, if Option B is pursued for the *product*, is the same invariant diff (cbuffer-offset / binding / decoration) generalized beyond the corpus:
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

**Overall — the three gates:** Gate 1, a *faithful* frontend chosen and proven (Option A byte-identical, or Option B + a render-equivalence harness with the documented caveat); Gate 2, a third-party consumer compiles a `.fx` with **only** a `PackageReference` (no `wwwroot`/`JSHost` wiring); Gate 3, a real headless browser renders a corpus `.fx` pixel-equivalent to `mgfxc`. Compile-seam DoD detail:

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
