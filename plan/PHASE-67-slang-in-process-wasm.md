# Phase 67: full Slang input where no process can be spawned (in-process slangc)

**Status: 🟡 browser route built and proven in-repo (2026-10-01); packaging and Android open.**
Issue [#257](https://github.com/kaltinril/ShadowDusk/issues/257). Follows
[Phase 66](PHASE-66-full-slang-input-implementation.md) (`ShadowDusk.Slang`, real slangc as a
child process).

## 1. The problem

`ShadowDusk.Slang` compiles genuine Slang (`import`, generics, `interface`s) by spawning the
pinned slangc v2026.14.1 once per entry point and handing its HLSL to the unchanged pipeline.
A browser cannot spawn a process, and Android was unchecked, so those hosts only had the
HLSL-compatible subset frontend in `ShadowDusk.Compiler`.

**Scope fence (non-negotiable, Phase 61 §2.1 / Phase 66 §1):** Slang is an INPUT language.
Whatever runs slangc in-process turns `.slang` into HLSL and nothing else; the HLSL then goes
through DXC (or vkd3d) exactly as on the desktop. It never compiles HLSL to SPIR-V and never
replaces DXC. This is the line Phases 22/23 drew: their sample used upstream slang-wasm *as the
HLSL compiler*, and Phase 23 rejected it for the product because two DXC flags could not be
forwarded through its API, so arbitrary user shaders could diverge silently. This phase uses
slangc only for the job it already does on the desktop, and the measurement below is what keeps
it honest: identical arguments in, identical bytes out.

## 2. Measurements (2026-10-01, Windows 11, Node 22, emsdk 6.0.0)

### 2.1 Upstream's official `slang-wasm` cannot reproduce the desktop route

The pinned release ships `slang-2026.14.1-wasm.zip` (sha256
`af339c23a447638be00070ef3d6cd7368c945545f69a86678c564b3bd9b4b06c`): `slang-wasm.js` +
`slang-wasm.wasm` (24.0 MB) + `interface.d.ts`. Its embind API is the playground's session API:
`GlobalSession.createSession(target)` takes a compile target and nothing else, and for HLSL the
binding forces profile `sm_6_6` (`source/slang-wasm/slang-wasm.cpp`). There is no `callMain`
and no `_main`. So there is no way to pass `-no-mangle`,
`-no-hlsl-pack-constant-buffer-elements`, or a single `-D` platform macro, all of which
`SlangCompiler` passes.

Measured over the 17 shipped corpus shaders (20 entry points): **0/20 byte-identical** to native
slangc's ShadowDusk invocation. The first divergence is structural, not cosmetic:
`#pragma pack_matrix(row_major)` (the session API's default) where the command line emits
`column_major`, `[shader("pixel")]` kept on the entry, `#line ... "/user.slang"` instead of
`"<stdin>"`, mangled names, and no platform macros. This is Phase 23's "flags don't forward"
defect again, so the official module is not used.

### 2.2 The same compiler, driven through its own command line: 235/235 identical

The same release also ships `slang-2026.14.1-wasm-libs.zip` (sha256
`c6f1942f83324bb951c7d24aa30a5c32125206b182b2cf04508cc2ac4d42febd`): upstream's own prebuilt
wasm static libraries (`libslang-compiler.a` 279 MB, `libcompiler-core.a`, `libcore.a`,
`libminiz.a`, `liblz4.a`, `libcmark-gfm.a`) and headers. That is the build upstream links its
own `slang-wasm.js` from, made with emsdk **6.0.0** (pinned in upstream's `release.yml` at the
v2026.14.1 tag).

`.wasm-build/slang-wasm/slangc-wasm-glue.cpp` links those libraries unchanged and replays
slangc's `innerMain` (`source/slangc/main.cpp`): a global session with `enableGLSL = true`,
`spCreateCompileRequest`, `setCommandLineCompilerMode`, `processCommandLineArguments(args)`,
`compile`. The argument list ShadowDusk builds reaches slang's own option parser verbatim. To
keep `-- -` and the `<stdin>` display name, the source is written to a MEMFS file and `stdin` is
reopened on it, so slang's `_readStdinSource` `fread`s it exactly as it reads a pipe. Output and
diagnostics are captured by an `ISlangWriter` that reports `isConsole() == false`, which is what
a redirected native slangc sees. Link: 61 s, `shadowdusk-slangc.wasm` 23.2 MB, `.js` 92.6 KB.

`.wasm-build/slang-wasm/node-test-slangc-wasm.mjs` runs every `.slang` in the four corpora
(shipped 17, adversarial 4, Phase 65 probe 4, Phase 66 A6 residue sweep 15), every
`[shader(...)]` entry, and all five targets' platform macros through native win-x64 slangc and
through the module with the identical argument list, comparing exit code, stdout and stderr
after the same per-line join the managed side applies:

| Measure | Result |
|---|---|
| slangc runs compared | **235** |
| byte-identical (exit, stdout, stderr) | **235** |
| of which slangc rejections (diagnostics compared too) | 50 |
| native slangc, total wall time (spawn per run) | 53.3 s |
| in-process module, total wall time (one global session) | 6.6 s |
| module load (node, warm disk) | 0.25 s |

The module was built with `-Os` against musl where native slangc is an MSVC/glibc/libSystem
release build; float constants in the HLSL (for example `0.29899999499320984f`) still match
exactly, as they already did across the four native RIDs.

### 2.3 In a real browser: 42/42 manifest bytes, 18/18 renders

`tests/ShadowDusk.BrowserTests/browser-slang-gate.mjs` (headless Chromium, ANGLE/SwiftShader,
published `ShaderFiddle.Web`, real `[JSImport]`, real HTTP fetch of every module):

- every `OpenGL/*` and `DirectX_Vkd3d/*` key of `slang-manifest.json` (21 shaders) compiled in
  the page through `WasmSlangCompiler` hashes to the manifest: **42/42**. That manifest is the
  one `SlangCrossHostByteIdentityTests` asserts on Windows, Linux and macOS, so the browser is
  one more host producing the same bytes;
- all 17 shipped shaders compiled for OpenGL through the slangc route and through the subset
  frontend, loaded into the live KNI WebGL `Effect` and read back: **17/17 maxd 0** between the
  two independent front ends, the procedural ones drawing a real image;
- the sample's interface + generics shader (`generic-blend.slang`, which only real slangc
  accepts) loads and draws.

First compile (module fetch + instantiate) 3.3 s; later compiles 40-170 ms per shader in the page.

### 2.4 Stack size and traps (PR #266 review)

The first build linked emscripten's default 64 KB stack (native slangc: 1 MB on Windows, 8 MB on
Linux/macOS). slang recurses per nesting level, so valid shaders trapped with `memory access out
of bounds` at ~205 added terms, ~205 else-ifs, ~156 nested ifs, ~98 nested parens and ~70 nested
ternaries, where native slangc compiles twice that. The module now links `-sSTACK_SIZE=8MB`, and
the node gate's depth cases (400 added terms, 300 parens, 200 ternaries, 300 nested ifs, 400
else-ifs, all below where native Windows slangc itself gives out) match native exactly. Wasm
recursion also uses the JS engine's own native stack, so far deeper input ends in a JS
`RangeError: Maximum call stack size exceeded`.

A trap also used to poison the instance: every later call failed, even for trivial shaders, and
the failure surfaced as a fake slangc diagnostic (`SD0622`). The shim now drops a trapped
instance, `WasmSlangCompiler` marks the module not ready and reports `SD1905`, and the next load is
fresh. Covered by the node gate (trap, then a compile through the shim that matches native) and the
browser gate (trap, then the next compile on the same page matches the manifest).

The DXC, SPIRV-Cross and vkd3d wasm recipes set no stack size either; that is a separate,
unmeasured gap (`docs/validation-matrix.md` §7).

### 2.5 Android: spawning is not a seamless route

Measured on the local `pixel_7_-_api_34` emulator (x86_64, API 34) with a throwaway .NET 9
Android probe app (not committed):

| Probe | Result |
|---|---|
| upstream Linux x86_64 slangc pushed to `/data/local/tmp` and run from `adb shell` | `No such file or directory`: its ELF interpreter `/lib64/ld-linux-x86-64.so.2` does not exist on bionic. Upstream publishes no Android build. |
| NDK-built test executable copied into the app's files dir, `Process.Start` | `Win32Exception: Permission denied` (W^X for `targetSdk >= 29`). This is what `SlangNativeCache` would do. |
| same executable shipped as `lib/x86_64/libprobeexe.so`, run from `nativeLibraryDir` (Debug build) | works: stdin piped, stdout captured, exit code 7 as written, 29-66 ms |
| same, default **Release** build | the file is not on disk (`extractNativeLibs=false`), `FileNotFoundException` |

So a spawn route on Android needs both an NDK build of slangc (none exists upstream) and a
consumer opt-in (`AndroidUseLegacyPackaging=true`) for the executable to exist at all. The
seamless directive rules that out. Android should take the in-process route too (§3.3).

## 3. Design

### 3.1 One route, two transports (decided; see `project_decisions.md`)

`SlangCompiler` stays the only implementation of the route. The slangc call is the only thing
that varies by host:

- `SlangcArguments.Build` is the one argument list; `SlangCompiler.RunSlangc` (desktop, child
  process) and the in-process route both use it.
- `SlangcArguments.JoinOutputLines` reproduces `Process.OutputDataReceived`'s per-line split and
  the `'\n'` re-join, so an in-process slangc's raw writer text lands on the same string.
- An internal constructor `SlangCompiler(IShaderCompiler, InProcessSlangc)` skips only the
  executable lookup/preparation (`SD0620`/`SD0621`/`SD0623`); entry discovery, the
  host-independent rejections, `SlangcRegisterStripper` (issue #252), `SlangHlslMerger`, the
  `.fx` assembly and the downstream compile are the same code. It is internal and reachable only
  by `ShadowDusk.Slang.Wasm` (`InternalsVisibleTo`). That is a convention, not a security boundary (the assemblies are not strong-named, so a determined caller could still reach it), but no public API offers a way to plug a different
  compiler into it.

`SlangInProcessRouteTests` pins the argument list, the output normalization, and runs the whole
21-shader corpus through the in-process seam with native slangc behind it, matching every
`AssembledFx` hash in the manifest.

### 3.2 Browser: `ShadowDusk.Slang.Wasm`

A `net8.0-browser` Razor-SDK project (the `ShadowDusk.Wasm` pattern) referencing
`ShadowDusk.Wasm` and `ShadowDusk.Slang`:

- `WasmSlangCompiler` wraps `SlangCompiler`'s in-process constructor with a `WasmShaderCompiler`
  downstream (shareable, so a page that already compiles `.fx` reuses its loaded modules).
  `CompileAsync` loads on first use; `InitializeAsync` + sync `Compile` mirror
  `WasmShaderCompiler`. `SD1904` = slangc module failed to load; `SD1905` = slangc module trapped (the instance is discarded, the next load is fresh); a sync compile before init is the existing `SD1903`. A DXC/vkd3d module that fails to load keeps its own `SD1900`/`SD1902`: `CompileAsync` loads only the slangc module up front and hands the assembled `.fx` to `WasmShaderCompiler.CompileAsync` when its sync core reports `SD1903`.
- `SlangcModule` registers `wwwroot/shadowdusk-slangc.js` (the `[JSImport]` shim) from
  `_content/ShadowDusk.Slang.Wasm/` and lazily fetches `wwwroot/slangc/shadowdusk-slangc.wasm`.
  The consumer wires nothing.
- Kept out of `ShadowDusk.Wasm` on purpose: the 23 MB module would otherwise land in every
  browser consumer's publish output, which breaks Phase 66's "no Slang cost unless you add the
  Slang package" rule.

The sample (`ShaderFiddle.Web`) routes Slang-looking source (a `[shader(...)]` attribute, no
`technique`) through `WasmSlangCompiler` for both live compile and export, and has a
"Slang generic blend" example button.

### 3.3 Android (designed, not built)

The in-process route generalizes: build slang's compiler library for Android with the NDK (the
`build-dxc-android.ps1` precedent), expose the same glue as a C entry point
(`runSlangc(source, args) -> exit/stdout/stderr`), and P/Invoke it behind the same
`InProcessSlangc` delegate. A loaded `.so` needs no extraction, so it works with default
packaging, which is how DXC and SPIRV-Cross already run on Android. Unmeasured: the slang
compiler library's size and its Android build effort (upstream does not build it for Android).

## 4. What landed (this PR) and what is left

**Landed and proven:**

- [x] Research: official slang-wasm measured unusable (0/20); upstream's prebuilt wasm libraries
  + slangc-replaying glue measured identical to native slangc (235/235).
- [x] Shared seam in `ShadowDusk.Slang` (`SlangcArguments`, internal in-process constructor),
  with tests (`SlangInProcessRouteTests`, pure + one integration test over the manifest).
- [x] `src/ShadowDusk.Slang.Wasm` (`WasmSlangCompiler`), in `ShadowDusk.slnx`, `IsPackable=false`.
- [x] Build recipe force-committed under `.wasm-build/slang-wasm/` (glue, `build-slangc-wasm.ps1`
  with the libraries' SHA-256 pinned, `node-test-slangc-wasm.mjs`).
- [x] Sample: Slang source compiles in the browser; example button.
- [x] Browser gate `browser-slang-gate.mjs` (42/42 bytes, 18/18 renders, measured locally) and
  both gates wired into `wasm.yml` `browser-smoke` (the job builds the module from the recipe,
  cached on the recipe's inputs, and re-proves it against native slangc every run).
- [x] Android spawn measured (§2.5).

**Remaining (registered in `docs/validation-matrix.md` §7 and `plan/plan.md`):**

- [ ] **Ship `ShadowDusk.Slang.Wasm` as the eleventh package.** A dispatch workflow that builds
  `shadowdusk-slangc.{js,wasm}` from the recipe; attach the artifact by hand to a fixed
  `native-slangc-wasm-2026.14.1` release tag (the `native-vkd3d-wasm-*` model, never republished);
  pin both hashes in `tools/restore.{ps1,sh}`; a `VerifySlangcWasmPresent` pack guard;
  `release.yml` + `pack-consume.yml` entries and a cold browser-consumer check; then the
  package count everywhere it is stated (`CLAUDE.md`, `project_facts.md`, README, docfx). After
  that the CI job restores the hosted module instead of building it.
- [x] **Project-reference leak in the sample publish (fixed in the review round):** `ShadowDusk.Slang`'s
  win-x64 `slangc.exe` + `slang-compiler.dll` (25 MB) used to be `CopyToOutputDirectory` items, which
  flow into every referencing project, so they landed in the browser sample's publish root. Every RID
  is now pack-only (repo runs find `tools/slang/<rid>/` by walking up, as the Unix RIDs already did).
- [ ] **Stack size of the DXC / SPIRV-Cross / vkd3d wasm modules** (§2.4; validation-matrix §7).
- [ ] **Android in-process slangc** (§3.3): NDK build of slang's compiler library, the C entry
  point, the P/Invoke transport, and an on-emulator proof (`validation/AndroidGl` precedent).
- [ ] Optional: retire the sample's dead Phase 22 Slang-as-HLSL-compiler shim
  (`samples/ShaderFiddle.Web/wwwroot/shadowdusk-dxc.js` + `wwwroot/slang/`), which is never
  registered but now sits beside a real Slang route and invites confusion.

## 5. How to reproduce

```powershell
./tools/restore.ps1                                             # native slangc + ShadowDusk.Wasm modules
pwsh .wasm-build/slang-wasm/build-slangc-wasm.ps1               # -> src/ShadowDusk.Slang.Wasm/wwwroot/slangc/
node .wasm-build/slang-wasm/node-test-slangc-wasm.mjs src/ShadowDusk.Slang.Wasm/wwwroot/slangc
cd tests/ShadowDusk.BrowserTests; npm install; npx playwright install chromium
node browser-slang-gate.mjs --require-module
```
