# Phase 23 — In-Browser Compilation (mode 2 end-to-end), un-deferred from Phase 100

**Status:** Active (un-deferred 2026-05-31). Promotes the "Native WASM modules" tail out of [Phase 100](PHASE-100-deferred-backlog.md) into a real, sequenced phase because [Phase 22](PHASE-22-wasm-shader-fiddle-sample.md)'s showcase needs it — the deferral was blocking the *reach* promise it's meant to prove.

**Depends on:** Phase 19 (the managed engine: injectable backend seams, the pure-managed `SpirvReflector`, the DXIL-free GL reflection path, `WasmShaderCompiler` + the `[JSImport]` contract), Phase 22 (the consumer/sample app), **Phase 24 (the headless-browser render harness — Gate 3's proof tool, which must come up *before* the faithful frontend lands so KNI-load risk is retired first)**, Phase 25 (untrusted web input), Phase 30 (CI wiring of the Phase 24 harness). Requires the **emscripten 3.1.34** toolchain (the exact version the .NET 8 WASM runtime is built with) and a real browser for the run-validation tail.

**Blocks:** the runtime half of the Part-1 (reach) promise in the browser — a `.fx` actually compiling **and** rendering client-side, no server. Closes the "modulo in-browser binary version" caveat Phase 19 left open.

---

## DECISION (2026-06-01): Option A — faithful DXC→WASM is the product frontend

Per **THE PURPOSE** ("no substitute compilers"), the in-browser HLSL→SPIR-V frontend for the **product** is the **pinned desktop DXC compiled to WASM** (Option A). **Slang-wasm is sample-only and is never the product frontend** — it stays in `samples/ShaderFiddle.Web/` to demonstrate reach while DXC→WASM is built, and is removed from / never promoted to the shipping `ShadowDusk.Wasm` package. The A-vs-B trade studied below is **settled**; it is retained only as the rationale for why A is mandatory and what B's residual risk was. This phase is **not done** until a corpus `.fx` compiles in-browser through the *faithful* DXC→WASM path with bytes identical to the CLI.

## Where this stands (2026-06-01)

A *working* mode-2 chain was built on `phase22-web-inbrowser-compile` (now merged to main):
1. It uses **Slang-wasm** as the HLSL→SPIR-V frontend — **a substitute compiler, accepted as sample-only**, NOT the product path (see DECISION above).
2. It ships **SPIRV-Cross as a `[JSImport]` module** (`spirv-cross.wasm`, node-verified byte-identical to desktop). This is the *faithful* SPIRV-Cross (same library + version as desktop), and the `[JSImport]`-static-web-asset delivery is the **chosen mechanism** (it is what DXC will also require — see *Gate 2*). The original `NativeFileReference` static-link plan is demoted to a future download-size optimization.

Every stage is node-verified; **there has been no real browser run** — Phase 24 closes that gap. What remains for the *product* is: (a) build the faithful DXC→WASM module (M0/M2), and (b) prove it renders in a real browser (M3, via Phase 24's harness).

### Prerequisite surfaced by Phase 24 — `roundEven` not WebGL1-valid — ✅ RESOLVED (2026-06-01)

Phase 24 ran the corpus in real KNI WebGL and proved MGFX **v10 renders 10/10** there (no KNIFX-v11 needed). But it validated the *golden (mgfxc)* bytes; when the corpus is compiled by **ShadowDusk's own** GL pipeline, **`Pixelated.mgfx` failed to load in KNI WebGL** because ShadowDusk emitted **`roundEven()`** (DXC maps HLSL `round`→`OpRoundEven`→SPIRV-Cross `roundEven()`), which **GLSL ES 1.00 / WebGL1 does not provide**. Desktop GL has it, so no desktop test caught it.

- **Fixed:** `MonoGameGlslRewriter` "Rule 8" (`LowerRoundToFloorHalfUp`) lowers `roundEven(x)` and bare `round(x)` → `floor((x) + 0.5)` for the GL profile — WebGL1-valid in all profiles and **byte-faithful to mgfxc** (the golden expresses `round` as `floor(x+0.5)`). Balanced-paren arg capture; whole-identifier matching (won't touch `ground`/`myround`).
- **Harness gap closed:** the Phase 24 harness now has a `--corpus=sd` path (`publish-sample-sd.mjs`, `RESULTS-SD.md`, `references-sd/`) that validates **ShadowDusk's own** `.mgfx` in real KNI WebGL, not just the golden — so this class of "our output ≠ loadable in WebGL" bug can't hide again.
- **Verified:** ShadowDusk's own `Pixelated` now loads + renders (Δ1 LSB) in real headless KNI WebGL; corpus 10/10 load + render. Desktop cross-validation vs mgfxc still passes (ImageTests 25, byte-identity/determinism 128 incl. 10/10, full solution **498/498**). See `tests/ShadowDusk.BrowserTests/ROUNDEVEN-FIX.md`.

## "Usable to end users on WASM" = three gates

DoD is **not** "compiles in a browser." It is: *a third-party dev adds the NuGet to their KNI/Blazor app and it just works, faithfully, on shaders we have never seen.*

| Gate | Milestone | What it means | Status |
|---|---|---|---|
| **1 — Faithful frontend** | M0/M2 | in-browser HLSL→SPIR-V trustworthy on **arbitrary** user shaders — **= faithful DXC→WASM** (decided) | 🔨 DXC→WASM build outstanding (Slang is sample-only) |
| **2 — Turnkey packaging** | M1 | `add package` + `call API`, **zero** consumer `wwwroot`/`JSHost` wiring | buildable now, frontend-agnostic |
| **3 — Proven render** | M3 | real (headless) browser compiles a corpus `.fx` → renders pixel-equal to `mgfxc` | needs Phase 24 harness |

All three gates are now committed work; **Gate 1 = the DXC→WASM build is the long pole.**

## Does KNI need byte-identical bytes? No — and that reframes Gate 1

KNI/MonoGame `new Effect(gd, bytes)` needs the `.mgfx` **structurally valid and behaviorally correct**, not byte-identical to anything (CLAUDE.md: *"behaviorally equivalent and `Effect`-loadable — NOT byte-identical to `mgfxc`"*). It loads correct bytes from any compiler.

Byte-identity is therefore **ShadowDusk's cheapest cross-host proof, not a runtime need:** if `wasm bytes == desktop bytes` and desktop is render-validated (Phase 17/18), WASM is validated **transitively, for free**. A substitute frontend forfeits this and forces an **independent render-proof** of every output. This is exactly why **Option A is the product path**:

- **Option A (CHOSEN) — build pinned DXC → WASM (emscripten 3.1.34).** Same compiler+version ⇒ byte-identical ⇒ free transitive proof **and confidence on untested shaders** (it is literally the validated compiler). Best fit for a fiddle's open input. Cost: multi-day LLVM-fork emscripten build; tens-of-MB module (lazy-loaded). **This cost is accepted.**
- **Option B (REJECTED for the product; sample-only) — substitute frontend (Slang-wasm, already built).** Different bytes by nature ⇒ no transitive proof. The Slang build showed: on the 10-shader corpus Slang reflects identically to DXC and (after the managed `NormalizeSlangNaming` shim) yields matching GLSL — *reconcilable for the corpus* — **but** 2 DXC flags don't forward through Slang's API and byte-identity across **arbitrary** shaders can't be proven, so a novel user shader can diverge silently. That silent-divergence risk on open fiddle input is precisely the failure mode THE PURPOSE forbids, so Slang **cannot** be the product frontend. It remains in the **sample** only.

**Settled:** A's cost is a one-time build; B's cost is perpetual validation + an irreducible open-input divergence risk that violates "no substitute compilers." A is the product frontend; the DXC→WASM build (M0/M2) is the remaining work.

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

- **M0 — build pinned DXC→WASM** (emscripten 3.1.34) — Gate 1. **Decided (Option A); the build is the work**, not a decision. Out-of-session LLVM-fork build (see the recipe in Track B below). DoD: emits `dxcompiler.{js,wasm}` whose `compileToSpirv` produces **byte-identical SPIR-V to the desktop CLI** on the corpus.
- **M1 — turnkey packaging** — Gate 2; frontend-agnostic; **do now** (does not wait on M0).
- **M2 — wire the faithful DXC→WASM frontend** into the shipping `ShadowDusk.Wasm` package; the Slang module stays in the sample only.
- **M3 — headless-browser render proof** — Gate 3; runs on **[Phase 24](PHASE-24-browser-render-validation.md)**'s Playwright harness, then wired into [Phase 30](PHASE-30-cross-platform-ci.md) CI.

M1 + M3 are unconditional. The detailed compile-seam task tracks (A–E) below remain valid, **except** Track A's "SPIRV-Cross via static-link / delete `JsSpirvToGlslTranspiler`" is **NOT the path** — SPIRV-Cross ships as a `[JSImport]` static web asset (same mechanism DXC requires); static-link is kept only as a future download-size optimization.

---

## The seam: both native tools ship as `[JSImport]` static web assets

The project owner's original lean was **"C# only — no JS glue if avoidable."** Investigation (three review agents, 2026-05-31) plus the 2026-06-01 packaging decision settled it: DXC **cannot** be static-linked (so it must be `[JSImport]`), and once DXC is a JS module, shipping SPIRV-Cross the same way (rather than as a static-link with a per-.NET emscripten-pin treadmill) is the simpler, version-decoupled choice that delivers identical zero-*consumer*-wiring. So **both** native tools ship as `[JSImport]` modules packaged as static web assets, self-registered by `ShadowDusk.Wasm`.

| Pipeline stage | Desktop today | Browser seam (this phase) | Delivery |
|---|---|---|---|
| HLSL → SPIR-V (**DXC**) | `Vortice.Dxc` **COM** (`IDxcCompiler3`) | **`[JSImport]`** to the faithful DXC→WASM emscripten module — unavoidable (see below) | `[JSImport]` static web asset |
| SPIR-V → GLSL (**SPIRV-Cross**) | raw `[DllImport]` C API (`spvc_*`) | **`[JSImport]`** to `spirv-cross.wasm` (already built, node-verified byte-identical) | `[JSImport]` static web asset |
| SPIR-V reflection | pure-managed `SpirvReflector` | unchanged | ✅ pure C# |
| FX parse / preprocess / GLSL rewrite / MGFX write | managed | unchanged | ✅ pure C# |

**Why DXC must stay `[JSImport]`:** Microsoft's DXC is an **LLVM/Clang fork that is explicitly not statically linkable** (COM self-DLL-loading internals + a proprietary `dxil.dll` signing blob not built from source — [Hexops devlog](https://devlog.hexops.org/2024/building-the-directx-shader-compiler-better-than-microsoft/), [DXC #4766](https://github.com/microsoft/DirectXShaderCompiler/issues/4766)). Every working "DXC in the browser" ships it as a self-contained emscripten **JS module**, never a linkable `.a`. And desktop DXC isn't `[DllImport]` either — it's COM via Vortice — so "mirror the desktop P/Invoke path" was never possible for DXC on any platform.

**Why SPIRV-Cross also ships as `[JSImport]` (not static-link):** it *could* be a `NativeFileReference` `libspirv-cross.a` (its C API is ABI-stable, no managed callbacks), and that stays a valid future download-size optimization. But static-link **must match the .NET emscripten pin and be rebuilt every .NET major**, whereas the `[JSImport]` module is version-decoupled, lazy-loadable, and is **already built and node-verified byte-identical to desktop**. Since DXC forces a JS module regardless, there is no zero-JS prize to win by static-linking only SPIRV-Cross — so it ships as the `[JSImport]` module. `JsSpirvToGlslTranspiler` is **kept**, not deleted.

**Net:** the faithful product path is **two `[JSImport]` static-web-asset modules** (`shadowdusk-dxc` = faithful DXC→WASM, `shadowdusk-spirv-cross`) + pure-managed reflection/parse/write. The consumer wires **nothing** (`ShadowDusk.Wasm` self-registers). True zero-JS is not a goal here — it is blocked by DXC's architecture and would buy nothing for the consumer.

---

## The DXC→WASM build (decided: Option A) — A vs B rationale, for the record

> **Decision is made (2026-06-01): Option A.** This section is retained to document *why* and what B's risk was — it is no longer an open call.

There is **no maintained prebuilt DXC-wasm** (the lone one, `A2K/javascript-hlsl-compiler`, is 2019, unlicensed, ~v1.0 DXC — unusable). So the faithful `shadowdusk-dxc` module must be built ourselves (Option A); the alternatives were:

- **Option A — build the *pinned* desktop DXC → WASM (emscripten 3.1.34).**
  - ✅ **Fidelity-safe:** same compiler + version → preserves Phase 19's *byte-identical-to-CLI* guarantee.
  - ❌ Multi-day, out-of-session: it's an LLVM fork; needs emscripten + patches for COM (WinAdapter), C++ exceptions (`-fwasm-exceptions`), and threading/FS assumptions. Large wasm (tens of MB).
- **Option B — pivot the browser frontend to Slang-wasm (`shader-slang`).**
  - ✅ Cheap & maintained: a real 5 MB in-browser WASM build exists today (v2026.10), HLSL-syntax input, SPIR-V output — no LLVM build.
  - ❌ **Strategic change + fidelity risk:** Slang's SPIR-V conventions (cbuffer byte layout via `-fvk-use-dx-layout`, the `-auto-binding-space 1` flat binding namespace, decorations) likely differ from DXC's, which `SpirvReflector` + the MojoShader GLSL chain depend on. Adopting it means re-proving equivalence and **probably relaxing "bytes identical to CLI" → "renders identically"** (which is, notably, the actual CLAUDE.md bar). Still `[JSImport]` (Slang-wasm is also an emscripten module).

**Why B was rejected for the product (2026-06-01):** the Slang path was fully prototyped (merged `phase22-web-inbrowser-compile`). On the corpus, Slang's SPIR-V reflects identically to DXC's and, after the managed `NormalizeSlangNaming` shim, yields matching GLSL — *reconcilable for the corpus*. **But** 2 DXC flags (`-fvk-use-dx-layout`, `-fvk-use-entrypoint-name`) don't forward through Slang's API, so byte-identity across **arbitrary** shaders is unprovable and a novel user shader can diverge silently. A fiddle's whole point is open user input, so "validated on a 10-shader corpus" cannot generalize to a safety guarantee. CLAUDE.md is explicit that swapping a producer and assuming the image still matches is exactly the failure mode to avoid — so B is **sample-only**, and the product gets the faithful DXC→WASM build (A).

### DXC→WASM build recipe (Option A — M0)

The build is out-of-session (multi-day, LLVM-fork) but well-trodden; capture the recipe in `tools/restore.*` as `Restore-DxcWasm`, mirroring `Restore-Vkd3dShader`. Ordered steps:

1. **Pin sources.** Clone `microsoft/DirectXShaderCompiler` at the **exact tag/commit the desktop `Vortice.Dxc` NuGet wraps** (read the version from the restored desktop `dxcompiler` so the WASM build is the *same compiler version* — this is what makes the output byte-identical and the transitive proof valid).
2. **Pin the toolchain.** emscripten **3.1.34** (the .NET 8 pin — § Hard constraints). Activate via emsdk in the restore script.
3. **Patch for WASM** (the known obstacles, per the Hexops devlog / DXC #4766):
   - **COM:** build with DXC's `WinAdapter` (its non-Windows COM shim) so `IDxcCompiler3`/`IDxcUtils` resolve without the Windows COM runtime.
   - **C++ exceptions:** compile/link with `-fwasm-exceptions` (DXC uses exceptions internally; the default `-fno-exceptions` WASM build will trap).
   - **Filesystem/threading:** stub or `-s` the FS/`pthread` assumptions; DXC expects a real FS for `#include` resolution — route includes through an in-memory FS or the `IDxcIncludeHandler` we already pass on desktop.
   - **`dxil.dll` signing blob:** the proprietary validator/signer is *not* buildable from source. For the SPIR-V target we do **not** need DXIL signing (we emit `-spirv`), so build DXC **without** the validator and confirm the `-spirv` path never touches `dxil.dll` (it doesn't — signing is a DXIL-only step).
4. **Export contract.** Compile with `MODULARIZE`, exporting a single `compileToSpirv(hlsl: string, args: string[]) → Uint8Array` matching the existing `shadowdusk-dxc` JS contract (so `JsDxcShaderCompiler` + `DxcFlagBuilder` are reused unchanged — the desktop arg list already forwards verbatim, unlike Slang).
5. **Verify byte-identity (the gate).** Node harness (mirror `.wasm-build/node-test-spirv-cross.mjs`): for each corpus `.fx`, assert the WASM `compileToSpirv` SPIR-V **equals** the desktop DXC SPIR-V byte-for-byte. This is M0's DoD and is what licenses the free transitive render-proof.
6. **Package.** Drop `dxcompiler.{js,wasm}` into `ShadowDusk.Wasm`'s packaged `wwwroot` as a static web asset (M1/M2); lazy-load on first compile (tens of MB).

---

## Hard constraints (confirmed)

1. **Emscripten version MUST be 3.1.34** — the version the .NET 8 WASM runtime is built with (proven by `Microsoft.NET.Runtime.Emscripten.3.1.34.Sdk.*` shipping in the 8.0.x band; .NET 9 moves to 3.1.56). A mismatch fails at link/load time, not cleanly. Pin it in `tools/restore.*`. *(SPIRV-Cross build agent to confirm the working recipe.)*
2. **`wasm-tools` workload required** (`dotnet workload install wasm-tools`) — present on this dev box; CI must install it.
3. **`Silk.NET.SPIRV.Cross.Native` has no `browser-wasm` build** — desktop RIDs only. We build/host `libspirv-cross.a` ourselves, exactly like the vkd3d-shader recipe (`tools/restore.*`, git-ignored, CI-cached by hash).
4. **`SpvcNative` callbacks:** none — every `spvc_*` call is value/pointer in/out, so the .NET 8 WASM "callbacks need function pointers" constraint does not bite.

---

## Tasks

> Legend: **◻ desktop/CI-verifiable** · **🖥️ browser-gated** · **⏳ emscripten-build-gated** · **❓ decision-gated (A/B)**

### Track A — SPIRV-Cross in the browser (DONE via `[JSImport]`; static-link is an optional later optimization)
- [x] SPIRV-Cross ships as `spirv-cross.wasm` + `shadowdusk-spirv-cross.js`, driven by `JsSpirvToGlslTranspiler` — built, committed, **node-verified byte-identical to desktop** (`.wasm-build/node-test-spirv-cross.mjs`). This is the faithful, version-decoupled path and the one that ships.
- [ ] *(optional, deferred)* A-opt. Static-link `libspirv-cross.a` via `NativeFileReference` to shave the module download — only worth it if download size becomes a problem, and it re-introduces the per-.NET emscripten-pin treadmill. Recipe (`Restore-SpirvCrossWasm`, `-DSPIRV_CROSS_STATIC=ON -DSPIRV_CROSS_ENABLE_C_API=ON`, `SpvcLoader.Register()` no-op under `OperatingSystem.IsBrowser()`) is kept on file but **not on the critical path**. Do **not** delete `JsSpirvToGlslTranspiler`.

### Track B — Faithful DXC→WASM frontend (one `[JSImport]` module) — Option A, the long pole
- [ ] ⏳ B1. Produce `dxcompiler.{js,wasm}` per the **DXC→WASM build recipe** above (pinned DXC source matching the desktop `Vortice.Dxc` version → emscripten 3.1.34, `WinAdapter` COM shim, `-fwasm-exceptions`, in-memory FS includes, no DXIL signer, `MODULARIZE`, exporting `compileToSpirv(hlsl, args[]) → Uint8Array`). Add `Restore-DxcWasm`.
- [ ] ⏳ B1-gate. **Byte-identity gate** (M0 DoD): node harness asserts WASM SPIR-V == desktop DXC SPIR-V on the full corpus.
- [ ] ◻ B2. `JsDxcShaderCompiler` + `DxcFlagBuilder` are reused unchanged (the desktop arg list forwards verbatim to DXC — the very property Slang lacks). `ShadowDusk.Wasm` self-registers `shadowdusk-dxc` (M1/M2). Remove the Slang `shadowdusk-dxc.js` shim from the shipping package (it stays in the sample only).

### Track C — End-to-end in-browser compile (the differentiator) — 🖥️
- [ ] 🖥️ C1. Host page registers `shadowdusk-dxc`, calls `WasmShaderCompiler.CompileAsync` on ≥1 corpus shader (OpenGL).
- [ ] 🖥️ C2. **Assert the in-browser `.mgfx` bytes equal the CLI output** for the same source + OpenGL target (the faithful DXC→WASM path makes this hold; it is the transitive render-proof).
- [ ] 🖥️ C3. No shader compile/link errors across the corpus in the console.

### Track D — Mode 1 (precompiled bytes load in WebGL) — 🖥️, lowest-risk, **owned by [Phase 24](PHASE-24-browser-render-validation.md)** (run first)
- [ ] 🖥️ D1. KNI WebGL `new Effect(gd, bytes)` on a CLI-compiled OpenGL `.mgfx`; render the corpus. *(Phase 22's sample already exercises this — fold its findings in, incl. the KNI MGFX-v10-vs-KNIFX-v11 load-parity question.)*
- [ ] 🖥️ D2. Confirm Phase-17 DesktopGL `.mgfx` loads+renders in WebGL; document any DesktopGL-vs-WebGL divergence.

### Track E — Sizing, security, CI
- [ ] 🖥️ E1. Measure download size / memory / cold-start (DXC/Slang wasm dominates); decide mode-2 default-on vs opt-in.
- [ ] E2. Run untrusted `.fx` through [Phase 25](PHASE-25-security-hardening.md) input validation.
- [ ] E3. [Phase 30 CI](PHASE-30-cross-platform-ci.md): headless-browser smoke for mode 1; install `wasm-tools` + pin emscripten 3.1.34; account for AV-scan slowness (CLAUDE.md Phase 21 note).

### Sequencing
SPIRV-Cross (Track A) is already done via `[JSImport]`. **D (mode-1 precompiled load) is the lowest-risk first landing and is owned by [Phase 24](PHASE-24-browser-render-validation.md)** — it retires the KNI MGFXReader10-load risk *before* the DXC→WASM build effort, so run it first. **B1 (the DXC→WASM build) is the long pole**; B2 + M1 packaging proceed in parallel. **C (faithful end-to-end compile) depends on B1-gate + B2** and is render-proven on Phase 24's harness (M3).

---

## Definition of Done

**Overall — the three gates:** Gate 1, the **faithful DXC→WASM frontend** built and proven byte-identical to the CLI on the corpus (Option A — Slang is sample-only and does **not** satisfy this gate); Gate 2, a third-party consumer compiles a `.fx` with **only** a `PackageReference` (no `wwwroot`/`JSHost` wiring); Gate 3, a real headless browser (Phase 24 harness) renders a corpus `.fx` pixel-equivalent to `mgfxc`. Compile-seam DoD detail:

A corpus shader compiles **entirely in-browser** by `ShadowDusk.Wasm` — DXC via the faithful `shadowdusk-dxc` `[JSImport]` module (pinned DXC→WASM); SPIRV-Cross via the `shadowdusk-spirv-cross` `[JSImport]` module (both packaged as self-registered static web assets) — and renders correctly in a real MonoGame/KNI **WebGL** build via `new Effect(gd, bytes)`, **no server**, with the in-browser `.mgfx` bytes **identical to the CLI output** for the same source + OpenGL target (the faithful path guarantees this). The polished Fiddle app remains Phase 22; the headless render harness is Phase 24.

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
