# ShadowDusk — Implementation Plan

This document is the top-level index. Each phase is fleshed out in its own document.

---

## THE PURPOSE (what every phase serves)

**The product is a drop-in `mgfxc` replacement: a self-contained library** a developer adds to a **MonoGame/KNI project on Linux, macOS, or Windows** that compiles **`.fx` → `.mgfx` in memory at runtime** with **nothing but the library** (no `fxc`, no Wine, no SDK), whose output **loads and renders identically to `mgfxc`'s in the real runtime**. **One faithful compiler — the same `mgfxc`-equivalent result everywhere.**

- The **library is the product**; the **CLI** and **MGCB plugin** are delivery shapes of it; the **browser / WASM shader-fiddle is only a sample of reach**, never the product.
- **No substitute compilers:** every host runs the *same* faithful pipeline (HLSL→DXC→SPIR-V→SPIRV-Cross→GLSL→MGFX, or vkd3d→DXBC). A host that can't yet run a faithful component is *not done* — never a licence to swap in a different compiler that diverges from `mgfxc`.
- Full statement + the success/evidence bar: see **`CLAUDE.md` → "THE PURPOSE" / "What success actually means"**.

Every phase below exists to serve that sentence. If a phase or sample starts redefining the goal, it has drifted — stop and re-anchor here.

---

## Reference Documents (background, not phase plans)

| Document | What it's good for | ⚠️ Read with |
|---|---|---|
| [`monogame_runtime_mgfx_compiler_research.md`](../monogame_runtime_mgfx_compiler_research.md) | Architecture survey of *how one builds a runtime MGFX compiler*: the `new Effect(gd, byte[])` loading path, `.xnb`-vs-raw-bytes, the DXC/SPIRV-Cross/MojoShader tool landscape, and a §0.1 map from its sections to these phases. | Its §0 alignment note — it was written greenfield and **understates** the MojoShader-GLSL-dialect blocker, which Phase 17 §3.6 proves is the real wall. Treat it as context, not a plan; Phases 7/8/17 supersede its roadmap. |

---

## Completed Phases

These phases are fully implemented. Their documents have been moved to `DONE/` with all checklist items ticked.

| Phase | File | Summary |
|-------|------|---------|
| 0 ✓ | [DONE/phase-0-setup.md](DONE/phase-0-setup.md) | Fixture corpus (39 .fx / 4 .fxh), golden .mgfx reference compilation, ShaderViewer sample — **DONE** |
| 1 ✓ | [DONE/PHASE-1-solution-scaffold.md](DONE/PHASE-1-solution-scaffold.md) | .NET solution structure, project references, NuGet dependencies, test framework — **DONE** |
| 2 ✓ | [DONE/PHASE-2-fx9-pre-parser.md](DONE/PHASE-2-fx9-pre-parser.md) | Custom parser: extract technique/pass/sampler_state/render-state blocks before DXC sees the file — **DONE** |
| 3 ✓ | [DONE/PHASE-3-preprocessor-macro-injection.md](DONE/PHASE-3-preprocessor-macro-injection.md) | #include flattening, platform macro injection (MGFX=1, GLSL=1, SM4=1, etc.) — **DONE** |
| 4 ✓ | [DONE/PHASE-4-dxc-integration.md](DONE/PHASE-4-dxc-integration.md) | Vortice.Dxc wiring, per-platform DXC flags, HLSL → SPIR-V compilation — **DONE** |
| 5 ✓ | [DONE/PHASE-5-shader-reflection.md](DONE/PHASE-5-shader-reflection.md) | Cross-platform parameter metadata extraction via IDxcUtils::CreateReflection and SPIRV-Cross — **DONE** |
| 6 ✓ | [DONE/PHASE-6-spirv-cross-glsl-transpilation.md](DONE/PHASE-6-spirv-cross-glsl-transpilation.md) | SPIRV-Cross C API P/Invoke, SPIR-V → GLSL/MSL, Y-flip, depth range, combined samplers — **DONE** |
| 7 ✓ | [DONE/PHASE-7-mgfx-binary-writer.md](DONE/PHASE-7-mgfx-binary-writer.md) | .mgfx binary format serialization: header, constant buffers, shaders, parameters, techniques, passes — **DONE** |
| 8 ✓ | [DONE/PHASE-8-compiler-library.md](DONE/PHASE-8-compiler-library.md) | `ShadowDusk.Compiler` NuGet library — `EffectCompiler : IShaderCompiler`, pipeline orchestration, the consumer-facing package — **DONE** |
| 9 ✓ | [DONE/PHASE-9-cli-entry-point.md](DONE/PHASE-9-cli-entry-point.md) | dotnet tool CLI, mgfxc-compatible flags, MGCB error format, stderr routing, exit codes — **DONE** |
| 15 ✓ | [DONE/PHASE-15-integration-tests.md](DONE/PHASE-15-integration-tests.md) | End-to-end .fx compilation tests — 9 fixtures × 3 platforms, determinism, error cases (103 tests, all passing) — **DONE** |
| 16 ✓ | [DONE/PHASE-16-image-regression-tests.md](DONE/PHASE-16-image-regression-tests.md) | Visual regression tests — offscreen OpenGL rendering of all 9 Phase 15 fixtures, 12 reference PNGs anchored on ShadowDusk's own output, 13 tests passing — **DONE** |

---

## Active & Planned Phases

*(Status per each phase doc's own header. "In progress" = currently being worked; "Planned" = written but not started.)*

| Phase | Status | File | Summary |
|-------|--------|------|---------|
| 17 | ✅ Done | [DONE/PHASE-17-monogame-runtime-validation.md](DONE/PHASE-17-monogame-runtime-validation.md) | In-engine equivalence **complete (2026-05-30)** for the full SM3 PS-only corpus (all 10/10, Dissolve incl.): ShadowDusk `.mgfx` loads in a real MonoGame `Effect` and renders pixel-equivalent to `mgfxc` (OpenGL) — the fidelity (Part 2) bar. Carried forward: DirectX → Phase 18, VS-driven effects → backlog 17-VS |
| 18 | ✅ Done | [DONE/PHASE-18-directx-dxbc.md](DONE/PHASE-18-directx-dxbc.md) | In-engine equivalence **complete (2026-05-30)** for the SM5 PS-only corpus (all 10/10): ShadowDusk's DX `.mgfx` loads in real MonoGame WindowsDX and renders pixel-equivalent to `mgfxc`, via the cross-platform **vkd3d-shader** backend (`d3dcompiler_47` oracle on Windows) — the DirectX half of fidelity. Carried forward: VS-driven → backlog 17-VS, Linux/macOS *run* validation → Phase 30 CI |
| 19 | ✅ Done | [DONE/PHASE-19-wasm-runtime-compilation.md](DONE/PHASE-19-wasm-runtime-compilation.md) | WASM compile **engine** (scope narrowed 2026-05-30; browser-runtime tail → Phase 100). Built & desktop-verified: injectable backend seams; a pure-managed `SpirvReflector` proven equivalent to the DXIL oracle (10/10), removing the Windows-only reflection blocker; the GL pipeline reflects SPIR-V and emits `.mgfx` **byte-identical** to the DXIL path (10/10); `WasmShaderCompiler` composes the pipeline with `[JSImport]` DXC/SPIRV-Cross backends and **compiles for `net8.0-browser`** |
| 21 | ✅ Done | [DONE/PHASE-21-test-suite-performance.md](DONE/PHASE-21-test-suite-performance.md) | Resolved the 21m43s `ShadowDusk.Integration.Tests` outlier. Root cause (structural fit): a per-construction `dotnet publish -c Release` in `CliBinaryFixture` — a cold Release build + AV-scan of freshly-copied native binaries. Fix: reuse the normal build's CLI binary (PR #2); suite back to seconds (128/128, ~6 s). Dev-time Defender exclusions documented in `CLAUDE.md`. Guardrail added 2026-05-31: `ShadowDusk.runsettings` 5-min `TestSessionTimeout` so a future hang fails fast |
| 22 | ✅ Done | [DONE/PHASE-22-wasm-shader-fiddle-sample.md](DONE/PHASE-22-wasm-shader-fiddle-sample.md) | KNI Blazor-WASM **sample app** (`samples/ShaderFiddle.Web/`, net8.0-browser): paste `.fx` → compile in-browser → `new Effect` → cat + shader + error UI — **complete (2026-06-02)**. Both modes ship on the **faithful product pipeline**: mode-1 loads the 10 Phase-17 goldens; **mode-2 compiles in-browser via the faithful DXC→WASM frontend** (`WasmShaderCompiler`, self-registered from the `ShadowDusk.Wasm` package) — the original Slang frontend is **superseded by Phase 23** and now dead sample-only reference. Both caveats resolved: faithful frontend (Phase 23, byte-identical to desktop DXC) + render proven 10/10 in real headless KNI WebGL (Phase 24; `MGFXReader10` parses v10, no KNIFX-v11 needed) + a live desktop-browser run. Documented **stretch implemented** (reflect params → "Live parameters" panel) + Reset button; `build` **and** `publish -c Release` clean with the 17 MB `dxcompiler.wasm` bundled. Carry-forward: untrusted input → Phase 25 (never a P22 criterion) |
| 23 | ✅ Done | [DONE/PHASE-23-in-browser-compilation.md](DONE/PHASE-23-in-browser-compilation.md) | **Faithful in-browser compile is the PRODUCT — complete (2026-06-02), all 3 gates met (adversarial review: closeable, 0 blockers).** Option A: the pinned **DXC→WASM** (== desktop DXC `e043f4a1`) is `ShadowDusk.Wasm`'s in-browser HLSL→SPIR-V frontend, args forwarded verbatim — **G0/G1 byte-identity 10/10**, **no Slang leak** (Slang sample-only). **Turnkey**: Razor-SDK static web assets + library self-registration → a scratch consumer compiles with **only a `PackageReference`**, zero wiring. **Render-proven rung-4**: faithful in-browser compile renders **10/10 in real headless KNI WebGL** (real `Effect`/`readPixels`/`Effect.Parameters`), mgfxc-equivalence transitive via Phase 17. Fixed the .NET-8-WASM-no-MD5 reach bug (`ManagedMd5` ≡ BCL MD5). Full suite **515/515**. Carry-forwards: DirectX-in-WASM→4.1, CI/Linux wasm-rebuild→30 §16, VS-path→17-VS, untrusted input→25 |
| 24 | ✅ Done | [DONE/PHASE-24-browser-render-validation.md](DONE/PHASE-24-browser-render-validation.md) | **Browser render validation (Playwright headless) — complete (2026-06-01).** First real-browser run. Mode-1: **10/10 load + 10/10 render-equivalent** in real KNI WebGL vs DesktopGL of the same bytes. MGFX **v10 renders in KNI WebGL → KNIFX-v11 NOT needed for render parity (carry-forward CLOSED).** The initial Dissolve failure was a sample-side unset slot-1 sampler state (not a compiler bug; fixed in the sample/harness). Harness (`tests/ShadowDusk.BrowserTests/`) handed to Phase 30 §16. **Follow-up finding now FIXED:** ShadowDusk's *own* GL output emitted `roundEven()` (WebGL1 lacks it) → `Pixelated` failed to load; lowered to `floor(x+0.5)`, harness extended to validate ShadowDusk's own bytes (`--corpus=sd`), now 10/10. Carried forward: mode-2 sample verification → Phase 23 Gate 3 (restore-gated `slang-wasm.wasm`) |
| 25 | Planned | [PHASE-25-security-hardening.md](PHASE-25-security-hardening.md) | Security hardening for the **untrusted-`.fx` library API** (any consumer, not just the fiddle) — path traversal, input validation, supply chain (incl. the `.wasm` artifacts) |
| 26 | Planned | [PHASE-26-documentation-site.md](PHASE-26-documentation-site.md) | **DocFX → GitHub Pages documentation site** at `shadowdusk.github.io/ShadowDusk/`: auto-generated managed API reference (from XML doc-comments) + how-to/examples/samples + conceptual/architecture pages, published by GitHub Actions. Bulk cost is writing `///` doc-comments (Core's consumer contract is ~80% undocumented). Reuses the existing accurate docs as one source of truth; excludes the Metal/MgcbPlugin stubs and the browser-TFM Wasm project from API metadata |
| 27 | Planned | [PHASE-27-pre-1.0-test-and-verification-sweep.md](PHASE-27-pre-1.0-test-and-verification-sweep.md) | **Pre-1.0 test & verification sweep** — close the deferred unit/integration coverage and manual pack/CLI verification items parked across Phases 2–9 & 15 so coverage isn't a 1.0 blind spot. Mostly "run the existing test file & tick the box" (DXC flags/diagnostics, DXC integration); genuine new tests: SD0101 binding-mismatch, `MgfxParameterMatch` golden snapshot, GLSL Y-flip, `FileSystemIncludeResolver` integration, `DxcIncludeHandler` smoke, CLI-process `[Theory]` parity, direct `ShaderIRBuilder` tests (needs `InternalsVisibleTo`), + manual CLI pack/install/publish (§9.4–9.6). Native-gated items skip cleanly; cross-platform *runs* → Phase 30. *(Promoted from Phase 100.)* |
| 28 | Planned | [PHASE-28-vs-driven-monogame-effects.md](PHASE-28-vs-driven-monogame-effects.md) | **VS-driven MonoGame effects** — extend faithful compile+render beyond the PS-only corpus to effects with their own vertex shader on the MonoGame GL path (symmetric `vs_uniforms_vec4` remap, VS-side attribute/varying I/O, finished PS/VS matrix expansion), validated pixel-equivalent vs `mgfxc` in real DesktopGL + confirmed on DX. *(Promoted from backlog 17-VS.)* |
| 29 | Planned | [PHASE-29-mgcb-content-processor-plugin.md](PHASE-29-mgcb-content-processor-plugin.md) | **MGCB Tier-2 content-processor plugin** — promote the `ShadowDusk.MgcbPlugin` stub to a real MonoGame `EffectImporter`/`EffectProcessor` so MGCB invokes ShadowDusk natively in-process (vs the Tier-1 PATH/`ExternalTool` drop-in), wrapping `EffectCompiler` to emit the same `.mgfx`; a convenience over Tier-1, not required for the core promise. *(Promoted from Phase 100.)* |
| 30 | Planned | [PHASE-30-cross-platform-ci.md](PHASE-30-cross-platform-ci.md) | RID matrix (SPIRV-Cross **+ vkd3d-shader**), native binary restore, GitHub Actions CI across Linux/macOS/Windows, **+ the WASM build & headless-browser smoke (§16)** that Phases 22/23/24/100 defer to it |
| 31 | Future | [PHASE-31-metal-msl-backend.md](PHASE-31-metal-msl-backend.md) | **Metal / MSL backend** — add the Metal Shading Language emission target (HLSL→DXC→SPIR-V→SPIRV-Cross **MSL**→MGFX, the GLSL branch's analogue; `SpvcBackend.Msl` already exists), replacing the empty `MslEmitter` stub and wiring `PlatformTarget.Metal` through the pipeline/CLI (today hard-rejected with `SD0200`). The hard part is the **validation surface**: no `mgfxc` MSL oracle and no MonoGame/KNI Metal runtime, so Metal ships **experimental/unvalidated** until a rung-4 render story lands — coordinated with Phase 30 macOS CI. *(Promoted from Phase 100.)* |
| 32 | Future (parked) | [PHASE-32-vulkan-backend.md](PHASE-32-vulkan-backend.md) | **Vulkan backend (SPIR-V target)** — wire the Vulkan SPIR-V `.mgfx` target end-to-end (DXC already emits SPIR-V; CLI/`PlatformTarget`/`MgfxProfile`/macros/flags already accept Vulkan; the real gap is routing Vulkan reflection through `SpirvReflector` instead of the empty-DXIL path). **Parked like 4.1**: no MonoGame/KNI Vulkan runtime and no `mgfxc`-Vulkan baseline, so "renders like `mgfxc`" is unreachable — ceiling is valid SPIR-V + well-formed `.mgfx`. *(Promoted from Phase 100.)* |
| 33 | Planned | [PHASE-33-webgl2-es300-hidef-output.md](PHASE-33-webgl2-es300-hidef-output.md) | **KNI HiDef / WebGL2 (GLSL ES 3.00) compatibility** — fixes issue [#7](https://github.com/kaltinril/ShadowDusk/issues/7) (Victor Chelaru / XnaFiddle): a ShadowDusk `.mgfx` loads in KNI **Reach** but **fails in KNI HiDef** (WebGL2/ES-3.00: `gl_FragColor undeclared`). **Root cause verified 2026-06-03 — a one-spot fidelity gap, not a missing target:** `mgfxc` emits the PS colour output as `#define ps_oC0 gl_FragColor`, and KNI's HiDef runtime auto-converts *that* form to ES 3.00; ShadowDusk inlines a *raw* `gl_FragColor` ([MonoGameGlslRewriter.cs:246-247](../src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs#L246-L247)) that the converter skips. Fix = emit `mgfxc`'s `#define` form → **one `.mgfx` works in Reach *and* HiDef, zero consumer input, no new flag/format, no KNI change**, strictly more `mgfxc`-faithful. *(Fidelity/reach track.)* |
| 4.1 | Spike (parked) | [PHASE-4.1-SPIKE-wasm-directx-dxbc.md](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) | Research spike: faithful **DirectX DXBC in WASM** (vkd3d-shader→emscripten). Far-future, after the OpenGL-in-WASM path (Phase 23); the DXBC analogue of Phase 23's DXC→WASM build. Server-relay option is **out of bounds** (no server roundtrip) |
| 100 | ✅ Retired | [PHASE-100-deferred-backlog.md](PHASE-100-deferred-backlog.md) | **Emptied & retired (2026-06-03).** Every item was promoted into a real phase: test/verification sweep → [27](PHASE-27-pre-1.0-test-and-verification-sweep.md), VS-driven effects → [28](PHASE-28-vs-driven-monogame-effects.md), MGCB Tier-2 plugin → [29](PHASE-29-mgcb-content-processor-plugin.md), Metal/MSL → [31](PHASE-31-metal-msl-backend.md), Vulkan → [32](PHASE-32-vulkan-backend.md) (the WASM browser-runtime tail had already moved to 23/24/30 §16). Kept only as a breadcrumb so old links resolve — the deferred bucket is no longer a parking lot |

### Roadmap tracks (open phases)

The remaining open phases group into four tracks (the numbers are stable IDs, not a sequence):

- **Release (→ v1.0):** [25](PHASE-25-security-hardening.md) security · [26](PHASE-26-documentation-site.md) docs site · [27](PHASE-27-pre-1.0-test-and-verification-sweep.md) test & verification sweep · [30](PHASE-30-cross-platform-ci.md) cross-platform CI. The path from "works here" to a trustworthy public 1.0.
- **Fidelity / completeness:** [28](PHASE-28-vs-driven-monogame-effects.md) VS-driven effects — compile the common custom-vertex-shader case, not just PS-only · [33](PHASE-33-webgl2-es300-hidef-output.md) KNI HiDef/WebGL2 — close the `gl_FragColor` fidelity gap so one `.mgfx` works in KNI Reach *and* HiDef.
- **Delivery shapes:** [29](PHASE-29-mgcb-content-processor-plugin.md) MGCB Tier-2 content-processor plugin — native MGCB integration beyond the Tier-1 drop-in.
- **Backend breadth (post-1.0, validation-gated):** [31](PHASE-31-metal-msl-backend.md) Metal/MSL · [32](PHASE-32-vulkan-backend.md) Vulkan (parked) · [4.1](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) DirectX-DXBC-in-WASM spike (parked) — each blocked on a real runtime to render-validate against.

[Phase 100](PHASE-100-deferred-backlog.md) is now retired/emptied — its items were promoted into the phases above. The deferred bucket is no longer a forever-parking-lot.

---

## Dependencies

```
Phase 1  (scaffold)
  └─ Phase 2  (FX9 parser)
  └─ Phase 3  (preprocessor)
       └─ Phase 4  (DXC integration)
            │    └─ Phase 4.1 (SPIKE: WASM + DirectX DXBC — parked, far-future)
            └─ Phase 5  (reflection)
            └─ Phase 6  (SPIRV-Cross transpilation)
                 └─ Phase 7  (binary writer)
                      └─ Phase 8  (ShadowDusk.Compiler library — EffectCompiler NuGet)
                           ├─ Phase 9  (CLI — ShadowDusk.Cli dotnet tool)
                           │    └─ Phase 15 (integration tests)
                           │         ├─ Phase 16 (image regression)
                           │         │    └─ Phase 17 (MonoGame runtime equivalence — OpenGL)
                           │         │         ├─ Phase 18 (DirectX DXBC — WindowsDX fidelity)
                           │         │         └─ Phase 19 (WASM runtime compilation — was "9W")
                           │         └─ Phase 30 (CI — desktop matrix + WASM/browser §16)
                           └─ Phase 19 (WASM — ShadowDusk.Wasm JS interop impl)
                                └─ Phase 22 (KNI Blazor-WASM SAMPLE app)
                                     └─ Phase 24 (browser render validation — Playwright; run FIRST)
                                          └─ Phase 23 (FAITHFUL in-browser compile — DXC→WASM, Option A)
```

> **The WASM-KNI product spine (the goal "a user uses our library inside WASM KNI"):** Phase 19 (engine) → Phase 22 (sample proves the shape) → **Phase 24** (real-browser render proof — retires the KNI MGFXReader10/v11 load risk; deliberately *before* the build effort) → **Phase 23** (faithful DXC→WASM frontend — the product). Phase 23's Gate 3 reuses Phase 24's harness; **Phase 30 §16** wires both into CI.
>
> Phase 19 intentionally appears twice (a diamond): it depends on **Phase 8** (the `IShaderCompiler` abstraction it implements) *and* on **Phase 17** (the MonoGame-loadable `.mgfx` format + MojoShader-dialect GLSL a browser-produced effect must also carry). Phases **23/24** (not the already-done Phase 19) additionally depend on **Phase 25** (untrusted-input security) and **Phase 30** (CI), which the graph keeps off the spine for readability — see each phase doc's "Depends on" line for the full set. Phase 19 supersedes the earlier "9W" placeholder.

## Key Decisions Already Made

- **Option B pipeline:** DXC → SPIR-V → SPIRV-Cross → GLSL/MSL. Option A (FXC/MojoShader) rejected — requires Wine on Linux/macOS.
- **DXC wrapper:** `Vortice.Dxc` NuGet package (bundles prebuilt native binaries for all platforms).
- **SPIRV-Cross binding:** Raw P/Invoke against the SPIRV-Cross C API (not Veldrid.SPIRV).
- **Default MGFXVersion:** `10` (MonoGame 3.8.2 stable). Version `11` is opt-in via flag.
- **Metal scope:** Out of scope until the OpenGL path is working and validated.
- **MGCB integration:** Tier 1 only (PATH-based drop-in binary named `mgfxc`). Tier 2 content processor plugin is a separate future undertaking.
- **Dual delivery targets:** CLI (`ShadowDusk.Cli`) and WASM library (`ShadowDusk.Wasm`). Output is always a `.mgfx` blob; `IShaderCompiler.CompileAsync` abstracts the difference. WASM implementation uses JS interop to call WASM-compiled DXC and SPIRV-Cross.
- **KNI compatibility:** KNI uses the same `.mgfx` format as MonoGame. No special output path needed for KNI — **with one caveat (Phase 33):** KNI's **HiDef** profile uses a WebGL2 / GLSL ES 3.00 context and relies on its runtime converter (`ConvertGLSLToGLSL300es`) rewriting `mgfxc`'s `#define ps_oC0 gl_FragColor` form to `out vec4`. ShadowDusk must emit that exact `#define` form (not a raw `gl_FragColor` write) for the same `.mgfx` to load in both Reach and HiDef. See [Phase 33](PHASE-33-webgl2-es300-hidef-output.md).

---

## ✅ Resolved Constraint (Phase 18): DXC Cannot Produce SM5 DXBC

Discovered during Phase 4 implementation. **DXC only produces SM6 DXIL** — it rejects `vs_5_0`/`ps_5_0` profiles with `error: invalid profile` for non-SPIRV targets. It has never supported DXBC (SM1–SM5) output. **Phase 18 (done 2026-05-30) resolved this** by routing the DX11 path through a DXBC backend behind `IDxbcShaderCompiler` — the cross-platform **vkd3d-shader** library (HLSL → DXBC_TPF), validated 10/10 in real MonoGame WindowsDX, with `d3dcompiler_47.dll` as a Windows-only correctness oracle. DXC's DXIL path is retained only for DX12/KNI. The historical analysis below is kept for context.

**Impact by target:**

| Target | Status | Notes |
|--------|--------|-------|
| OpenGL (SPIR-V path) | ✅ Unaffected | DXC compiles `vs_5_0 -spirv` fine; SPIRV-Cross handles the rest |
| Vulkan (SPIR-V path) | ✅ Unaffected | Same pipeline |
| DirectX 11 | ✅ Resolved (Phase 18) | DXC's SM6 DXIL won't load on D3D11; **Phase 18 routes DX11 through vkd3d-shader → SM5 DXBC** (`d3dcompiler_47` oracle on Windows). 10/10 load + render pixel-equivalent in real WindowsDX |
| DirectX 12 / KNI | ✅ Works | D3D12 natively accepts DXIL (SM6); `vs_6_0`/`ps_6_0` used in Phase 4 |

**SM6 DXIL on D3D11 is a hard no.** `ID3D11Device::CreateVertexShader` rejects DXIL unconditionally — even on Windows 10. DXIL is a D3D12-only format.

### Cross-Platform SM5 DXBC Options

The only viable cross-platform HLSL→DXBC compiler that does not require Wine:

**`vkd3d-shader`** (`gitlab.winehq.org/wine/vkd3d`)
- A standalone C library — **no Wine runtime required**. It is developed under the Wine project umbrella but ships and runs independently; linking against it is identical to linking SPIRV-Cross.
- Compiles HLSL to SM4/SM5 DXBC via a `vkd3d_shader_compile()` C API; P/Invoke pattern mirrors SPIRV-Cross
- Cross-platform: Linux, macOS, Windows. **No official prebuilt Windows DLL exists** (Phase 18 finding) — we build `libvkd3d-shader` from WineHQ source (vkd3d-1.17 via MSYS2/autotools, self-contained, zero non-system deps) and host per-RID; Linux/macOS have distro/source builds. `tools/restore.*` carries the recipe.
- License: **LGPL-2.1+** — safe to use as a dynamically-linked native binary (same model as SPIRV-Cross)
- Active development; v2.0 released May 2026; adopted by SDL3's `SDL_shadercross` as its non-Windows DXBC backend (no Wine involved there either)
- Coverage for MonoGame-style effects (cbuffers, Texture2D samplers, VS/PS only): sufficient
- Known gaps: SM5 UAV buffers (RWBuffer) and tessellation stages are partial; irrelevant for basic MonoGame effects
- **Not byte-identical to `fxc.exe` output**, but semantically equivalent; on Windows `d3dcompiler_47.dll` (ships with Windows) can serve as a fidelity fallback

**Recommended path for DirectX SM5 support (Phase 4.1):**

No end-user installation required on any platform:

| Platform | Library | How it's delivered |
|----------|---------|-------------------|
| Windows | `libvkd3d-shader.dll` (shipping) + `d3dcompiler_47.dll` (oracle) | vkd3d built/hosted per-RID (the cross-platform shipping backend on every OS); `d3dcompiler_47.dll` ships with Windows and is used only as the correctness oracle |
| Linux | `libvkd3d-shader.so` | Downloaded by `tools/restore.sh`, bundled into `dotnet publish` output |
| macOS | `libvkd3d-shader.dylib` | Same as Linux |

This mirrors exactly how SPIRV-Cross is distributed today. Users install ShadowDusk and nothing else.

> **WASM delivery target: DirectX DXBC is an open problem.** Native P/Invoke is unavailable in the browser, so neither `d3dcompiler_47.dll` nor `libvkd3d-shader` can be called directly from .NET WASM. No prebuilt WASM artifact of vkd3d-shader currently exists. The path forward requires one of: (a) compiling vkd3d-shader to WASM via emscripten and calling it via `[JSImport]` (same pattern as WASM-compiled DXC), or (b) a server-side compilation relay for DXBC. This is unresolved — see [PHASE-4.1-SPIKE-wasm-directx-dxbc.md](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) for the full problem statement and candidate solutions.

**As of Phase 18 (done 2026-05-30), the `PlatformTarget.DirectX` DX11 profile compiles to SM5 DXBC via vkd3d-shader** and loads/renders in real MonoGame WindowsDX (10/10; `d3dcompiler_47` oracle on Windows). DXC's SM6 DXIL path is retained only for DX12/KNI. The OpenGL path (Phase 17) is fully functional. **WASM + DXBC remains the open problem** (Phase 4.1) — no native P/Invoke in the browser.
