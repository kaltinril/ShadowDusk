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
- **Evidence ladder, weakest → strongest:** (1) compiles without error → (2) `.mgfx` is structurally well-formed → (3) ShadowDusk's GLSL matches `mgfxc`'s GLSL *in our own renderer* → (4) **ShadowDusk's `.mgfx` loads in MonoGame's `Effect` and renders like `mgfxc`'s in the real runtime.** Only (4) proves the promise. (4) is **proven for the OpenGL SM3 PS-only corpus** (Phase 17, done 2026-05-30 — `plan/DONE/PHASE-17-monogame-runtime-validation.md`: all 10/10 shaders render pixel-equivalent in real MonoGame DesktopGL) **and for the DirectX SM5 PS-only corpus** (Phase 18, done 2026-05-30 — `plan/DONE/PHASE-18-directx-dxbc.md`: all 10/10 DX `.mgfx` load in real MonoGame WindowsDX and render pixel-equivalent to `mgfxc`, via **both** the Windows-only `d3dcompiler_47` oracle and the cross-platform **vkd3d-shader** backend — the latter being what makes DX DXBC compilable where `mgfxc` can't run). VS-driven effects are also proven (Phase 28, done 2026-06-05 — `plan/DONE/PHASE-28-vs-driven-monogame-effects.md`: rung-4 max-delta-0 vs `mgfxc` in real DesktopGL **and** WindowsDX); Linux/macOS *run* validation of the vkd3d backend → Phase 30 CI.
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
| FNA *(Phase 39 — **rung 4 proven**, PS-only + VS-driven corpora; one `.fxb` serves all FNA backends)* | D3D9-style HLSL (SM ≤ 3) | vkd3d-shader → D3D9 bytecode → ShadowDusk `Fx2EffectWriter` → fx_2_0 (`.fxb`) |

> **FNA's bar (Phase 39).** For FNA the reference compiler is not `mgfxc` but Microsoft's
> `fxc.exe /T fx_2_0` (Windows-only, deprecated, FNA's blessed workflow runs it under Wine) —
> so the FNA analog of the promise is: ShadowDusk's `.fxb`, loaded by **real FNA**
> (`Effect` → FNA3D → MojoShader), renders **pixel-equivalent to the `fxc /T fx_2_0` build**,
> same-backend-compared. The evidence ladder mirrors the MonoGame one: (1) compiles → (2)
> structurally well-formed per MojoShader's parse rules + calibrated against real fxc goldens
> (`tests/fixtures/golden/FNA/`) → (3) the real MojoShader library parses+translates it → (4)
> real FNA renders pixel-equivalent. **All four rungs are proven for the SM3 PS-only AND
> VS-driven corpora (2026-06-09, `validation/FnaValidation`: gate 17/17 — 13 PS-only rows
> via the SpriteBatch scene incl. multi-technique selection and the float4x4 calibration
> row, + 4 VS-driven effects via the custom-geometry quad scene — plus the extended
> entries, max delta ≤ 1/255 vs the fxc oracle in real FNA 26.06; in-pass render states
> empirically honored; Phase 40 hardened the fidelity surface — see
> `plan/DONE/PHASE-40-fna-fidelity-hardening.md`)**. `fxc`/`d3dcompiler_47` are test oracles only and never ship — the shipping path
> is vkd3d-shader's SM1–3 backend on every host, packed into the NuGet for win-x64 + linux-x64
> with cross-host byte-identical output. This is additive reach (Part 1); the
> mgfxc-replacement promise remains the primary product.

## Compiler-leverage strategy — what we own, and what we never will

*(Made explicit 2026-06-09 at owner direction: "we don't want to become the expert in
Vulkan/D3D/OpenGL compilation if we don't have to — leverage others' work." This section
records that this is, deliberately, already the architecture — and why the line sits
where it does, so future work doesn't drift across it.)*

**The division of labor:**

| Layer | Owner | Why |
|---|---|---|
| HLSL parsing & compilation | **upstream** — Microsoft DXC; Wine vkd3d-shader (SM5 DXBC, SM1–3) | multi-year compiler engineering; actively maintained by the people who own the formats |
| Cross-compilation (SPIR-V → GLSL/MSL) | **upstream** — Khronos SPIRV-Cross | same |
| Container formats (`.mgfx`, fx_2_0 `.fxb`) | **ShadowDusk** | MonoGame/FNA-proprietary; no upstream will ever provide them — this *is* the product's reason to exist |
| Runtime-dialect adapters (MonoGame-GL rewrite, MojoShader-compat patches) | **ShadowDusk** | the consumer runtimes' undocumented contracts; nobody else targets them |
| **Validation against real runtimes** (the evidence ladder, rung-4 harnesses) | **ShadowDusk** | **this is the actual moat** — it is what lets us *trust* upstream compilers instead of becoming them |

**The rules that keep the line honest:**

- **Never fork or own compiler internals.** When upstream has a gap (vkd3d's int-ternary,
  MojoShader's printFloat LLP64 bug), the responses are, in order: fail loudly with a clear
  diagnostic; patch *minimally and surgically* on our side of the boundary (the
  `D3d9BytecodePatcher` pattern — byte-level-tested, documented, reversible); record an
  upstream-fix follow-up. Never "just handle it in our compiler" — we don't have one.
- **Pin versions; bumps are deliberate events** (vkd3d stays at 1.17 because output
  byte-stability is a product promise — a bump re-baselines goldens and re-runs rung-4).
- **One faithful pipeline, no substitute compilers** (the standing rule): leverage only
  works if every host runs the *same* upstream components — which is also exactly what
  makes cross-host byte-identity achievable.
- Parked backends (Metal, Vulkan) stay parked **not** because the compilation is hard —
  SPIRV-Cross/DXC already do it — but because there is no consumer runtime to
  rung-4-validate against yet. When one exists, the work is container plumbing +
  validation, not compiler expertise. That's the strategy working as intended.

## Host × target matrix, and the browser-export principle

**Compile-target and render-backend are independent.** `CompilerOptions.Target` is an
explicit parameter on every host; nothing limits a host to emitting "its own" format
except which upstream compilers have been built for that host. This matters most in the
browser: a website built on `ShadowDusk.Wasm` is not just a "compile GL for WebGL" tool —
it is (by owner direction, 2026-06-09) intended as a **full export station**: users upload
`.fx` and download compiled artifacts for *any* supported consumer (MonoGame GL/DX, FNA
`.fxb`, …), with the host-appropriate default and an explicit override. The override is
the *allowed* kind of choice (picking a platform the user's game targets — per
`seamless-for-end-user`), and the in-browser artifact must be **byte-identical** to the
desktop-compiled one (proven for GL — the G1 gate — and, since Phase 4.1 (2026-06-12),
for DX and FNA via the real-browser byte-identity gate; the bar for every future host).

Where each host×target cell stands (updated 2026-06-12, post-Phase 37 A/B/C + Phase 18 Track A + Phase 4.1):

| Emit ↓ / Host → | Windows | Linux | macOS | Browser (WASM) |
|---|---|---|---|---|
| OpenGL `.mgfx` | ✅ proven | ✅ compiles green in CI (37 B fixed the Vortice `wchar_t` marshalling; the browser-smoke renders its corpus on llvmpipe); rung-4 render-vs-`mgfxc` still Windows-proven only | ✅ compiles green in CI (37 A dylib + the 37 B fix; full integration suite passes); byte-identity-vs-win assertion + rung-4 render are the remaining tail | ✅ proven, byte-identical |
| DX11 DXBC `.mgfx` | ✅ proven | ✅ compiles end-to-end (vkd3d backend + managed `RdefReader` reflection — Track A); render bar is the real WindowsDX runtime, Windows-only by nature | ✅ same as Linux (DX11 no longer constructs DXC, so 37 A doesn't gate it) | ✅ **export target** — vkd3d→WASM landed (Phase 4.1, 2026-06-12): real-browser gate 65-artifact byte-identity vs the cross-host manifest; render bar stays desktop-by-nature (no Direct3D in a browser) |
| FNA fx_2_0 `.fxb` | ✅ proven | ✅ proven | ✅ natives ship + compile suite green in CI (37 C); render oracle (`fxc`) is Windows-only by nature | ✅ **export target** — same Phase 4.1 vkd3d→WASM module, same 2026-06-12 real-browser byte-identity proof (no D3D9 in a browser) |
| Vulkan SPIR-V / Metal MSL | parked — no validatable consumer runtime yet (Phases 31/32) | | | |

> Phase 37 C (2026-06-10) hosted all four pinned vkd3d 1.17 per-RID binaries and made
> `tools/restore.*` provision them everywhere — which also surfaced (via CI) that the
> earlier "DX11 on Linux ✅" claim was overstated: vkd3d produced the DXBC fine, but the
> `.mgfx` pipeline's reflection step still P/Invoked Windows-only `D3DReflect`
> (`DxbcReflectionExtractor`). **Phase 18 Track A (2026-06-10) closed that gap**: DXBC
> reflection is now the pure-managed `RdefReader` (ShadowDusk.Core — the CtabReader
> sibling), proven **deeply equal to the D3DReflect oracle** for both d3dcompiler_47 and
> vkd3d DXBC (`DxbcReflectionParityTests`) with **zero `.mgfx` byte change** (full-corpus
> A/B, DX + GL). The Linux/macOS DX11 cells above claim *compile* reach (rungs 1–2 +
> oracle-parity reflection); the rung-4 render claim stays Windows-proven only — DXBC
> renders only in a real WindowsDX runtime.

Every gap above was a **packaging/porting gap, never a compiler-writing gap** — and one
artifact, **vkd3d-shader compiled to WASM (Phase 4.1, ✅ done 2026-06-12)**, closed the
entire browser column: the fx_2_0 writer, bytecode patcher, and reflection are managed C#
that already ran in WASM, so vkd3d.wasm unlocked **both** DX and FNA export in the browser
from the same pinned 1.17 source (no substitute compiler). The evidence: the node gate
replays all 98 vkd3d stage compiles of the byte-identity corpus through the product shim,
98/98 byte-identical to the desktop native; a real headless browser running the real
`WasmShaderCompiler` reproduces all 65 full artifacts (37 DX `.mgfx` + 28 FNA `.fxb`)
SHA-256-identical to the committed cross-host manifest — so render-equivalence transfers
from the desktop rung-4 proofs by transitivity (`plan/PHASE-4.1-SPIKE-wasm-directx-dxbc.md`).
DX/FNA in the browser are **export targets** — a browser cannot render DXBC/D3D9 bytecode,
by construction, so the browser-side bar is byte-identity, honestly stated. Of the old
completion order, only **(4)** remains: Vulkan/Metal stay validation-gated; the desktop GL
column's remaining tail is the Linux/macOS rung-4 render items noted in the matrix.

> **DirectX DXBC now works (Phase 18, done 2026-05-30).** DXC compiles to **DXIL (SM6)**, not the **DXBC (SM ≤ 5)** MonoGame 3.8's DX11 runtime loads — so the DX11 path no longer uses DXC. It routes through a DXBC backend behind `IDxbcShaderCompiler`: the cross-platform **vkd3d-shader** library (HLSL → DXBC_TPF) is the shipping backend, with Windows-only `d3dcompiler_47.dll` as a correctness oracle. DXC `ps_6_0`/`vs_6_0` (DXIL) is retained only for the DX12/KNI path. **Both OpenGL (Phase 17) and DirectX (Phase 18) are now validated end-to-end** in the real MonoGame runtime for the SM3/SM5 PS-only corpus (10/10 each); the DX backend's selector defaults to the oracle, with `DxbcBackend.Vkd3d` opt-in. WASM + DirectX DXBC, long the open problem, closed 2026-06-12: the same pinned vkd3d 1.17 compiled to WASM ships in `ShadowDusk.Wasm`, byte-identical to desktop (Phase 4.1).
