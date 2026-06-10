# THE PURPOSE — full reference

> This is the long-form expansion of the **THE PURPOSE** summary in [`CLAUDE.md`](../CLAUDE.md).
> CLAUDE.md keeps the load-bearing one-liners always-in-context; this doc holds the detail,
> the success criteria, the evidence ladder, and the backend table. Read this when working on
> pipeline correctness, validation, or anything that touches the "identical to `mgfxc`" promise.

## The product

**The product is a drop-in `mgfxc` replacement: a self-contained library** a user adds to their **MonoGame/KNI project on Linux, macOS, or Windows**, that compiles **`.fx` → `.mgfx` in memory at runtime**, requiring **nothing but the library itself** — no `fxc.exe`, no `mgfxc`, no Wine, no Windows SDK, no native toolchain the user has to install separately. Its output **loads and renders identically to `mgfxc`'s** in the **real MonoGame/KNI runtime**. **One faithful compiler; the same `mgfxc`-equivalent result everywhere.**

The load-bearing distinctions — internalize these, they have drifted before:

- **The library *is* the product.** The deliverable is the in-memory compiler a developer references from their own game/app and calls at runtime (`IShaderCompiler.CompileAsync(fx) → .mgfx bytes`). The **CLI** (`dotnet tool` `ShadowDuskCLI`) and the **MGCB plugin** are *delivery shapes of the same library* for build-time use. The **browser / WASM shader-fiddle app is ONLY a sample / test of reach — never the product.** Do not let sample work redefine the goal.
- **One pipeline, everywhere — NO substitute compilers.** Every host (desktop, CLI, WASM/browser) runs the **same faithful pipeline**: HLSL →`[DXC]`→ SPIR-V →`[SPIRV-Cross]`→ GLSL →`[managed: reflect + MojoShader-dialect rewrite + MGFX writer]`→ `.mgfx` (or `vkd3d-shader` → DXBC for DirectX). A host **must not** swap in a *different* frontend/compiler (e.g. a different HLSL→SPIR-V tool) to make a platform "work" — a different compiler produces *different output* and silently breaks the "identical to `mgfxc`" promise. If a faithful component can't run on a host yet, that host's runtime-compile is **not done** — that is never a licence to substitute.
- **"Self-contained" is a hard requirement.** The user gets the NuGet package and it just works on their OS — the native pieces the pipeline needs ride *inside the package* (transitively, as native assets), never as a separate manual install. "Add the package, call the API" is the entire setup.
- **The bar is the real runtime, not our tests.** Only ShadowDusk's `.mgfx` loading in MonoGame's `Effect` and rendering like `mgfxc`'s proves the promise (see *What success actually means* below).

## Project Overview

ShadowDusk is a cross-platform HLSL shader compiler for MonoGame and KNI. Its five core purposes are:

1. **OS-agnostic compilation** — compile `.fx` shaders on Linux, macOS, or Windows with no Wine or Windows SDK required.
2. **DirectX and OpenGL targets** — produce DXBC (DirectX 11) or GLSL (OpenGL/WebGL) output from a single HLSL source.
3. **Drop-in `mgfxc` replacement** — transparent substitute for MonoGame's Windows-only `mgfxc` tool; same CLI flags, same `.mgfx` output, same exit codes and error format so existing content pipelines require zero changes.
4. **CLI tool** — `dotnet tool` named `ShadowDuskCLI`; usable standalone, from MGCB, or from any build script.
5. **In-memory & WASM-capable** — the same library compiles in-process / in-memory at runtime (returns `.mgfx` bytes; no temp files or child process required by the API), and is built to run inside .NET WASM so a KNI/Blazor browser game could compile shaders at runtime without a server roundtrip. **The in-browser shader-fiddle is a *sample* of this reach — not a separate product.**

## What success actually means

ShadowDusk earns its existence on **two** axes, and needs **both** — either one alone is worthless:

1. **Reach `mgfxc` can't (the differentiator).** Compile `.fx` where MonoGame's own toolchain cannot: on **Linux/macOS** (no Wine, no Windows SDK, no `fxc.exe`) and **at runtime, in-browser / in-memory** via WASM (e.g. XNA Fiddle). `mgfxc` already covers Windows-at-build-time, so matching it *only* there would be pointless — the reach is the reason to exist.
2. **Output `mgfxc` would (the fidelity).** The compiled `.mgfx`, loaded into the **real MonoGame/KNI runtime**, renders **the same image** as the `mgfxc`-compiled version — zero code or content-pipeline changes.

The product is the **combination**: *the same result `mgfxc` gives, produced where `mgfxc` can't run.* The two parts are validated differently — Part 1 (reach) by cross-platform CI (Phase 30) + the WASM/in-memory path, with all hosts producing byte-identical output to each other; Part 2 (fidelity) by the bar below. The ultimate combined proof is a shader compiled by ShadowDusk *on Linux or in a browser* rendering the same in-game image as `mgfxc`'s Windows build.

**Part 2's bar is in-engine behavioral equivalence:** a game whose `.fx` shaders are compiled with ShadowDusk instead of `mgfxc`, loaded into the **real MonoGame/KNI runtime** (`Effect`), renders **the same pixels** as the `mgfxc`-compiled version.

- The measure is *what the player sees in a real MonoGame game*, **not "ShadowDusk's own tests pass."** Unit tests, `.mgfx` structural tests, and images rendered by **ShadowDusk's own** renderer are necessary **proxies, not the bar** — a proxy can be green while the real goal is unmet. (This has happened: a GLSL cross-validation passed only because the test renderer was taught to bind uniform names the real runtime doesn't use.)
- **Evidence ladder, weakest → strongest:** (1) compiles without error → (2) `.mgfx` is structurally well-formed → (3) ShadowDusk's GLSL matches `mgfxc`'s GLSL *in our own renderer* → (4) **ShadowDusk's `.mgfx` loads in MonoGame's `Effect` and renders like `mgfxc`'s in the real runtime.** Only (4) proves the promise. (4) is **proven for the OpenGL SM3 PS-only corpus** (Phase 17, done 2026-05-30 — `plan/DONE/PHASE-17-monogame-runtime-validation.md`: all 10/10 shaders render pixel-equivalent in real MonoGame DesktopGL) **and for the DirectX SM5 PS-only corpus** (Phase 18, done 2026-05-30 — `plan/DONE/PHASE-18-directx-dxbc.md`: all 10/10 DX `.mgfx` load in real MonoGame WindowsDX and render pixel-equivalent to `mgfxc`, via **both** the Windows-only `d3dcompiler_47` oracle and the cross-platform **vkd3d-shader** backend — the latter being what makes DX DXBC compilable where `mgfxc` can't run). VS-driven effects remain (backlog 17-VS); Linux/macOS *run* validation of the vkd3d backend → Phase 30 CI.
- **Compare same-backend, never cross-backend.** Validation always compares ShadowDusk vs `mgfxc` on the *same* target (GL↔GL, DX↔DX) — never OpenGL output against DirectX output. The shader's *intent* (e.g. a 9-tap blur) is backend-agnostic, but each backend is a **separate emitted artifact** (OpenGL = GLSL text; DirectX = GPU bytecode) loaded by a **different** MonoGame runtime path. So a green OpenGL result says nothing about DirectX: a shipped game runs exactly one backend, and each must be produced correctly and validated on its own. "The blur is correct" ≠ "ShadowDusk emitted a valid, loadable artifact for *this* backend."
- **"Same `.mgfx` output" means behaviorally equivalent and `Effect`-loadable — NOT byte-identical to `mgfxc`.** Different compilers; byte-equality with `mgfxc` is neither expected nor a goal. "Byte-identical / deterministic" (Core Design Constraint 3) refers only to *ShadowDusk's own* reproducibility: same ShadowDusk version + same source + same target → same bytes.

## Backend pipeline table

MonoGame's stock content pipeline (`MGCB`) shells out to `mgfxc`, which depends on `fxc.exe` (DirectX SDK) on Windows. ShadowDusk replaces that pipeline step with a portable toolchain that transpiles and cross-compiles shaders for each supported MonoGame/KNI backend:

| Consumer runtime / backend | Shader Language | Compiler Target |
|---|---|---|
| DirectX (Windows) | HLSL | vkd3d-shader → DXBC (SM5) |
| OpenGL / DesktopGL | GLSL | DXC → SPIR-V → SPIRV-Cross → GLSL |
| Metal (macOS / iOS) *(not yet implemented)* | MSL | DXC → SPIR-V → SPIRV-Cross → MSL |
| Vulkan (future) | SPIR-V | DXC → SPIR-V (direct) |
| FNA *(Phase 39 — **rung 4 proven**, PS-only corpus; one `.fxb` serves all FNA backends)* | D3D9-style HLSL (SM ≤ 3) | vkd3d-shader → D3D9 bytecode → ShadowDusk `Fx2EffectWriter` → fx_2_0 (`.fxb`) |

> **FNA's bar (Phase 39).** For FNA the reference compiler is not `mgfxc` but Microsoft's
> `fxc.exe /T fx_2_0` (Windows-only, deprecated, FNA's blessed workflow runs it under Wine) —
> so the FNA analog of the promise is: ShadowDusk's `.fxb`, loaded by **real FNA**
> (`Effect` → FNA3D → MojoShader), renders **pixel-equivalent to the `fxc /T fx_2_0` build**,
> same-backend-compared. The evidence ladder mirrors the MonoGame one: (1) compiles → (2)
> structurally well-formed per MojoShader's parse rules + calibrated against real fxc goldens
> (`tests/fixtures/golden/FNA/`) → (3) the real MojoShader library parses+translates it → (4)
> real FNA renders pixel-equivalent. **All four rungs are proven for the SM3 PS-only corpus
> (2026-06-09, `validation/FnaValidation`: gate 10/10 + 12 extended entries, max delta ≤ 1/255
> vs the fxc oracle in real FNA 26.06)**; VS-driven FNA effects are the remaining 17-VS-style
> follow-up. `fxc`/`d3dcompiler_47` are test oracles only and never ship — the shipping path
> is vkd3d-shader's SM1–3 backend on every host, packed into the NuGet for win-x64 + linux-x64
> with cross-host byte-identical output. This is additive reach (Part 1); the
> mgfxc-replacement promise remains the primary product.

> **DirectX DXBC now works (Phase 18, done 2026-05-30).** DXC compiles to **DXIL (SM6)**, not the **DXBC (SM ≤ 5)** MonoGame 3.8's DX11 runtime loads — so the DX11 path no longer uses DXC. It routes through a DXBC backend behind `IDxbcShaderCompiler`: the cross-platform **vkd3d-shader** library (HLSL → DXBC_TPF) is the shipping backend, with Windows-only `d3dcompiler_47.dll` as a correctness oracle. DXC `ps_6_0`/`vs_6_0` (DXIL) is retained only for the DX12/KNI path. **Both OpenGL (Phase 17) and DirectX (Phase 18) are now validated end-to-end** in the real MonoGame runtime for the SM3/SM5 PS-only corpus (10/10 each); the DX backend's selector defaults to the oracle, with `DxbcBackend.Vkd3d` opt-in. WASM + DirectX DXBC remains the open problem (Phase 4.1).
