# Phase 42 — `InitializeAsync()` + synchronous `Compile()` (issue #28)

**Status:** ✅ **Done (2026-06-12)** — PR #58 merged with **all CI green**: 3-OS Build & Test + Integration + Pack & Consume + WASM & Browser (including the cold-SD1903 + warm-sync 65/65 browser gate). Issue #28 closed.

**Track:** Reach (Part 1 of THE PURPOSE — in-memory runtime compilation) + consumer API. This is the **linchpin dependency for `vchelaru/XnaFiddle#39`** (Victor Chelaru's runtime shader compilation in exported KNI/Blazor projects): without it, a synchronous host call site (`Content.Load<Effect>`) cannot use the compiler on single-threaded browser WASM at all.

---

## TL;DR

`IShaderCompiler` gains two additive members: a one-time, idempotent **`Task InitializeAsync(CancellationToken)`** and a **synchronous `Result<CompiledShader, ShaderError[]> Compile(string, CompilerOptions, CancellationToken)`** that mirrors `CompileAsync`. The pipeline core (`CompilationPipeline.Run`) is now **synchronous end-to-end**; `CompileAsync` everywhere is a thin shell over that one core — **never a second pipeline** — so sync and async output is byte-identical by construction (and asserted anyway, full corpus, three targets). On WASM, `InitializeAsync` eagerly warms **all three** modules (DXC + SPIRV-Cross + vkd3d); a sync `Compile` before initialization returns the clear **SD1903** error, never an opaque abort.

## The why (the issue, quoted)

Filed by **vchelaru** (issue #28), whose analysis was verified against the codebase and holds on every path, including the post-issue Phase 4.1 vkd3d-WASM backend:

> **shader compilation is already synchronous in this codebase** — the only genuinely-async work is the one-time WASM module load. So the async surface can be split into a one-time `InitializeAsync()` and a synchronous `Compile()`, with **no second pipeline implementation** and therefore no risk to byte-identity.

> **Blazor WASM:** blocking **deadlocks**. WASM runs on the single browser thread; the compile path crosses into JS via `[JSImport]` and awaits a JS `Promise`, which can only resolve when control returns to the browser event loop. Blocking the one thread to wait on that Promise means it never resolves — classic sync-over-async deadlock.

> Given this repo's prime directive ("one faithful pipeline," deterministic byte-identical output), the implementation must **not** fork `RunAsync` into a parallel sync copy — two implementations would drift and break the identical-output promise.

His "clean scope" recommendation (the whole pipeline core sync — all targets, all backends, desktop included) is what was built.

### Key-finding re-verification (including what postdates the issue)

| Stage | Sync already? | Evidence |
|---|---|---|
| `FxPreParser` / `Preprocessor` / `RenderStateParser` / `ShaderIRBuilder` / `MgfxWriter` / `Fx2EffectWriter` / `D3d9BytecodePatcher` / `CtabReader` | yes | pure managed code, no awaits |
| Desktop DXC (`DxcShaderCompiler`) | yes | `CompileAsync` was literally `Task.Run(() => CompileCore(request))` |
| Desktop vkd3d (`Vkd3dShaderCompiler`) | yes | same `Task.Run(CompileCore)` shape |
| Desktop d3dcompiler_47 (`D3DCompilerShaderCompiler`) | yes | same shape (+ sync guard short-circuits) |
| Reflection (`ReflectionPipeline`, `DxbcReflectionPipeline`) | yes | `Task.Run(() => _extractor.Extract(...))` over sync extractors; `SpirvReflector.Reflect` already sync |
| SPIRV-Cross transpile (desktop + WASM) | yes | `ISpirvToGlslTranspiler.Transpile` is a sync interface; the WASM `transpileToGlsl` `[JSImport]` is synchronous (the module instantiates eagerly at registration) |
| WASM DXC `compileToSpirv` | yes, post-init | `shadowdusk-dxc.js` — synchronous export; only `ensureReady()` is a Promise |
| **WASM vkd3d `compile` (Phase 4.1 — postdates the issue)** | **yes, post-init** | `shadowdusk-vkd3d.js` — `compile(...)` is a synchronous export over the `sdw_vkd3d_compile` C ABI; only `ensureReady()` is a Promise. The issue's finding **extends cleanly** to the third module. |

No genuinely-async per-compile step exists anywhere; nothing had to be scoped out.

## Design (as built)

### Public API surface (exact signatures)

```csharp
// ShadowDusk.Core — IShaderCompiler (additive members)
Task InitializeAsync(CancellationToken cancellationToken = default);
Result<CompiledShader, ShaderError[]> Compile(
    string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default);

// ShadowDusk.HLSL — IDxcShaderCompiler / IDxbcShaderCompiler (additive members)
Result<PlatformBlob, ShaderError> Compile(
    DxcCompileRequest request, CancellationToken cancellationToken = default);
Result<PlatformBlob, ShaderError> Compile(
    D3DCompileRequest request, CancellationToken cancellationToken = default);

// ShadowDusk.HLSL — ReflectionPipeline / DxbcReflectionPipeline (additive members)
Result<ReflectedEffect, ShaderError> Reflect(ReflectionInput input, CancellationToken ct = default);
Result<ReflectedEffect, ShaderError> Reflect(
    ReadOnlyMemory<byte> dxbcBlob, IReadOnlyList<ParameterAnnotation>? fxAnnotations, CancellationToken ct = default);
```

The consumer pattern (the whole point of the issue):

```csharp
await compiler.InitializeAsync();                       // once, from an async context
var result = compiler.Compile(fxSource, options);       // inside Content.Load<Effect> — no Task, no deadlock
var effect = new Effect(graphicsDevice, result.Value.Data);
```

### One pipeline core

- `CompilationPipeline.RunAsync` → **`Run` (synchronous)**; `RunFnaAsync` → `RunFna`; the per-entry-point and per-FNA-stage helpers likewise. Every backend call inside is the new sync `Compile`/`Reflect`.
- **Desktop backends:** sync `Compile` calls the pre-existing `CompileCore` directly; `CompileAsync` keeps its exact `Task.Run(CompileCore)` shape (thread-offload preserved).
- **`EffectCompiler.CompileAsync`** = `Task.Run(() => Compile(...))` — the whole compile offloads to the thread pool (previously only the backend stages did; parse/preprocess ran on the caller's thread). `Compile` = `pipeline.Run(...)` inline. `InitializeAsync` = documented no-op (desktop natives load on first use, synchronously).
- **WASM backends** (`JsDxcShaderCompiler`, `WasmVkd3dShaderCompiler`): sync `Compile` calls the post-init synchronous `[JSImport]` directly, gated on a managed readiness flag (`WasmCompilerInitialization`) — **the flag is load-bearing**: a synchronous `[JSImport]` into an unregistered module aborts the .NET WASM runtime, so the C# side must know readiness without calling JS. Cold call → **SD1903**, the clear "await `InitializeAsync()` first" error (issue acceptance criterion).
- **`WasmShaderCompiler.CompileAsync`** = run the sync core; if (and only if) it failed with SD1903, do the one-time per-target module load (`EnsureDxcReadyAsync` for GL/Vulkan, `EnsureVkd3dReadyAsync` for DX/FNA — the same lazy policy as before, so the 17.4 MB DXC download still never burdens page init **and** a parse error still surfaces without forcing a module download), then run the same sync core once more. Load failures map through the same shared helpers as before (SD1900-family for DXC, SD1902 for vkd3d) — behavior-compatible for existing consumers.

### `InitializeAsync` warming policy (WASM): eager-everything

`WasmShaderCompiler.InitializeAsync` warms **module registration + DXC + SPIRV-Cross + vkd3d** — everything any target's sync `Compile` needs. Rationale (the seamless rule): after one `await InitializeAsync()`, `Compile` of **any** target just works; the consumer never learns which target needs which module. The vkd3d module is 432 KB — negligible next to the 17.4 MB DXC, so per-target laziness in `InitializeAsync` would complicate the contract to save nothing. (`CompileAsync` keeps per-target lazy loading — eager loading belongs only to the explicit warm-up call.) A module that cannot load throws `InvalidOperationException` naming the module and the fix (fail loudly; in the shipped package all three modules are always present as static web assets).

### Back-compat notes

- All additions are **additive for callers**; `CompileAsync` behavior is unchanged for the CLI, MGCB usage, tests, and the sample (one nuance: a WASM module-load failure now surfaces after a first sync attempt — same `ShaderError` codes/messages as before).
- The three public interfaces gained members — a source-level change for **implementors** only; all in-repo implementations (5 backends + 2 test fakes) were updated. No external implementors exist pre-1.0.
- `CLAUDE.md`'s "async all the way down" rule targets **child-process** invocations (the CLI) and still holds there; the sync path never blocks on a task anywhere (grep-proven below).

## As-built file map

| Area | Files |
|---|---|
| API surface | `src/ShadowDusk.Core/IShaderCompiler.cs`, `src/ShadowDusk.Compiler/EffectCompiler.cs` |
| Pipeline core | `src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs` (`Run`/`RunFna`, fully sync) |
| Desktop backends | `src/ShadowDusk.HLSL/Dxc/{IDxcShaderCompiler,DxcShaderCompiler}.cs`, `Vkd3d/Vkd3dShaderCompiler.cs`, `D3DCompiler/{IDxbcShaderCompiler,D3DCompilerShaderCompiler}.cs` |
| Reflection | `src/ShadowDusk.HLSL/Reflection/{ReflectionPipeline,DxbcReflectionPipeline}.cs` |
| WASM | `src/ShadowDusk.Wasm/{WasmCompilerInitialization (new),WasmShaderCompiler,JsShaderBackends,WasmVkd3dShaderCompiler}.cs` |
| Tests | `tests/ShadowDusk.Integration.Tests/Tests/SyncCompileByteIdentityTests.cs` (new), `tests/ShadowDusk.Compiler.Tests/{SyncCompileApiTests (new),DxbcCompilerInjectionTests}.cs`, `tests/ShadowDusk.BrowserTests/Vkd3dCorpusProbe/Program.cs` |
| Browser proof | `samples/ShaderFiddle.Web/Pages/Index.razor.cs` (`TestSyncCompileExport` + `TestInitializeCompiler` JSInvokable hooks), `tests/ShadowDusk.BrowserTests/browser-vkd3d-gate.mjs` (Phase 42 section) |

## Gates (the issue's acceptance criteria + the standing suites)

| Gate | Result |
|---|---|
| **Byte-identity, sync vs async, full corpus, per target** — `SyncCompileByteIdentityTests`: OpenGL (37 fixtures), DirectX/vkd3d (37), FNA (28), + a d3dcompiler_47 oracle row (Windows) | ✅ pass (in suite below) |
| Existing suites incl. `CrossHostByteIdentityTests` (the committed manifest is untouched — output bytes did not change) | ✅ `dotnet test ShadowDusk.slnx`: **933 passed / 0 failed / 0 skipped** (win-x64, including the 12 new Phase-42 tests) |
| Node vkd3d gate: `node node-test-vkd3d-wasm.mjs` | ✅ **98/98** byte-identical |
| Real-browser gate (`browser-vkd3d-gate.mjs`, extended): (a) **cold sync `Compile` → SD1903** on DirectX, OpenGL, and Fna; (b) `InitializeAsync` OK, awaited twice (idempotent); (c) warm **synchronous** `Compile` over the full DX+FNA manifest corpus byte-identical to the committed manifest; existing async 65/65 unchanged | ✅ cold SD1903 3/3, init OK, sync 65/65, async 65/65 (see `RESULTS-VKD3D-BROWSER.md`) |
| Grep-proof: no `.Result` / `.Wait()` / `GetAwaiter().GetResult()` anywhere under `src/` | ✅ only doc-comment mentions and the unrelated `SharpGen.Runtime.Result` type |
| `///` doc-comments on all new public API (DocFX site auto-generates) | ✅ |

## Honesty notes / non-goals

- The browser cold-check runs **before** any compile in the session by construction (lazy loading means any compile would warm the modules and mask it) — the gate enforces the order.
- `EffectCompiler.CompileAsync` now offloads the *entire* compile via `Task.Run` (previously the parse/preprocess prefix ran on the caller's thread before the first backend await). Sanctioned: the issue's design says "desktop: `Task.Run(() => Run(...))` (preserve the thread offload)"; observable behavior (results, errors, bytes) is identical.
- The issue's "real-runtime bar" (an exported KNI project compiling via `Compile` inside `Content.Load<Effect>` and rendering) is the downstream XnaFiddle#39 integration; the browser gate proves the exact bytes that path will produce (sync browser bytes == committed manifest == desktop rung-4 render-proven bytes — the Phase 4.1 transitivity argument), and `Effect`-loadability of those same bytes is already render-proven (Phases 17/18/24/39–40).
