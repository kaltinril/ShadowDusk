# Phase 23 M2 + M3 — Faithful DXC→WASM frontend, wired and render-proven

This documents the **faithful in-browser HLSL→SPIR-V frontend** (Phase 23, Option A:
the pinned desktop DXC compiled to WebAssembly) being wired into the product path
(M2) and proven to render in a real headless KNI WebGL browser (M3). The Slang shim
stays **sample-only** (`samples/ShaderFiddle.Web/wwwroot/shadowdusk-dxc.js`) — it is
never the product frontend.

**Result: G0 ✅, G1 ✅ (10/10 byte-identical via the shim), G2 ✅ (10/10 compiled
in-browser via the faithful frontend AND rendered pixel-equivalent in real KNI
WebGL, Pixelated + Dissolve included). No blocker.**

---

## The shim — design

**Location (the PRODUCT home, where M1 will package it):**
- `src/ShadowDusk.Wasm/wwwroot/shadowdusk-dxc.js` — the faithful `[JSImport]` shim (committed, ~7 KB).
- `src/ShadowDusk.Wasm/wwwroot/dxc/dxcompiler.js` — the emscripten loader (committed, ~54 KB).
- `src/ShadowDusk.Wasm/wwwroot/dxc/dxcompiler.wasm` — the faithful DXC build (**gitignored**, ~17.4 MB; restored — see `src/ShadowDusk.Wasm/wwwroot/dxc/RESTORE.md`).

The shim satisfies the existing `[JSImport]` contract for module **`shadowdusk-dxc`**
(`src/ShadowDusk.Wasm/JsShaderBackends.cs`) **unchanged** — no C# change was needed:

```csharp
[JSImport("ensureReady",    "shadowdusk-dxc")] static partial Task   EnsureReadyAsync();
[JSImport("compileToSpirv", "shadowdusk-dxc")] static partial byte[] CompileToSpirv(string hlsl, string[] args);
```

- **`ensureReady()`** — lazily `import('./dxc/dxcompiler.js')`, `await createDxcModule()`,
  caches the instance; idempotent (two calls load once); surfaces a sticky load error.
  Deferred (no top-level await) so the 17.4 MB `.wasm` is **not** downloaded at page
  init — `JsDxcShaderCompiler.CompileAsync` awaits it once before the first compile,
  keeping the mode-1 boot instant. (Same lazy contract the Slang shim used.)
- **`compileToSpirv(hlslSource, args)`** — synchronous; calls
  `instance.compileToSpirv(hlslSource, args)`, copies the embind `Uint8Array` out of
  the WASM heap, checks the SPIR-V magic word, returns the bytes.

**The load-bearing difference vs the Slang shim: `args` are forwarded to DXC
VERBATIM** — no `-E`/`-T` parsing, no flag translation. The faithful module *is* DXC
and accepts the exact `DxcFlagBuilder` list
(`-E <entry> -T ps_5_0 -spirv -fvk-use-dx-layout -auto-binding-space 1 -Zpr -WX`).
This is precisely the property Slang lacked (2 DXC flags don't forward through Slang's
API), which is why DXC is the faithful path and Slang is sample-only.

**Module mechanics:** `dxcompiler.js` is a MODULARIZE + EXPORT_ES6 emscripten build
(`export default createDxcModule`) that locates `dxcompiler.wasm` via
`new URL("dxcompiler.wasm", import.meta.url)`. Co-locating the two in `dxc/` is all
the wiring needed — no `locateFile` override. The optional DXIL-validator `dlopen`
probe is stubbed inside the module at link time (`--js-library dxc-dlopen-stub.js`),
so the shim needs no extra files at runtime.

---

## G0 — raw module byte-identity (re-confirmed in this worktree)

`node .wasm-build/node-test-dxc-wasm.mjs` drives the raw emscripten module's
`compileToSpirv` over the 10-shader corpus (`.wasm-build/corpus-spirv/*`) and asserts
the SPIR-V equals the desktop DXC ground truth byte-for-byte.

```
ALL 10 CORPUS SHADERS BYTE-IDENTICAL — DXC->WASM == desktop DXC. M0 gate PASSED.
```

| Shader | SPIR-V bytes | Shader | SPIR-V bytes |
|---|---|---|---|
| Dissolve | 2060 | Pixelated | 1024 |
| Dots | 1912 | Saturate | 1616 |
| Fading | 1020 | Scanlines | 1364 |
| Grayscale | 1104 | Sepia | 1240 |
| Invert | 1080 | TintShader | 1172 |

## G1 — shim byte-identity (M2 DoD)

`node .wasm-build/node-test-dxc-shim.mjs` (new) drives the **product shim**
(`src/ShadowDusk.Wasm/wwwroot/shadowdusk-dxc.js`) through its real contract surface —
`await ensureReady()` (called twice, to exercise idempotency) then
`compileToSpirv(hlsl, args)` — and asserts byte-identity to the same ground truth.
This proves the shim wraps the module correctly (lazy load, verbatim args, byte
copy-out, magic check), not merely that the module is good.

```
[Dissolve]  OK — 2060 bytes, byte-identical to desktop DXC (via SHIM)
[Dots]      OK — 1912 bytes ...
[Fading]    OK — 1020 bytes ...
[Grayscale] OK — 1104 bytes ...
[Invert]    OK — 1080 bytes ...
[Pixelated] OK — 1024 bytes ...
[Saturate]  OK — 1616 bytes ...
[Scanlines] OK — 1364 bytes ...
[Sepia]     OK — 1240 bytes ...
[TintShader]OK — 1172 bytes ...
ALL 10 CORPUS SHADERS BYTE-IDENTICAL VIA THE FAITHFUL SHIM — G1 gate PASSED.
```

**G1 = 10/10 byte-identical via the shim.**

---

## A pipeline bug the faithful path surfaced — MD5 on WASM (fixed)

The first time the faithful compile actually ran end-to-end in the browser (G2), it
failed on **every** shader with `Cryptography_UnknownHashAlgorithm, MD5`. The managed
MGFX writer (`MgfxWriter.ComputeEffectKey`) derived MonoGame's effect-cache key from
`System.Security.Cryptography.MD5.HashData`, and the **.NET 8 browser/WASM runtime
does not provide MD5** (its crypto provider only exposes the SHA family via
SubtleCrypto). This blocked the entire in-browser product pipeline — and no desktop
test caught it because desktop has MD5.

**Fix:** `src/ShadowDusk.Core/ManagedMd5.cs` — a self-contained RFC-1321 MD5,
swapped in at `MgfxWriter.ComputeEffectKey`. MD5 is a fixed standard, so it is
byte-identical to the BCL MD5 on every platform — desktop output is unchanged and the
key is identical cross-host. Verified:
- ManagedMd5 == BCL MD5 across boundary cases (empty, 55/56/57-byte padding edges, multi-block, 2 KB random) — all identical.
- Desktop **byte-identity + determinism** integration tests: 14/14 pass (desktop `.mgfx` unchanged).
- Desktop **image cross-validation** vs mgfxc goldens: 25/25 pass.
- Core unit tests: 231/231 pass.

This is a genuine reach fix: without it, `WasmShaderCompiler.CompileAsync` could not
produce a `.mgfx` in the browser at all.

---

## G2 — faithful render-proof in real headless KNI WebGL (M3 DoD)

**How the faithful module is registered for the proof:** `publish-sample-faithful.mjs`
publishes `samples/ShaderFiddle.Web`, then **overwrites** the served
`wwwroot/shadowdusk-dxc.js` with the faithful product shim and drops the faithful
`dxc/dxcompiler.{js,wasm}` into the served `wwwroot/dxc/`. The sample's
`JSHost.ImportAsync("shadowdusk-dxc", "{BaseUri}shadowdusk-dxc.js")` then resolves to
the FAITHFUL shim (the Slang shim + `wwwroot/slang/` are left untouched in the sample
source — sample-only, unbroken). It also recompiles the corpus with ShadowDusk's CLI
(mode-1 baseline) and reuses `references-sd/` (the desktop DesktopGL render of
ShadowDusk's own bytes).

`run-harness.mjs --corpus=faithful` (new path) then, headless Chromium with
`--use-gl=angle --use-angle=swiftshader`, for **all 10** corpus shaders:
in-browser `WasmShaderCompiler.CompileAsync(.fx)` → faithful DXC→WASM → SPIR-V →
SPIRV-Cross WASM → `SpirvReflector` + MGFX writer → `.mgfx` → `new Effect(gd, bytes)`
in KNI WebGL → render → pixel-compare vs `references-sd/`. (The first compile
lazy-loads + instantiates the 17.4 MB `dxcompiler.wasm` in the browser.)

```
[harness] FAITHFUL mode-2 — compiling all 10 corpus shaders in-browser via DXC->WASM…
[harness] Mode-1: 10/10 pass; loaded=10/10
[harness] FAITHFUL mode-2: 10/10 render-pass; compiled=10/10 in-browser   (exit 0)
```

**Faithful in-browser compile + render, per shader** (vs desktop render of ShadowDusk's own bytes, Phase 17 §6.1 tolerance):

| Shader | In-browser compile | Render | Max channel Δ | Px over tol | Verdict |
|---|---|---|---|---|---|
| Grayscale | OK | OK | 1 | 0/262144 | PASS(tol) |
| Invert | OK | OK | 1 | 0/262144 | PASS(tol) |
| TintShader | OK | OK | 1 | 0/262144 | PASS(tol) |
| Sepia | OK | OK | 1 | 0/262144 | PASS(tol) |
| Saturate | OK | OK | 3 | 10/262144 (0.004%) | PASS(tol) |
| **Pixelated** | OK | OK | 1 | 0/262144 | PASS(tol) |
| Scanlines | OK | OK | 1 | 0/262144 | PASS(tol) |
| Fading | OK | OK | 1 | 0/262144 | PASS(tol) |
| Dots | OK | OK | 11 | 8585/262144 (3.275%) | PASS(tol) — documented sin/cos halftone tolerance 12 |
| **Dissolve** | OK | OK | 128 | 380/262144 (0.145%) | PASS(tol) — localized discard-band drift within pixel budget |

The faithful in-browser deltas are **identical to the mode-1 (precompiled desktop
bytes) deltas** — exactly as expected, since the faithful WASM SPIR-V is byte-identical
to desktop DXC (G0/G1), so the in-browser `.mgfx` equals the desktop `.mgfx`. The diff
is purely WebGL(ANGLE/SwiftShader)-vs-DesktopGL precision/transcendental drift, the
risk Phase 24 isolated.

**Called out explicitly:**
- **Pixelated** passes — the `roundEven`→`floor(x+0.5)` WebGL1 fix holds in the faithful path (max-delta 1).
- **Dissolve** passes — the slot-1 sampler fix holds (max-delta 128 over 0.145% of pixels, the documented localized discard-band drift, within the 0.5% pixel budget).

**G2 = 10/10 compiled in-browser via the FAITHFUL frontend + 10/10 rendered. The
17.4 MB `dxcompiler.wasm` loads and runs in a real browser; the end-to-end faithful
in-browser pipeline renders correctly.** This is the M3 Definition of Done.

Artifacts: `RESULTS-FAITHFUL.md`, `captures-faithful/*.png` (WebGL readbacks),
`diffs-faithful/*_diff.png`, all generated by the run.

---

## How to reproduce

```bash
# Gates G0 + G1 (node, fast — no browser):
node .wasm-build/node-test-dxc-wasm.mjs     # G0: raw module
node .wasm-build/node-test-dxc-shim.mjs      # G1: product shim

# Gate G2 (real headless browser):
cd tests/ShadowDusk.BrowserTests
npm install
npx playwright install chromium
node publish-sample-faithful.mjs             # publish + swap to faithful shim + binaries
node run-harness.mjs --corpus=faithful       # 10/10 compile + render; exit 0
```

(`node-test-dxc-shim.mjs` lives under the gitignored `.wasm-build/` alongside G0's
`node-test-dxc-wasm.mjs`; the faithful `dxcompiler.wasm` is restored per
`src/ShadowDusk.Wasm/wwwroot/dxc/RESTORE.md`.)

---

## What remains for M1 (packaging — next agent)

M2/M3 deliberately did **not** do M1 (Razor SDK static-web-asset packaging / NuGet /
out-of-repo scratch consumer). The faithful files are placed where M1 will package
them. M1's remaining work (from the Phase 23 plan):

1. Multi-target `ShadowDusk.Wasm` (and `ShadowDusk.Compiler`) `net8.0;net8.0-browser`.
2. Package `src/ShadowDusk.Wasm/wwwroot/` (`shadowdusk-dxc.js`, `dxc/dxcompiler.{js,wasm}`,
   `shadowdusk-spirv-cross.js`, `spirv-cross/spirv-cross.{js,wasm}`) as **static web
   assets** so a consumer gets them transitively at `_content/ShadowDusk.Wasm/…`
   (the `spirv-cross` module currently still lives only in the sample wwwroot — M1
   moves it into the package alongside the dxc files placed here).
3. `ShadowDusk.Wasm` **self-registers** both `[JSImport]` modules via `JSHost.ImportAsync`
   against its own `_content/ShadowDusk.Wasm/` base path; delete the consumer-side
   registration the sample does today.
4. Ensure the restore step (RESTORE.md) runs before `pack` so the 17.4 MB `.wasm` is
   present to be packaged.
5. Verify with a scratch consumer **outside the repo**: `PackageReference` only,
   `net8.0-browser`, compile a `.fx` with no manual asset wiring.

The shim and binaries here are the shipping files M1 packages as-is — the faithful
shim is the product frontend; Slang remains sample-only.
