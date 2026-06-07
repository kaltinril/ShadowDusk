# Phase 38 — Surface real line/column compile diagnostics from the in-browser (WASM) compiler

**Status:** 🟢 **Implemented (2026-06-07)** — branch `phase38-wasm-compile-diagnostics`. Approach **B**. All code edits + the Stage-2 relink are done and verified headless: **G1 byte-identity 10/10** (no fidelity regression) and the new **G2 diagnostics gate** passes (bad HLSL now yields `file:line:col: error: message`, not the opaque blob). The final rung — seeing the squiggle in a real KNI/Blazor browser — is the only thing left.

**Track:** Reach (Part 1 of THE PURPOSE — in-browser/in-memory) + diagnostics quality (Core Design Constraint 5: *fail loudly with file/line/column*). Consumer-facing: this is what lets a downstream KNI/Blazor tool (e.g. Vic's **XNAFiddle**) show *where* a shader is wrong, not just *that* it failed.

---

## TL;DR

On **desktop**, a failed `.fx` compile returns `ShaderError[]` with real `File`/`Line`/`Column`/`Message` (the editor can squiggle the bad line). On **WASM/in-browser**, the *same* failure currently returns a single `Line: 0` error whose message is the opaque string **`WASM DXC backend failed: [object WebAssembly.Exception]`** — no line, no reason.

The diagnostic text is **not missing — it's lost in transit.** DXC produces it, the C++ glue captures it verbatim, then `throw`s it; but the module is built with `-fwasm-exceptions` and does **not** export emscripten's exception-message helper, so the `what()` string is unreadable from JS/.NET.

**Fix (Approach B):** make the C++ glue **return** the diagnostics instead of throwing them, have the JS shim re-throw a normal `Error` carrying that text, and have the C# WASM seam run the text through the **same `DxcDiagnosticReformatter`** desktop already uses. Then **relink only Stage 2** of the WASM build (em++ link of our glue — minutes; the heavy LLVM/DXC libs are already cached on disk).

**Consumer impact: zero API change.** Vic already calls `WasmShaderCompiler.CompileAsync(...)` → `Result<CompiledShader, ShaderError[]>`. After this fix those `ShaderError`s carry real `.Line`/`.Column`/`.Message` in the browser, exactly like desktop. His existing code that reads those fields just starts working — no flag, no version pick, no extra wiring (per the *seamless-for-end-user* rule).

---

## Symptom (reported)

User loaded `samples/ShaderFiddle.Web`, pasted garbage HLSL, clicked **Compile & Apply (mode 2)**, and got:

```
1 compile error(s) — last good render kept.
1 diagnostic(s)
WASM DXC backend failed: [object WebAssembly.Exception]
```

No line number, no squiggle, no indication of *what* or *where* the error is. (The sample's new squiggle/gutter UI works — there was simply no line data to drive it.)

## Root cause (verified end-to-end)

1. **DXC captures the diagnostics correctly.** The faithful C++ embind glue pulls DXC's `DXC_OUT_ERRORS` blob verbatim on failure — the same `file:line:col: error: message` text the desktop reformatter parses — then throws it:
   - [`.wasm-build/dxc-wasm-glue.cpp:108-118`](../.wasm-build/dxc-wasm-glue.cpp#L108-L118) → `throw std::runtime_error(errText);`
2. **`-fwasm-exceptions` makes the thrown text opaque in JS.** Build flag confirmed in `.wasm-build/build-dxc-wasm.ps1` (lines ~164, ~217). A C++ exception thrown across embind under wasm-EH arrives in JS as a `WebAssembly.Exception` whose `.message` is the default `[object WebAssembly.Exception]`. Decoding the real `what()` requires emscripten's `Module.getExceptionMessage(e)` helper.
3. **The decode helper was not exported.** `grep` over the shipped `src/ShadowDusk.Wasm/wwwroot/dxc/dxcompiler.js` finds **none** of `getExceptionMessage` / `getCppExceptionMessage` / `EXPORT_EXCEPTION_HANDLING_HELPERS`. So neither the JS shim nor .NET can read the text.
4. **The C# WASM seam therefore can only wrap the opaque string.** It does not call the reformatter:
   - [`src/ShadowDusk.Wasm/JsShaderBackends.cs:67-77`](../src/ShadowDusk.Wasm/JsShaderBackends.cs#L67) → `Line: 0`, `Message: $"WASM DXC backend failed: {ex.Message}"`.

**Contrast — desktop already does it right:** [`src/ShadowDusk.HLSL/Dxc/DxcShaderCompiler.cs:58-75`](../src/ShadowDusk.HLSL/Dxc/DxcShaderCompiler.cs#L58-L75) runs `DxcDiagnosticReformatter.Reformat(errorText, sourceFileName)` and returns the primary parsed error with line/col. The parser itself is shared and correct: [`src/ShadowDusk.HLSL/Dxc/DxcDiagnosticReformatter.cs`](../src/ShadowDusk.HLSL/Dxc/DxcDiagnosticReformatter.cs) (Clang-style `^(?<file>.+):(?<line>\d+):(?<col>\d+):\s*(?<severity>error|warning|note):\s*(?<message>.+)$`).

## Fix (Approach B — chosen)

Make the diagnostics travel as **data**, not as an exception payload, so wasm-EH opacity is irrelevant.

1. **C++ glue** — `.wasm-build/dxc-wasm-glue.cpp`
   - Change `compileToSpirv` so that on a failed compile it **returns** a result carrying the error text (e.g. a JS object `{ spirv: Uint8Array, error: string }`, `error` empty on success) instead of `throw std::runtime_error(errText)`.
   - Keep the existing `DXC_OUT_ERRORS` extraction (lines 108-118) — that part is already correct; only the *delivery* changes (return vs throw). Genuine init/instantiation failures (`DxcCreateInstance`, blob creation) may still throw — those are not per-line diagnostics.
2. **JS shim** — `src/ShadowDusk.Wasm/wwwroot/shadowdusk-dxc.js`
   - Read the returned object; if `error` is non-empty, `throw new Error(error)` — now a **plain JS Error** whose message *is* DXC's `file:line:col: error: message` text (marshals to .NET as `JSException.Message`).
   - On success, return the `spirv` `Uint8Array` (keep the magic-byte / empty-output sanity checks as defense in depth).
3. **C# WASM seam** — `src/ShadowDusk.Wasm/JsShaderBackends.cs`
   - In the `catch (JSException ex)`, run `ex.Message` through `DxcDiagnosticReformatter.Reformat(ex.Message, request.SourceFileName)` and return the **primary** parsed `ShaderError` (mirrors desktop's `errors[0]` behavior). Fall back to the current opaque wrap only when the text parses to zero diagnostics (e.g. a real backend/init failure, not a shader error).
4. **Relink (Stage 2 only)** — re-run just the em++ link step of `.wasm-build/build-dxc-wasm.ps1` (the `-BuildOnly` / Stage-2 path) to rebuild `dxcompiler.{js,wasm}`, then copy into `src/ShadowDusk.Wasm/wwwroot/dxc/`.
   - **Stage 1 is NOT needed:** the 50 LLVM/DXC `.a` archives are present at `.wasm-build/dxc-src/build-wasm/lib/*.a`, and emsdk is present at `.wasm-build/emsdk/...em++.bat`. Stage 2 is a link-only step (`em++ ... -O3`), minutes not hours.

### Why not Approach A

Approach A keeps the `throw` and instead relinks with `-sEXPORT_EXCEPTION_HANDLING_HELPERS=1` + `EXPORTED_RUNTIME_METHODS=['getExceptionMessage']`, then the JS shim catches and calls `getExceptionMessage(e)`. It avoids the C++ edit but couples us to emscripten EH internals and message formatting. **B is cleaner and more faithful** (the diagnostic is a normal return value). Both require the same Stage-2 relink and the same C# reformatter change.

## Sample squiggle UI (already implemented this session)

`samples/ShaderFiddle.Web` was upgraded so it *can show* line-accurate errors once the backend supplies them:

- `Pages/Index.razor` — the plain `<textarea>` became a **line-number gutter + scroll-synced backdrop + transparent textarea**. Flagged lines get a wavy underline; the gutter line number turns red with the message as a hover tooltip; the diagnostics list shows `Line N, Col C: message` and each entry is clickable to jump to the line.
- `Pages/Index.razor.cs` — keeps structured `ShaderError`s (no longer flattens to strings); derives per-line message/severity maps; clears stale squiggles on edit/reset; `GotoLineAsync`.
- `wwwroot/css/app.css` — gutter/backdrop/squiggle styles; transparent editor layered over the backdrop.
- `wwwroot/index.html` — `sdEditorSync` (scroll alignment) + `sdEditorGotoLine`.
- `_Imports.razor` — `@using ShadowDusk.Core` for `ShaderErrorSeverity` in markup.

This builds clean today (Debug, 0 warnings). It currently shows the single opaque WASM error because the backend supplies `Line: 0`; after the backend fix it will squiggle the real line.

## Definition of done

- A deliberately-broken `.fx` compiled **in the browser** (mode 2) returns a `ShaderError` with the **correct `Line`/`Column`/`Message`** (matches what desktop returns for the same source).
- The ShaderFiddle.Web sample **squiggles the offending line** and shows the reason (gutter tooltip + clickable diagnostic), driven by that data — verified in a real browser.
- A consumer (XNAFiddle-shape) reading `error.Line` / `error.Column` / `error.Message` off `WasmShaderCompiler.CompileAsync` gets usable values with **no API/wiring change**.
- The rebuilt `dxcompiler.{js,wasm}` still produces **byte-identical SPIR-V** on the success corpus (no fidelity regression) — re-run the node gate `.wasm-build/node-test-dxc-shim.mjs` / `node-test-dxc-wasm.mjs` (10/10) and confirm successful compiles are unchanged.
- ShadowDusk.Wasm package builds; sample builds/publishes.

## Verification plan

1. **Node harness (fast, headless):** extend `.wasm-build/node-test-dxc-shim.mjs` with a known-bad shader; assert the shim now throws an `Error` whose message contains `:<line>:<col>:` and `error:`. Confirm the good corpus still returns valid SPIR-V (byte-identity unchanged).
2. **C# unit (no browser):** the reformatter path is already covered for desktop; add a WASM-seam test that feeds a sample DXC error string and asserts a parsed `ShaderError` (`Line>0`).
3. **Real browser:** `dotnet run` the sample, paste garbage, confirm squiggle on the right line + gutter tooltip + clickable jump. Spot-check a multi-line shader for alignment under scroll.

## Result (2026-06-07)

Implemented Approach B and verified headless:

- **C++ glue** now returns `{ spirv, error }` (no throw on compile failure); **JS shim** re-throws `new Error(error)` carrying DXC's verbatim text (with a back-compat branch for a bare-`Uint8Array` build); **C# seam** parses it via `DxcDiagnosticReformatter` and returns the first located (`Line > 0`) diagnostic, falling back to `SD1900` raw.
- **Stage-2 relink only** (`build-dxc-wasm.ps1 -SkipHostTblgen -SkipLib`) — the 50 cached LLVM/DXC archives + emsdk 3.1.34 were on disk, so no multi-hour rebuild. New `dxc-wasm-out/dxcompiler.{js,wasm}` copied into the package wwwroot.
- **G1** (`node-test-dxc-shim.mjs`): **10/10 byte-identical** to desktop DXC — zero fidelity regression.
- **G2** (new `node-test-dxc-diagnostics.mjs`): a broken shader now yields
  `…/Grayscale.fx:30:18: error: use of undeclared identifier '…'` — exactly the `file:line:col:` shape the reformatter consumes; no `[object WebAssembly.Exception]`.
- `ShadowDusk.Wasm` + the sample both build clean.

**Left:** run the sample in a real browser and confirm the squiggle/gutter lands on the offending line (the C# reformatter is desktop-unit-tested and the format matches, so this is a confirmation rung, not a risk).

## Binary policy (decided 2026-06-07)

Per the user ("industry standard, as long as it doesn't break our flow and purpose"): **keep the expensive `.wasm` committed as deliberate, documented vendored artifacts** (`dxcompiler.wasm` ~17 MB, `spirv-cross.wasm` ~2 MB ×2). The unambiguous standard — commit all source, exclude cheaply-rebuildable outputs (`bin/obj`) — is already met, and the source-input gaps were closed (commit `5ab24e7`). The cleaner alternatives each risk the flow on a private repo with collaborators/forks: Git LFS (every workflow + contributor must handle LFS + quota; a miss turns the shipped `.wasm` into a pointer and breaks `pack`), restore-from-our-release (needs hosted assets + auth, none exist yet), and a history rewrite (force-push that breaks existing clones). Reversible later: if repo *size* becomes the priority, `git lfs migrate import` in a maintenance window is the standard next step.

## Notes / follow-ups

- **Primary vs all diagnostics:** the `IDxcShaderCompiler` seam returns a single `Result<PlatformBlob, ShaderError>`, so both desktop and this fix surface the **primary** (first) diagnostic. Showing **all** errors as multiple squiggles needs a small seam change to carry `ShaderError[]` (or aggregate upstream) — easy additive follow-up if Vic wants it. Not in this phase's DoD.
- **DXC error codes are generic (`X0000`).** Line/col/message are accurate; per-error HLSL codes are a placeholder both on desktop and WASM. Separate enhancement.
- **Faithful-pipeline rule respected:** no substitute compiler — this only changes how the *existing* faithful DXC→WASM module reports failures; success-path bytes are unchanged (must be re-proven by the node gate).
- **Backwards-compat:** no `.mgfx` format or MonoGame-version change; success output untouched.

## Provenance

Found 2026-06-07. User asked whether ShadowDusk can validate `.fx` and return line/error details (yes — desktop does), then to make the WASM sample squiggle bad lines and show the reason. Testing surfaced the WASM-only opaque-error gap (`[object WebAssembly.Exception]`), and the user asked specifically how a downstream tool (Vic's XNAFiddle WASM/KNI fiddle) could show the exact failing line. User chose Approach B and asked for this phase doc on a new branch. Related memories: `product-is-selfcontained-library`, `seamless-for-end-user`, `phase23-m0-dxc-wasm-done` (the faithful DXC→WASM build + node gate), `phase22-shaderfiddle-web`, `global-param-default-not-baked`.
