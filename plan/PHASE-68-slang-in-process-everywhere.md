# Phase 68: full Slang in-process everywhere (desktop, browser, Android)

**Status: 📋 Planned (2026-10-04): owner decisions recorded, Area A spike first; no product code written.**
Issues: [#366](https://github.com/kaltinril/ShadowDusk/issues/366) (browser package, Area E),
[#337](https://github.com/kaltinril/ShadowDusk/issues/337) (re-measure, Area F),
[#257](https://github.com/kaltinril/ShadowDusk/issues/257) (the in-process seam),
[#286](https://github.com/kaltinril/ShadowDusk/issues/286) (arm64, unchanged).
Follows [Phase 66](PHASE-66-full-slang-input-implementation.md) (`ShadowDusk.Slang`, real slangc
as a child process) and [Phase 67](PHASE-67-slang-in-process-wasm.md) (the same slangc in-process
in the browser, 235/235 identical).

## 1. Goal, tied to THE PURPOSE

The product is the in-memory compiler a game calls **while it runs**. Full Slang today reaches
that compiler by launching `slangc` once per entry point (plus a `-E` pass on some shapes): about
140-160 ms of process start per run on Windows (measured below), a native executable that has to
be found, copied and made executable (`SlangNativeCache`), and no route at all where a process
cannot be spawned (the browser has Phase 67's in-repo module but no package; Android has nothing).

This phase makes full Slang compile **in-process, in memory, on every host the package
supports**, with no child process and no command line:

| Host | Today | After this phase |
|---|---|---|
| win-x64, linux-x64, osx-x64, osx-arm64 | spawn bundled `slangc` | **in-process call into the bundled slang library (default)**; spawn kept as a non-required escape hatch |
| Browser (KNI Blazor WASM, XnaFiddle) | Phase 67 module works in-repo, `IsPackable=false` | **`ShadowDusk.Slang.Wasm` NuGet package** (issue #366) |
| Android | none (spawn measured blocked, Phase 67 §2.5) | **P/Invoked NDK build of slang's library**, proven on the emulator lane |
| win-arm64, linux-arm64 | `SD0620` (no bundled slang, decision #286 / PR #390) | unchanged unless the owner asks |

Non-negotiables carried forward unchanged:

- **Same compiler, same pin, same bytes.** The in-process route calls the pinned slang v2026.14.1
  library that already ships beside `slangc` (on Android, an NDK build of the same tag), so it is
  not a substitute compiler. The acceptance gate is byte identity with `slangc` on the full Slang
  corpus on all three desktop OSes, in CI, before it becomes the default.
- **Slang is an INPUT language only.** The library turns `.slang` into HLSL; DXC / vkd3d compile
  that HLSL exactly as today (Phase 61 §2.1, Phase 67 §1).
- **Seamless.** No consumer step, flag or property. The escape hatch exists only for crash
  isolation and is never the path to correct output.
- **One `SlangCompiler`.** The transports differ; the argument list (`SlangcArguments`), the output
  normalization (`JoinOutputLines`), every rejection, the register pass, the merge and the
  downstream pipeline stay one copy (the Phase 67 decision).

## 2. Owner decisions this phase encodes (2026-10-04)

1. **Desktop: in-process becomes the default route for `ShadowDusk.Slang`**, calling the pinned
   library the package already carries (verified file names in §4.1). The bundled `slangc`
   subprocess stays as a **non-required fallback / escape hatch** (crash isolation), never the path
   to correct output.
2. **Byte identity is the acceptance gate**: in-process output byte-identical to `slangc`'s on the
   full Slang corpus (the 235-run set Phase 67 measured) on Windows, Linux and macOS, run in CI,
   before the default flips.
3. **Runtime cost is product cost.** The owner rejected #337's options (a)/(b) because they add
   `slangc` launches to shapes that already compile correctly. This phase measures compile latency
   (spawn vs in-process, cold vs warm, session reuse) and notes that (a)/(b) may become acceptable
   once a `-E` pass is an in-process call. **It proposes re-measuring; it does not decide** (Area F).
4. **Browser: XnaFiddle must compile full Slang in real time in the page.** `.fx` and the
   HLSL-compatible `.slang` subset already do (`ShadowDusk.Wasm`); full Slang needs
   `ShadowDusk.Slang.Wasm` as a package. Issue #366 is now **needed** and is Area E.
5. **Android: the recorded decision stands** (P/Invoked NDK build of slang's library; no spawn;
   `AndroidUseLegacyPackaging` forbidden by the seamless directive). Area G, measured on
   `android-emulator.yml`. This re-opens the Phase 67 "Android not planned" line.
6. **win-arm64 / linux-arm64: no bundled slang** (decision #286, PR #390). In-process does not
   change that unless requested; the in-process route reports the same `SD0620` there.

## 3. What is already in place (do not rebuild)

- `SlangCompiler` has an internal in-process seam: `delegate InProcessSlangc(string slangSource,
  IReadOnlyList<string> arguments) -> (ExitCode, Stdout, Stderr)` and the internal constructor
  `SlangCompiler(IShaderCompiler, InProcessSlangc)` that skips only the executable lookup and
  preparation (`SD0620`/`SD0621`/`SD0623`). `InvokeSlangc` applies `SlangcArguments.JoinOutputLines`
  to the in-process text so both transports land on the same string.
- Three argument shapes, all in `SlangcArguments`: `Build` (per entry, ends `-- -`),
  `BuildPreprocess` (`-E -- -`) and `BuildPreprocessFiles` (`-E -- [-] files...`, paths relative to
  the compile's working directory, which is `slangc`'s own directory).
- `SlangInProcessRouteTests` pins the argument list and the normalization and runs the 21-shader
  manifest through the seam with native `slangc` behind it.
- Phase 67's wasm glue (`.wasm-build/slang-wasm/slangc-wasm-glue.cpp`) replays `slangc`'s
  `innerMain` and its node gate is the 235-run identity harness
  (`node-test-slangc-wasm.mjs`: four corpora, every `[shader]` entry, five targets' macros).
- `slang-manifest.json` + `SlangCrossHostByteIdentityTests` already assert one set of bytes on
  Windows, Linux and macOS.

## 4. Research (2026-10-04)

Everything in §4.1-4.3 was verified by inspection (dumpbin, a symbol scan of the Unix libraries,
and slang's source at the `v2026.14.1` tag). §4.4 was measured with a throwaway Python `ctypes`
probe on win-x64 only (kept in [`PHASE-68-appendix/inprocess-probe/`](PHASE-68-appendix/inprocess-probe/);
not product code, not run by any test).

### 4.1 What the package already carries

Exactly two natives per bundled RID (the Phase 66 minimal set; `tools/verify-slang-nupkg.sh`
checks the eight entries by exact name):

| RID | Library (the in-process target) | Bytes | Executable (fallback) |
|---|---|---|---|
| win-x64 | `slang-compiler.dll` | 25,334,784 | `slangc.exe` |
| linux-x64 | `libslang-compiler.so.0.2026.14.1` | 33,659,352 | `slangc` |
| osx-x64 | `libslang-compiler.0.2026.14.1.dylib` | 31,352,480 | `slangc` |
| osx-arm64 | `libslang-compiler.0.2026.14.1.dylib` | 29,306,992 | `slangc` |

So the in-process route adds **no new native to the package and no size** on desktop. The win-x64
library depends only on `SHELL32`, `ADVAPI32`, `KERNEL32` and `ole32` (its C runtime is linked
statically, which matters for §4.3).

The Windows library exports 458 symbols, among them the whole legacy compile-request C API:
`slang_createGlobalSession2`, `spCreateCompileRequest`, `spDestroyCompileRequest`,
`spProcessCommandLineArguments`, `spCompile`, `spSetWriter`, `spGetWriter`,
`spSetDiagnosticCallback`, `spGetDiagnosticOutput`, `spAddSearchPath`, `spSetFileSystem`,
`spAddTranslationUnit`, `spAddTranslationUnitSourceString(Span)`, `spAddEntryPoint(Ex)`,
`spGetEntryPointSource`, `slang_shutdown`. These are declared in `slang-deprecated.h` at the pin
("kept for source/binary compatibility ... will drop these declarations over time"), but `slangc`
itself is built on them.

**`slangc` imports exactly nine symbols from the library** (dumpbin on win-x64; the same set found
in the Linux and macOS executables):

| Import | C export? | Spelling per ABI |
|---|---|---|
| `spCreateCompileRequest`, `spProcessCommandLineArguments`, `spCompile`, `spSetWriter`, `spDestroyCompileRequest`, `slang_shutdown`, `slang_createGlobalSessionWithoutCoreModule` | yes (`extern "C"`) | plain (`_` prefix on Mach-O) |
| `spSetCommandLineCompilerMode` | **C++-mangled** | MSVC `?spSetCommandLineCompilerMode@@YAXPEAUICompileRequest@slang@@@Z`; Itanium `_Z28spSetCommandLineCompilerModePN5slang15ICompileRequestE` (Linux and macOS, present in both libraries) |
| `slang_createGlobalSessionImpl` | **C++-mangled**, takes an internal struct | not needed: `slang_createGlobalSession2` calls it with a default `GlobalSessionInternalDesc` (`source/slang/slang-api.cpp`), which is what a release `slangc` passes |

**Conclusion: everything `slangc` does to the library is reachable from managed code through the
library's own exports, with no native code of ours.**

### 4.2 What `slangc`'s `innerMain` does, and what each step means in-process

`source/slangc/main.cpp` at the pin, step by step:

| `slangc` step | In-process equivalent | Note |
|---|---|---|
| global session: `SlangGlobalSessionDesc{}` with `enableGLSL = true`, non-bootstrap | `slang_createGlobalSession2(&desc)`, created once per process | Measured ~104 ms (§4.4); `slangc` pays it on every run. |
| `setSessionDefaultPreludeFromExePath(argv[0])` | none | Sets only the C++ and CUDA preludes (`source/core/slang-test-tool-util.cpp`); irrelevant to `-target hlsl`, and the prelude files are not in the minimal set anyway. |
| `compileRequest->addSearchPath(<slangc's directory>)` | `spAddSearchPath(<the library's directory>)` | Same directory in the package. |
| `spSetWriter(DIAGNOSTIC, <stderr writer>)` | `spSetWriter` for DIAGNOSTIC, STD_ERROR and STD_OUTPUT with a managed `ISlangWriter` whose `isConsole()` is false | **Mandatory**: the library's default STD_OUTPUT writer is a `FileWriter` on the C runtime's `stdout` (`slang-end-to-end-request.cpp`), so without it the HLSL would be printed to the game's console. |
| `spSetCommandLineCompilerMode(request)` | the mangled export (or the `ICompileRequest` vtable slot) | **Mandatory**: it is what makes the matrix layout default **column-major** ("legacy slangc tool defaults to column major layout") and turns on command-line output behaviour. Its absence is exactly the `pack_matrix(row_major)` divergence Phase 67 measured in upstream's session-API module (0/20). |
| `spProcessCommandLineArguments(argv[1..])` | the same call, fed `SlangcArguments`' list | See §4.3 for the one token that cannot be fed verbatim. |
| `spCompile` inside `try { } catch (Exception)` printing `internal compiler error` | `spCompile` | `EndToEndCompileRequest::compile` already catches `AbortCompilationException` and `Exception` itself; the outer catch is a second net that managed code cannot replicate for C++ exceptions (open question Q3). |
| exit code `TestToolUtil::getReturnCode(res)` | the same mapping in managed code | `CompilationFailed = -1`, `Failed = 1`, `Ignored = 2`; the process route then sees the OS's view of `-1` (Windows keeps it; Unix truncates to 255). `SlangDiagnosticReformatter.SelectPrimary` reads the exit code, so the in-process transport must return what the process route would have seen on that OS. |
| `slang::shutdown()` at exit | never (process lifetime) or at unload | Memory measured in Area B. |

### 4.3 Hazards found by reading the source

1. **`-` reads the process's real C `stdin`.** `OptionsParser::addInputStdin` calls
   `_readStdinSource(stdin, ...)` (`fread`; on Windows `_setmode(_O_BINARY)` on fd 0) and adds the
   bytes as a translation unit named `<stdin>`. In a game that means blocking on, or consuming, the
   game's own stdin. Phase 67's glue reopened `stdin` on a MEMFS file, which is harmless in a
   browser but **not an option on desktop**: it is process-global, races every thread, and on
   Windows the library's statically linked C runtime keeps its own `stdin`, which the host cannot
   reach at all. **The `-- -` tail is the one part of the argument list the desktop transport cannot
   pass verbatim.**
2. **The source cannot be added after the parse instead.** With no input on the command line,
   `parseOptions` fails with `EntryPointsNeedToBeAssociatedWithTranslationUnits` before returning
   (`slang-options.cpp`), and re-creating the entry points through `spAddEntryPoint` would
   re-implement the parser's own association rules (a second copy that can drift, which is the
   Phase 23 defect at small scale). Rejected.
3. **The GLSL builtin module.** Because `enableGLSL = true`, every global-session creation first
   tries a **bare-name** load of a `slang-glsl-module` shared library (`tryLoadBuiltinModuleFromDLL`
   → `SharedLibrary::load("slang-glsl-module")`, version-tag checked), then a cache file
   `slang-glsl-module.bin` **beside the library** (found through the library's own path), and on a
   miss compiles the module and tries to write that cache file there. Two consequences: the
   bare-name load is the #270 / #350 PATH-hijack class, now inside the game process (the child
   process has the same exposure today); and the writable-directory half of `SlangNativeCache` still
   matters for speed (a read-only package directory means recompiling the module on every
   session), though no longer for correctness.
4. **Stack.** `slangc.exe` reserves 1 MB (PE header, measured); a Unix `slangc` main thread gets the
   default 8 MB. slang recurses per nesting level (Phase 67 §2.4). An in-process call on a .NET
   pool thread gets CoreCLR's secondary-thread stack (smaller than 8 MB on Unix; exact sizes to be
   measured in Area A), so a deeply nested but valid shader that `slangc` compiles could overflow,
   and **a stack overflow in-process kills the game; .NET cannot catch it**. The transport must run
   the native call on a dedicated thread with an explicit stack size (decision D4).
5. **Working directory.** `slangc` runs with its own directory as the working directory
   (`SlangCompiler.PrepareProcessSlangc`), so relative paths (quoted `import`s, `__include`,
   `BuildPreprocessFiles`' inputs) resolve there. In-process they would resolve against the game's
   working directory, which is process-global and must not be changed. Parity needs either absolute
   paths or a file system that resolves relative paths against that same directory (see S1 in §5).
6. **Process-global side effects to audit.** No `setlocale`, `uselocale`, `sigaction` or
   `pthread_atfork` name appears in the Linux or macOS library (string scan); `signal`, `dlopen`,
   `getenv`, `__cxa_atexit` (and `freopen` on macOS) do. Windows imports
   `SetUnhandledExceptionFilter` (the static C runtime's fail-fast path), `LoadLibrary*`,
   `GetEnvironmentVariableA`. Area A turns this string scan into a symbol-table audit and a
   `DxcSetlocaleAudit`-style runtime check (the DXC precedent: issues #256, #312 and the LLVM
   signal-handler finding in `project_facts.md`).
7. **API status.** The compile-request API is in `slang-deprecated.h`. It stays as long as `slangc`
   is built on it, and pins move only deliberately, but every Slang pin bump must re-run Area C's
   identity gate (it would anyway: a bump re-baselines `slang-manifest.json`).

### 4.4 Probe measurements (win-x64 only, throwaway `ctypes` probe)

The probe loads `slang-compiler.dll` **by absolute path** and replays §4.2 exactly: one global
session (`enableGLSL`), `spCreateCompileRequest`, `spAddSearchPath(<library dir>)`, `spSetWriter`
on all three channels with a callback `ISlangWriter` (`isConsole() == false`; the analogue of a
managed writer), the mangled `spSetCommandLineCompilerMode`, `spProcessCommandLineArguments`,
`spCompile`, `spDestroyCompileRequest`, and the `ToolReturnCode` mapping.

| Measurement | Result |
|---|---|
| **Verbatim lists including `-- -`**, the probe process's own stdin piped (fresh process and session per run): every `.slang` under `tests/fixtures/shaders` (28 files: `slang`, `slang-adversarial`, `slang-sksl`), every `[shader]` entry plus the `-E` pass, OpenGL macros | **61/61 identical** to `slangc.exe` (exit code, stdout, stderr after the `JoinOutputLines` normalization) |
| Same harness, three failing sources (syntax + undefined identifier, missing `import`, `#error`) with entry and `-E` | **6/6 identical**, diagnostics included (`--> <stdin>:5:26` line and column, exit code `-1`) |
| **One shared global session**, 244 sequential runs: the 28 files x 4 macro sets (OpenGL and DirectX exact; Vulkan- and FNA-like approximations) x every entry plus `-E`, file-path input on both sides | **244/244 identical**: no state leaked between compiles in one session |
| `slangc.exe` spawn, per run | median **146 ms** (129-421 ms) over the 61 runs; ~160 ms with a file input |
| Library load (`LoadLibrary`, absolute path) | 1.7 ms |
| Global session creation (once per process) | **~104 ms** |
| First compile in a fresh session | median 14 ms (61 runs) |
| Warm compile, same session (`Plasma.slang` / a generics shader, 30 reps) | median **14 / 18 ms** |
| Warm `-E` pass, same session | median **0.3 ms** |
| 244 runs in one session, per run | median **7.2 ms**, p95 26 ms, 1.9 s total (the same runs spawned would take ~35 s) |

What this shows and what it does not:

- **Shown:** the C API path (`spProcessCommandLineArguments` with `SlangcArguments`' list) is the
  same compiler behaviour as `slangc`, byte for byte, including diagnostics and the `-E` pass, and
  a reused session does not perturb output. In-process is roughly **10x faster per compile** and the
  `-E` pass drops from a process start to well under a millisecond.
- **Not shown:** the desktop transport cannot use `-- -` in a game (§4.3 item 1), so the source
  delivery mechanism is unmeasured; Linux and macOS are unmeasured; the probe is not the 235-run
  Phase 67 harness and its Vulkan/FNA macro sets were approximate; concurrency, stack depth,
  memory growth and the exit code on Unix are unmeasured. All of that is Area A.

## 5. Which entry point (recommendation)

**Candidate 1: managed P/Invoke replay of `slangc`'s `innerMain` against the shipped library.
RECOMMENDED.** A new internal transport in `ShadowDusk.Slang` loads the bundled library by absolute
path, keeps one global session, and per call does exactly §4.2's sequence, feeding
`SlangcArguments`' list to `spProcessCommandLineArguments`. Reasons:

- It is the same compiler entry `slangc` uses (its import table is the proof), so identity is by
  construction and the probe already measured it (61 + 6 + 244 runs identical).
- **No new native artifact.** Nothing to build, sign, notarize, hash-pin or host per RID; the
  library is already in the package and already SHA-256 pinned by `tools/restore.*`.
- It generalizes to Android unchanged (an NDK-built `libslang-compiler.so` exports the same C API
  and the Itanium-mangled `spSetCommandLineCompilerMode`), so desktop and Android share one managed
  transport.
- The two awkward pieces are small and in managed code: an `ISlangWriter` COM object (a static
  vtable of `[UnmanagedCallersOnly]` function pointers, like the probe's) and binding the mangled
  export per ABI (two spellings, or the `ICompileRequest` vtable slot taken from `slang-deprecated.h`).

**Candidate 2: a thin native glue library per RID replaying `innerMain`** (Phase 67's glue, built
for desktop). Kept as the fallback if Area A finds that a managed writer or file system cannot reach
identity. Costs: a new native per RID (four desktop RIDs plus Android) with its own build workflow,
pins, macOS signing and the "never take a native without Linux and macOS builds" rule; and it does
**not** solve the stdin problem (the glue cannot reach the Windows library's private C runtime
either).

**Candidate 3: the modern `IGlobalSession` / `ISession` API. Rejected** on measured evidence: Phase
23 and Phase 67 found flags do not forward (0/20), and §4.2 explains the main divergence (the
session API never calls `setCommandLineCompilerMode`, so it defaults to row-major). Re-expressing
every `slangc` flag as session options would be a second option parser to keep in sync.

**The one open sub-question: how the source reaches the parser without `stdin`.** Area A measures:

- **S1 (lead): an in-memory `ISlangFileSystem` and the input name `<stdin>`.** The transport replaces
  the final `-` with the literal input path `<stdin>` (still `-lang slang`), and sets a managed
  file system (`spSetFileSystem`) that serves the source bytes for that name and delegates every
  other path to the real file system, resolving relative paths against the directory `slangc` would
  have run in (also fixing §4.3 item 5). By source reading, stdin input and file input both become a
  translation-unit artifact named by its path, so `#line` and diagnostics should read `<stdin>`
  exactly; what must be measured is canonical-path / unique-identity handling for **imported**
  files once a custom file system is in the chain (a plain `ISlangFileSystem` is wrapped in slang's
  `CacheFileSystem`; an `ISlangFileSystemExt` must reproduce `OSFileSystem`'s answers).
- **S2: redirect `stdin`.** Rejected (process-global, unreachable on Windows, §4.3 item 1).
- **S3: ask upstream** for a caller-supplied stdin blob (or for `-` to be served through the
  request's file system). File it in Area A regardless; it would let the list be passed verbatim.
  We never patch slang itself (project rule: never fork compiler internals).
- **S4: drop `-- -` and add the source after parsing.** Rejected by source reading (§4.3 item 2).

Whichever wins, the deviation is one token in a list that otherwise reaches slang verbatim, it is
the same kind of transport detail Phase 67's MEMFS stdin is, and Area C's identity gate decides it.

## 6. Design (target shape; subject to Area A)

- **Transport selection.** The public `SlangCompiler()` constructor resolves the bundled library
  (`SlangToolPath`, extended with the library path) and uses the in-process transport by default.
  The process transport stays reachable as the escape hatch (decision D1 picks its shape). On an
  unbundled host (win-arm64, linux-arm64, older macOS) both report the existing `SD0620`.
- **Native loading.** `NativeLibrary.Load(<absolute path>)` only, after a SHA-256 check of the file
  against the pin (the issue #350 rule for vkd3d and SPIRV-Cross); never a bare name, never
  `DllImport` name probing. The bare-name `slang-glsl-module` probe inside slang (§4.3 item 3) is
  recorded as a known exposure, reported upstream, and measured in Area A (where the loader looks).
  `SlangNativeCache`'s execute-bit work is unnecessary for the in-process path (a library is mapped,
  not executed); its writable-directory logic is kept only if Area B measures that the GLSL module
  cache write matters for cold latency.
- **Threading.** One global session per process, created lazily. Start by serializing every native
  call behind one lock (Phase 67 §3.3's plan, the safe default for a session whose concurrent use
  slang does not document); Area B measures whether per-thread sessions are identical and worth it.
  `SlangCompiler.CompileAsync`'s `Task.Run` stays; the native call itself runs on a dedicated thread
  with an explicit stack (D4).
- **Output and diagnostics.** A managed `ISlangWriter` per channel, `isConsole() == false`, bytes
  decoded as UTF-8 and passed through the existing `JoinOutputLines`, so every diagnostic keeps
  slang's own file, line, column and text verbatim (the probe matched `<stdin>:5:26`). Source bytes
  in are UTF-8 without BOM, as the process route writes stdin; arguments are UTF-8 (what `slangc`'s
  `wmain` converts to).
- **Exit code.** Map `SlangResult` through `ToolReturnCode`, then to what the process route would
  have observed on that OS, so `SlangDiagnosticReformatter` produces the same message.
- **Memory and lifetime.** The session lives for the process (no `slang_shutdown`); each request is
  destroyed after its call; writer buffers are per call. Area B records RSS over thousands of
  compiles (the wasm module measured flat: 216.5 MB after the first compile, flat over 3000).
- **Crash isolation.** In-process, a native crash or stack overflow kills the game, so:
  1. **Known crash triggers are refused or isolated before the native call.** Today's only known
     trigger is the #323 / #337 constant-buffer-member namespace pair, which crashes `slangc` and is
     surfaced after the fact as `SD0622`. That residue exists only because detecting it costs a
     `-E` pass; in-process that pass costs ~0.3 ms, so Area F re-measures #337 option (a) as the
     pre-refusal. Until a decision lands, a source that could reach the trigger (spells `namespace`
     and a constant buffer, and no read text proves the pair absent) is routed through the process
     transport (D2).
  2. **The fallback never runs automatically to mask a crash** (it cannot: the process is gone) and
     never re-runs a compile that failed in-process; it is chosen up front, by the pre-refusal rule
     or by the consumer's escape hatch.
  3. Every crash found in-process becomes a pinned regression fixture and an upstream report.
- **Packaging.** Desktop: unchanged (same eight natives). Browser: Area E. Android: Area G adds
  `runtimes/android-*/native/libslang-compiler.so` to `ShadowDusk.Slang` (D6 decides which ABIs).
  No change to `ShadowDusk.Compiler` or any other package; the "no Slang cost unless you add the
  Slang package" rule holds everywhere. No package gains a MonoGame dependency.

## 7. Areas

### Area A: spike, the desktop entry point (do first; no product code merges before it)

Verify, on win-x64, linux-x64 and macOS (arm64 natively, x64 under Rosetta as issue #352 does):

- [ ] **A1** the probe's §4.2 sequence from C# (`NativeLibrary`, function pointers, managed
  `ISlangWriter`) reproduces `slangc` on the full 235-run Phase 67 set (port the node gate's corpus
  walk and its five exact macro sets), with **stdin-fed** lists, all three OSes.
- [ ] **A2** source delivery: S1 measured on the same set **plus import-using sources** (quoted-path
  imports, `__include`, `BuildPreprocessFiles` inputs, a missing import) and against relative-path
  resolution from `slangc`'s directory; S3 filed upstream either way. Exit: one mechanism at 235/235
  (or a written reason to fall back to Candidate 2).
- [ ] **A3** the mangled `spSetCommandLineCompilerMode` binds on all four RIDs (or the vtable slot
  does) and `slang_createGlobalSession2` behaves as the release `slangc`'s `Impl` call (identity
  covers it).
- [ ] **A4** stack: measure CoreCLR's secondary-thread stack per OS, run Phase 67's depth cases
  (400 added terms, 300 parens, 200 ternaries, 300 nested ifs, 400 else-ifs, and the bisected first
  failing depths) in-process on a dedicated thread at the size D4 picks, compare to `slangc`.
- [ ] **A5** process-global audit: symbol tables (not strings) for `setlocale`/`signal`/`sigaction`/
  `atexit`/`freopen`/`dlopen`/`getenv`; a runtime check that the locale, signal dispositions and
  `stdin`/`stdout` are untouched after compiles; where the bare-name `slang-glsl-module` load looks
  on each OS.
- [ ] **A6** latency table on all three OSes (cold process, cold session, warm, `-E`), the numbers
  Area F and the docs use.
- **Exit criteria:** a written result appended to this doc (§4.4 extended), the transport shape
  chosen, the decisions in §12 answered or re-asked with data.

### Area B: the desktop transport

- [ ] Internal `NativeSlangc` transport behind the existing `InProcessSlangc` seam; absolute-path,
  hash-checked load; lazy global session; serialized calls; dedicated-thread stack; managed writers;
  exit-code mapping; the chosen source delivery.
- [ ] `SlangToolPath` resolves the library as well as the executable; the RID gating (`SD0620`) is
  shared by both transports.
- [ ] Memory: RSS over 3000 compiles recorded; session reuse confirmed identical (the probe's 244-run
  check, ported).
- [ ] Concurrency: N threads compiling the manifest in parallel stay identical and never deadlock;
  only then consider relaxing the lock.
- **Acceptance:** every existing Slang test passes through the in-process transport unchanged
  (selected by the internal seam, not yet the default).

### Area C: the identity gate in CI (the acceptance gate for the default flip)

- [ ] An integration test (`[Trait("Category","Integration")]`, real natives, fails rather than
  skips on a host without them, per the Slang soft-skip decision) that runs the 235-run set through
  both transports and compares exit code, stdout and stderr byte for byte, including the depth cases.
- [ ] It runs on `ubuntu-latest`, `windows-latest` and `macos-latest` (arm64, plus osx-x64 under
  Rosetta) in the existing integration lane (`run-integration` label); the slow-test budget rules
  apply (`TestBudget.Compile`; re-measure the inactivity gap per `project_rules.md`).
- [ ] `slang-manifest.json` through the in-process transport on all three OSes (already one set of
  bytes; now through the new transport).
- **Acceptance:** green on all three OSes in CI, with the measured count recorded here.

### Area D: make it the default

- [ ] Flip the public constructor's default to in-process; keep the escape hatch per D1.
- [ ] Crash pre-refusal / process routing per D2 (pinned test for the #323 cbuffer-member shape
  proving the game process survives).
- [ ] Full `dotnet test ShadowDusk.slnx` plus the Windows render gate, whose Slang gates must pass
  **unchanged** through the in-process route: `SlangCorpus`, `SlangFullCorpus` (DX11 real `Effect`),
  `SlangFullCorpusDx12`, `SlangFullCorpusVulkan`, `SlangTexturedGl`, `FnaValidation -- slang`
  (`-IncludeFna`), and the Slang arms of the CI lanes (`SlangFullCorpus` on the DX WARP lane, the GL
  gates on llvmpipe).
- [ ] `Pack & Consume` (label `run-integration`) cold consumer compiles full Slang in-process on each
  desktop OS, with no consumer step.
- [ ] Docs (Area H) in the same PR.

### Area E: `ShadowDusk.Slang.Wasm` as a NuGet package (issue #366, now needed for XnaFiddle)

Phase 67 §4's remaining list, unchanged in substance:

- [ ] Dispatch workflow building `shadowdusk-slangc.{js,wasm}` from the recipe; artifact attached by
  hand to a fixed `native-slangc-wasm-2026.14.1` release tag (never republished); both hashes pinned
  in `tools/restore.{ps1,sh}`; a `VerifySlangcWasmPresent` pack guard; the CI job restores the hosted
  module instead of building it.
- [ ] emsdk clone pinned to a tag or commit (the script clones HEAD today).
- [ ] Third-party notices for slang, miniz, lz4, cmark-gfm.
- [ ] `release.yml` and `pack-consume.yml` entries; a **cold browser-consumer check** (a Blazor WASM
  project that adds only `ShadowDusk.Slang.Wasm` and compiles a full-Slang shader in the page).
- [ ] **Size / opt-in story:** the 23 MB module rides only in consumers that add
  `ShadowDusk.Slang.Wasm`; assert a `ShadowDusk.Wasm`-only consumer's publish carries no slangc
  module, and a Slang.Wasm consumer's publish carries no desktop natives (issue #273).
- [ ] Committed tests for `SD1904`, the concurrent-trap retry and the cold synchronous `SD1903`; an
  FNA arm in `browser-slang-gate.mjs`; the module's memory footprint recorded.
- [ ] **Prove it on the requester's input** (project rule): XnaFiddle-shaped use, a KNI Blazor WASM
  page compiling full Slang live from the package, on @vchelaru's own example shaders where available.
- [ ] Package count "ten" → "eleven" everywhere it is stated (CLAUDE.md, project_facts.md, README,
  docfx, RELEASING.md, the `/release` skill).

### Area F: compile latency and the #337 re-measure (measure, do not decide)

- [ ] Publish the A6 latency table (spawn vs in-process; cold process, cold session, warm; `-E`;
  session reuse) for all three OSes, plus what a game sees per `SlangCompiler.Compile` end to end.
- [ ] Re-measure #337 options (a) (`-E` over namespace-and-directive sources) and (b) (read each
  quoted-path module on import) as in-process calls: added cost per compile, and which residue shapes
  each closes. Present to the owner; the 2026-10-04 rejection stands until the owner reverses it.
- [ ] Re-check the other places that were shaped by spawn cost (the #252 / #292 register pass gating,
  the #323 confirmation run) and list, without changing them, any whose trade-off moves.

### Area G: Android in-process (the recorded NDK decision)

- [ ] Build slang's compiler library at `v2026.14.1` with the NDK for `android-arm64` and
  `android-x64` (the `build-dxc-android.sh` / `dxc-android-build.yml` precedent: a dispatch workflow,
  artifacts hosted under a fixed tag, SHA-256 pinned). Measure size and build effort first (unknown;
  upstream publishes no Android build). Only the `-target hlsl` path is needed (no `slang-llvm`, no
  glslang), as on desktop.
- [ ] The same managed transport as Area B (no glue): it P/Invokes the NDK library by absolute path
  from the mapped image (the Android DXC precedent verifies the mapped image by GNU build id, since a
  Release APK does not extract libraries).
- [ ] On-device identity: the 235-run set (or the manifest) compiled on the API-34 x86_64 emulator
  matches desktop bytes; a full-Slang shader loads as an `Effect` in `validation/AndroidGl`.
  Runs on `android-emulator.yml` (label `run-android`).
- [ ] Default packaging only (no `AndroidUseLegacyPackaging`, no `extractNativeLibs`).
- [ ] Update `docs/validation-matrix.md` §7's "Full Slang on Android" row (closed 2026-10-03 as not
  planned; this phase re-opens it by owner decision).

### Area H: docs and support surfaces (in the PR that ships each area)

Per CLAUDE.md's support-surface list: `docs/pipeline-overview.puml` + regenerated
`docfx/images/pipeline-overview.svg` (the Slang box's transport), `docs/the-purpose.md` (backend
table and host x target matrix: full Slang in-process on desktop, browser, Android),
`docs/validation-matrix.md` (cells, a §6 row for any new driver with its exact command, §7 rows for
#366, Android, and any new gap), `docs/repository-layout.md`, README (targets table, "How the
pipeline works", package count), the DocFX pages (`index.md`, `getting-started/overview.md`,
`guides/choosing-a-target.md`, the Slang backend page, `contributing/validation.md`, `glossary.md`,
the architecture pages), `src/ShadowDusk.Slang/README.md`, XML doc-comments on `SlangCompiler`,
`project_facts.md`, `project_decisions.md`, `plan/plan.md`, `CHANGELOG.md`, CLAUDE.md (package count,
gate list if it changes), `RELEASING.md` and the `/release` skill (new release chores: the slangc
wasm module, any Android natives).

## 8. Evidence bar per rung

| Rung | Desktop in-process | Browser package | Android |
|---|---|---|---|
| 1 compiles | every Slang test green through the in-process transport | cold browser consumer compiles in the page | on-device compile succeeds |
| 2 well-formed | unchanged (same `.fx`, same pipeline) | manifest keys parse | `Effect` loads |
| 3 matches reference in our renderer | **identity with `slangc`**: 235/235 on Windows, Linux, macOS in CI (Area C); `slang-manifest.json` identical | manifest bytes in headless Chromium (already 42/42 in-repo) | manifest bytes on the emulator |
| 4 real engine vs reference compiler | the existing Slang gates (DX11, DX12, Vulkan, GL, FNA) pass **unchanged** through the in-process route | KNI WebGL render (already 18/18 in-repo), now from the package | `validation/AndroidGl` loads and draws a full-Slang effect |

Byte identity with `slangc` is what makes rung 4 carry over: identical HLSL in means the downstream
pipeline, already rung-4 proven per target, produces identical effects.

## 9. Tests and CI

- **Unit (pure):** writer normalization, exit-code mapping, transport selection, the S1 file-system
  name mapping, the pre-refusal rule. No disk, no process, no native.
- **Integration (real natives):** the identity gate (Area C), concurrency, depth, memory, the crash
  pre-refusal survival test; tagged `Integration`, run on all three OSes.
- **Lanes:** `ci.yml` integration (`run-integration`), `pack-consume.yml` (cold consumers, desktop and
  browser), `wasm.yml` `browser-smoke` (`run-browser`), `android-emulator.yml` (`run-android`), the
  validation lanes (`run-validation-render`), and the local Windows render gate before merge.
- A new driver, if any, gets a `docs/validation-matrix.md` §6 row and a slot in
  `validation/run-windows-render-gates.ps1`.

## 10. Risks

| Risk | Mitigation |
|---|---|
| A native crash or stack overflow in slang kills the game | pre-refusal / process routing of known triggers (D2); dedicated stack (D4); escape hatch (D1); every crash pinned and reported upstream |
| S1 cannot reproduce import resolution byte for byte | fall back to Candidate 2 only if it can; otherwise keep the process transport for import-using sources until S3 lands upstream (decision then) |
| Concurrent use of one session is unsafe | serialize first; measure before relaxing |
| Bare-name `slang-glsl-module` load inside the game process | measured in A5; reported upstream; the exposure already exists in the child process |
| The deprecated compile-request API is removed upstream | pins are deliberate; Area C re-runs on any bump; `slangc` depends on the same API |
| Android build of slang is large or hard | measure first (Area G); the decision allows "not yet" with a loud `SD0620`-style refusal, never a silent subset fallback |
| The 23 MB wasm module leaks into non-Slang browser consumers | Area E's publish assertions |
| Cold session cost (~104 ms) lands on a game's first Slang compile | measured (A6); an optional warm-up call is a consumer convenience, never required |

## 11. Open questions

- **Q1** Does S1 (in-memory file system named `<stdin>`) reach identity for import-using sources, and
  does slang wrap a plain `ISlangFileSystem` or need `ISlangFileSystemExt` for canonical paths?
- **Q2** Is one global session safe for concurrent compile requests, and is a per-thread session pool
  byte-identical and worth its memory?
- **Q3** Can a C++ exception escape `spProcessCommandLineArguments` or `spCompile` into managed frames
  (the `slangc` outer catch has no managed equivalent)? Find out by reading `parseOptions` and by
  fuzzing malformed argument lists in A1.
- **Q4** What stack sizes does CoreCLR give secondary threads per OS, and what size makes the
  in-process depth envelope at least `slangc`'s on every OS?
- **Q5** Where does each OS's loader look for the bare-name `slang-glsl-module`, and does shipping
  nothing leave a hijackable search?
- **Q6** Does the GLSL module cache write beside the library matter for cold latency when the package
  directory is read-only (a shared NuGet cache)?
- **Q7** Android: size and build effort of slang's library with the NDK at the pin; which ABIs.

## 12. Decisions needed (owner)

- **D1 Escape hatch shape.** Recommended: an internal-default, public opt-in on `SlangCompiler`
  (for example a constructor option naming the process transport), documented as crash isolation
  only, never required for correct output. Alternative: an environment variable (no API surface,
  but invisible and process-global).
- **D2 Automatic process routing for known crash triggers.** Recommended: yes, for shapes that can
  reach a known trigger, until Area F's re-measure gives a pre-refusal; never as a retry after an
  in-process failure. Alternative: refuse those shapes up front with a registered code (fail loudly,
  but turns today's "compiles unless it crashes" into a refusal).
- **D3 Fallback on library load failure.** Recommended: no automatic switch to the process transport;
  report a registered diagnostic (both natives come from the same package directory, so a load
  failure is a packaging defect to surface, and switching would hide it). Alternative: switch
  silently (same bytes either way, but hides the defect).
- **D4 Stack size of the dedicated native thread.** Recommended: at least `slangc`'s largest (8 MB) on
  every OS, accepting that past Windows `slangc`'s 1 MB envelope the in-process route may compile a
  shader the child process crashed on (a crash replaced by output, never different output for a
  shader `slangc` compiles). Alternative: match each OS's `slangc` exactly (identical failure depths,
  but a too-deep shader then kills the game on Windows).
- **D5 #337 options (a)/(b)** after Area F's numbers (the owner's 2026-10-04 rejection stands until then).
- **D6 Android ABIs** (arm64 only, or arm64 + x86_64 for the emulator lane) after Area G's size
  measurement.

## 13. Non-goals

- Bundling slang for win-arm64 or linux-arm64 (decision #286; revisit only on request).
- Using Slang as an output language or as a substitute for DXC / vkd3d anywhere.
- Upstream's session-API `slang-wasm.js`, or the session API on desktop.
- Building slang from source for desktop (the upstream release library is the artifact; Android is
  the one host with no upstream build).
- Removing the bundled `slangc` (kept as the escape hatch).
- Byte identity with `mgfxc` for Slang (no reference compiler reads Slang; the bar is identity with
  `slangc` plus the existing Slang render gates).
- Compute, mesh or ray-tracing stages (Phase 66 non-goal, unchanged).
- Lifting upstream's macOS 26 floor (#237, closed by decision).

## 14. How to reproduce the research probe (win-x64)

```powershell
./tools/restore.ps1                      # restores tools/slang/win-x64/
$P   = (Resolve-Path plan/PHASE-68-appendix/inprocess-probe).Path
$Lib = (Resolve-Path tools/slang/win-x64).Path          # absolute: the probe runs from $P
python $P/drive.py       $P $Lib tests/fixtures/shaders   # verbatim '-- -' lists, stdin piped
python $P/drive-multi.py $P $Lib tests/fixtures/shaders   # one shared session, 244 runs
```

Both print an identity count and timings. `slangc` writes `slang-glsl-module.bin` into
`tools/slang/win-x64/` on its first run (expected; Phase 66 A3).
