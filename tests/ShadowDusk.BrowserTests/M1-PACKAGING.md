# Phase 23 M1 — turnkey packaging (DONE)

**Goal (DoD):** a third-party dev adds **only a `PackageReference` to `ShadowDusk.Wasm`**
to their KNI/Blazor `net8.0-browser` app and compiles `.fx` → `.mgfx` in-browser via the
FAITHFUL DXC→WASM pipeline — with **ZERO** `wwwroot`/`JSHost` wiring of their own. The
native JS+wasm modules ride **inside the package** as Blazor static web assets (served at
`_content/ShadowDusk.Wasm/…`) and `ShadowDusk.Wasm` **self-registers** them.

**Result: all gates pass. The DoD is proven end-to-end — including a real headless KNI
WebGL run that compiles + renders 10/10 corpus shaders through the package alone, with the
sample's hand-wiring deleted. No blocker.**

---

## Packaging approach

### Razor SDK → static web assets

`src/ShadowDusk.Wasm/ShadowDusk.Wasm.csproj` switched from `Microsoft.NET.Sdk` to
**`Microsoft.NET.Sdk.Razor`** (TFM unchanged: `net8.0-browser`). The Razor SDK treats
everything under `wwwroot/` as **Blazor static web assets**: on `pack` they ship inside the
`.nupkg` under `staticwebassets/`, and a consuming Blazor/KNI app auto-serves them at
`_content/ShadowDusk.Wasm/…` via the package's generated
`build/Microsoft.AspNetCore.StaticWebAssets.props` — **no consumer wiring**.

Packaging metadata mirrors `ShadowDusk.Compiler.csproj` / `ShadowDusk.Core.csproj`:
`PackageId=ShadowDusk.Wasm`, `PackageVersion=0.1.0`, `PackageLicenseExpression=MIT`,
`Description`, `PackageTags`. A `VerifyDxcWasmPresent` MSBuild target fails the build loudly
(before `ResolveStaticWebAssetsInputs`/`BeforeBuild`/`Pack`) if the gitignored
`wwwroot/dxc/dxcompiler.wasm` is missing, so a `.nupkg` can never silently ship without the
faithful frontend's wasm.

### Self-registration mechanism (the zero-wiring core) — chosen + why

`src/ShadowDusk.Wasm/WasmModuleRegistration.cs` — a static `EnsureRegisteredAsync()` that
**lazily, idempotently** `JSHost.ImportAsync`-registers BOTH `[JSImport]` modules from the
package's own static web assets. It is awaited at the top of
`JsDxcShaderCompiler.CompileCoreAsync` (before the first `[JSImport]` call), so a consumer
just calls `WasmShaderCompiler.CompileAsync(...)` and wires nothing.

**URL resolution — the load-bearing detail.** `JSHost.ImportAsync` resolves a *relative*
module URL against the WASM runtime's `_framework/` folder, which is always a direct child
of the app base. So the registrar imports against:

```
../_content/ShadowDusk.Wasm/shadowdusk-dxc.js
../_content/ShadowDusk.Wasm/shadowdusk-spirv-cross.js
```

The `..` climbs out of `_framework/` to the app base, then into `_content/ShadowDusk.Wasm/`
(where the package serves its wwwroot). This is **sub-path-safe** (works whether the app is
at the site root or under a sub-path) and needs **no** `document.baseURI` read and **no**
consumer-supplied `NavigationManager` — the alternative `{Nav.BaseUri}…` form the sample
used (now deleted) required the consumer to inject `NavigationManager`.

**Why register inside the compile path (vs a Blazor JS initializer):** the consumer's only
entry point is `CompileAsync`; registering there is the minimal, robust hook and keeps all
logic in C# where the `[JSImport]` contract lives. A `lib.module.js` initializer was
considered but is unnecessary — it would still need a C#-side import for the actual
`[JSImport]` modules, and `JSHost.ImportAsync` from the compile path already delivers
zero-consumer-wiring.

**Ordering safety:** the `shadowdusk-spirv-cross` shim uses top-level `await` (eager WASM
instantiation) so its `transpileToGlsl` export can be synchronous. `JSHost.ImportAsync`
only resolves after a module finishes evaluating, so awaiting the import guarantees the
SPIRV-Cross WASM is ready before the **synchronous** `[JSImport]` in
`JsSpirvToGlslTranspiler` fires. Both modules are registered up front in the async DXC path,
which the pipeline (`CompilationPipeline`: `dxcCompiler.CompileAsync` → `glslTranspiler.
Transpile`) **always** runs before the synchronous transpile — so no synchronous `[JSImport]`
can ever hit an unregistered module (which would abort the .NET WASM runtime).
Registration is gated by a `SemaphoreSlim` + cached `Task`; a failed import is NOT cached
(so a transient asset-fetch failure can retry).

### SPIRV-Cross module moved into the package

The faithful SPIRV-Cross module previously lived only in the sample; M1 copied it into the
package so it ships as static web assets:
`src/ShadowDusk.Wasm/wwwroot/shadowdusk-spirv-cross.js` + `wwwroot/spirv-cross/spirv-cross.
{js,wasm}` (the 2.2 MB `spirv-cross.wasm` is committed — small/faithful). The shim's
`import './spirv-cross/spirv-cross.js'` resolves relative to its own module URL, i.e.
`_content/ShadowDusk.Wasm/spirv-cross/spirv-cross.js`, which the package ships.

### Restore wiring

`tools/restore.ps1` (`Restore-DxcWasm`) and `tools/restore.sh` (`restore_dxc_wasm`) now copy
the built `.wasm-build/dxc-wasm-out/dxcompiler.wasm` into
`src/ShadowDusk.Wasm/wwwroot/dxc/dxcompiler.wasm` (size-checked; skips if up to date) before
build/pack, and accept a pre-populated package copy as sufficient. The 17.4 MB
`dxcompiler.wasm` stays gitignored (`.gitignore` already ignores the package wwwroot copy +
`.wasm-build/`); the small `shadowdusk-dxc.js` shim and `dxc/dxcompiler.js` loader are
committed. Documented in `src/ShadowDusk.Wasm/wwwroot/dxc/RESTORE.md`.

---

## The `_content` asset layout (what a consumer gets)

```
_content/ShadowDusk.Wasm/
├── shadowdusk-dxc.js              (7 KB)    faithful [JSImport] shim — product frontend
├── dxc/
│   ├── dxcompiler.js             (54 KB)    emscripten MODULARIZE+ES6 loader (committed)
│   └── dxcompiler.wasm        (17.4 MB)     faithful pinned DXC→WASM (gitignored, restored)
│   └── RESTORE.md
├── shadowdusk-spirv-cross.js     (9 KB)     SPIRV-Cross [JSImport] shim
└── spirv-cross/
    ├── spirv-cross.js            (26 KB)    emscripten loader (committed)
    └── spirv-cross.wasm         (2.2 MB)    SPIRV-Cross→WASM (committed)
    └── README.md
```

---

## Verification gates

| Gate | What | Result |
|---|---|---|
| **G-build** | `dotnet build src/ShadowDusk.Wasm` (net8.0-browser) + `dotnet build ShadowDusk.slnx` | ✅ clean, 0 warnings/errors |
| **G-tests** | `dotnet test ShadowDusk.slnx --settings ShadowDusk.runsettings` | ✅ **515/515** (12+248+89+25+13+128), no regression |
| **G-pack** | `dotnet pack src/ShadowDusk.Wasm` + inspect `.nupkg` | ✅ all assets under `staticwebassets/` (see below) |
| **G-consumer** | scratch Blazor WASM `net8.0-browser` consumer OUTSIDE the repo, PackageReference only | ✅ restores + builds + publishes; `_content/ShadowDusk.Wasm/…` present in publish |
| **G0 / G1** | node byte-identity of faithful DXC→WASM module + product shim | ✅ 10/10 byte-identical to desktop DXC |
| **G-sample / headless render** | sample (hand-wiring deleted) + `run-harness.mjs --corpus=faithful` | ✅ **10/10 compiled in-browser + 10/10 rendered** in real KNI WebGL |

### G-pack — `ShadowDusk.Wasm.0.1.0.nupkg` contents (key entries)

```
staticwebassets/shadowdusk-dxc.js              7088
staticwebassets/dxc/dxcompiler.js             53903
staticwebassets/dxc/dxcompiler.wasm        17404371   (after restore)
staticwebassets/shadowdusk-spirv-cross.js      9410
staticwebassets/spirv-cross/spirv-cross.js    25828
staticwebassets/spirv-cross/spirv-cross.wasm 2219895
lib/net8.0-browser1.0/ShadowDusk.Wasm.dll
build/Microsoft.AspNetCore.StaticWebAssets.props   (auto-serves at _content/ShadowDusk.Wasm/)
```

### G-consumer — scratch consumer (the DoD proof)

A Blazor WASM app created at `%TEMP%/sd-consumer/ScratchConsumer` (outside the repo),
`net8.0-browser`, with **one** `<PackageReference Include="ShadowDusk.Wasm" Version="0.1.0"/>`
resolved from a local NuGet feed (`%TEMP%/sd-feed`, the 5 freshly-packed ShadowDusk.*
packages) — **no `wwwroot` files, no `JSHost.ImportAsync`, no copying `.js`/`.wasm`**. A
`Compile.razor` calls `new WasmShaderCompiler().CompileAsync(grayscaleFx, OpenGL)`.

`dotnet publish -c Release` succeeds and the publish output contains:
- `wwwroot/_content/ShadowDusk.Wasm/{shadowdusk-dxc.js, dxc/dxcompiler.{js,wasm}, shadowdusk-spirv-cross.js, spirv-cross/spirv-cross.{js,wasm}}` — all present (17M dxc + 2.2M spirv-cross).
- `wwwroot/_framework/{ShadowDusk.Core,HLSL,GLSL,Compiler,Wasm}.wasm` — the full set pulled transitively.

These are exactly the URLs the self-registrar imports (`../_content/ShadowDusk.Wasm/…`).

### G-sample / headless — the strongest proof

`samples/ShaderFiddle.Web/Pages/Index.razor.cs` had its hand-wiring deleted (the two
`JSHost.ImportAsync` calls + the `NavigationManager Nav` injection + the
`System.Runtime.InteropServices.JavaScript` using). The sample now consumes the package
exactly like a third-party app — it just calls `CompileAsync`, and the package
self-registers from its own `_content/ShadowDusk.Wasm/`.

`node publish-sample-faithful.mjs` (updated to re-assert the faithful frontend into the
served `_content/ShadowDusk.Wasm/` rather than the sample root) + `node run-harness.mjs
--corpus=faithful` ran headless Chromium (ANGLE/SwiftShader):

```
[harness] Mode-1: 10/10 pass; loaded=10/10
[harness] FAITHFUL mode-2: 10/10 render-pass; compiled=10/10 in-browser   (exit 0)
```

All 10 corpus shaders compiled **in-browser via the package-self-registered faithful
DXC→WASM frontend** and rendered pixel-equivalent in real KNI WebGL. Per-shader deltas are
identical to the prior M2/M3 baseline (Grayscale/Invert/TintShader/Sepia/Pixelated/Scanlines/
Fading Δ1; Saturate Δ3 over 0.004%; Dots Δ11 within tolerance 12; Dissolve Δ128 over 0.145%
localized discard-band drift) — confirming the M1 packaging refactor changed packaging only,
not behavior. This proves the self-registration code path works end-to-end in a real
browser, with the consumer wiring NOTHING.

---

## Files changed

- `src/ShadowDusk.Wasm/ShadowDusk.Wasm.csproj` — Razor SDK + packaging metadata + `VerifyDxcWasmPresent`.
- `src/ShadowDusk.Wasm/WasmModuleRegistration.cs` — **new**; self-registers both modules from `_content/ShadowDusk.Wasm/`.
- `src/ShadowDusk.Wasm/JsShaderBackends.cs` — awaits `EnsureRegisteredAsync` before the first `[JSImport]`; threads `cancellationToken`; refreshed stale Slang/21 MB doc comments.
- `src/ShadowDusk.Wasm/wwwroot/shadowdusk-spirv-cross.js`, `wwwroot/spirv-cross/{spirv-cross.js,spirv-cross.wasm,README.md}` — **moved into the package** (was sample-only).
- `src/ShadowDusk.Wasm/wwwroot/dxc/dxcompiler.wasm` — restored (gitignored).
- `src/ShadowDusk.Wasm/wwwroot/dxc/RESTORE.md` — M1 marked DONE.
- `samples/ShaderFiddle.Web/Pages/Index.razor.cs` — DELETED the consumer-side `JSHost.ImportAsync` hand-wiring + `Nav` injection; the package self-registers now.
- `tools/restore.ps1`, `tools/restore.sh` — copy `dxcompiler.wasm` into the package wwwroot before build/pack.
- `tests/ShadowDusk.BrowserTests/publish-sample-faithful.mjs` — overlay the faithful frontend into `_content/ShadowDusk.Wasm/` (the self-registration path).

Slang stays **sample-only** (its `wwwroot/shadowdusk-dxc.js` + `wwwroot/slang/` are intact
in the sample but never registered); the faithful DXC→WASM module is the product frontend.
