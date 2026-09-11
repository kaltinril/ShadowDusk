# Phase 66 — Full, slangc-backed Slang input (general product capability)

**Track:** Additive package / reach. Additive only — `ShadowDusk.Compiler`'s existing `.slang`
subset frontend (`SlangFrontend.cs`) and every existing output byte are untouched; this phase adds
a new, separate, opt-in package.

**Status:** 🟡 In progress. Scoped 2026-09-11; A1-A4 done same day (native vendoring +
`ShadowDusk.Slang.SlangCompiler`, the real compile route, the mangling fix and the OpenGL
row_major gap fix — corpus now 21/21 on both DirectX_11 and OpenGL). A5-A8 open.

**Depends on:** [Phase 61](DONE/PHASE-61-slang-support.md) (the shipped HLSL-compatible-subset
frontend and its groundwork §6/§7/OQ2/OQ3) and [Phase 65](PHASE-65-full-slang-input-spike.md) (the
spike that re-measured Phase 61's open questions against real slangc v2026.14.1 and is this
phase's evidence base — **read it before writing any code**). This phase does not re-derive either;
it acts on them.

> [project_decisions.md](../project_decisions.md), 2026-09-11: owner overruled Phase 65's
> "no demonstrated demand" recommendation. The bet: community interest in "ShadowDusk supports
> Slang" (already circulating before the messaging gap was even known) plus a **complete**
> implementation is worth the packaging cost, rather than waiting for a named consumer who needs
> arbitrary runtime Slang. Record the bet here so it isn't re-litigated mid-implementation: the
> demand question was heard and answered by the owner, not left open.

---

## 1. What this is

**Real slangc, shipped as its own package.** A consumer who adds `ShadowDusk.Slang` gets genuine
Slang — `import`, generics, `interface`s, everything real slangc accepts — compiled through
**real slangc → HLSL**, then handed to the **existing, unchanged, faithful pipeline** (the same
DXC every `.fx` uses). A consumer who does not add the package is completely unaffected: no size,
no dependency, no behavior change. This is the "(c)" option from Phase 65 §6, chosen over "(a)
messaging fix only" and "(b) author-time-only CLI" — not instead of them; §6 below folds both in
as cheap, immediate wins alongside the real build.

**Non-negotiables, carried forward unchanged from Phase 61 (do not re-litigate):**

- Slang **never** substitutes for DXC anywhere in the pipeline (Phase 61 §2.1, closed on measured
  evidence: two DXC flags — `-fvk-use-dx-layout`, `-fvk-use-entrypoint-name` — do not forward
  through Slang's API, so byte-identity on arbitrary input is unprovable). The route is
  strictly `.slang → [real slangc] → HLSL → [existing faithful DXC pipeline, untouched]`.
- The FX9 technique/pass synthesis convention already shipped in `SlangEntryScanner.cs` /
  `SlangFrontend.cs` ([shader("vertex")] / [shader("fragment")] discovery, synthesized
  technique block) is **reused as-is** for the real-slangc route. Only the "compile the body"
  step changes — from "straight to DXC" to "through slangc to HLSL, then DXC."
- Acceptance rule is Phase 61 §6 A6's, now actually buildable because the toolchain exists:
  **accept whatever real slangc accepts; reject only what MonoGame/KNI/FNA cannot hold — loudly,
  by name, never approximated.** Compute/mesh/raytracing entry points stay a **non-goal**
  ([Phase 58](DONE/PHASE-58-extended-shader-stages.md): stock MonoGame/KNI hold only VS and PS),
  rejected with a registered diagnostic naming the construct, never silently dropped.
- No route through this frontend is `mgfxc`-equivalent (`mgfxc` cannot read Slang at all); never
  claim it as one, and never add `.slang` as a `docs/validation-matrix.md` §1 cell.

---

## 2. Packaging shape

**New standalone package: `ShadowDusk.Slang`** — the `ShadowDusk.ShaderToy` precedent (its own
`PackageId`, its own optional `PackageReference`, zero weight on anyone who doesn't add it). This
is the answer to Phase 65 §4's "does the whole product need to carry 121 MB/RID" concern: no —
only a consumer who explicitly opts in does, and even then only for the one RID they build for.

**A1 measured this (2026-09-11, evidence in `plan/PHASE-66-appendix/slang-llvm-exclusion-probe/`):
`slang-llvm.dll` is excludable.** It is Slang's LLVM/Clang-backed pass-through compiler for
CPU/host codegen targets (`-emit-cpu-via-llvm`, `host-callable`, `shader-object-code`) — orthogonal
to the `-target hlsl` source-to-source route this product uses. Deleting it from a copy of the
pinned v2026.14.1 win-x64 release and running `slangc -target hlsl` against all 24 entry points of
the existing corpus (the 17-shader `tests/fixtures/shaders/slang/` set plus Phase 65's Gum-shaped
and generics-probe shaders, generics/interfaces included) compiles cleanly with no change in
behavior; `slangc -v` also still launches. A positive control confirms the deletion is
load-bearing, not incidental: `-target hlsl` still exits 0, but
`-target shader-sharedlib -emit-cpu-via-llvm` on the same no-LLVM binary now fails loudly with
`error[E52002]: pass-through compiler not found`. This drops the per-RID native weight from
**~126.2 MB to ~41.8 MB** (measured `bin/` directory totals, same basis as Phase 65 §4c's ~121 MB
figure), a **~67% cut**, before A2 even trims the set down to what's actually vendored
(`slang.exe`/`slangd.exe`/`slangi.exe` are separate CLI/language-server/interpreter binaries not
needed to invoke `slangc.exe`, and are dropped in A2's real package, not measured here). A2 should
design its win-x64 native vendoring around the no-LLVM set from the start.

---

## 3. The work

Ordered; later items depend on earlier ones. Each item's "done" bar is stated so this doesn't
turn into an open-ended slog.

- **A1 — Minimal-build probe (do this FIRST, before any restore-script work). DONE, 2026-09-11.**
  No from-source build needed: deleting `bin/slang-llvm.dll` from the pinned win-x64 release is
  sufficient — `-target hlsl` is unaffected (full corpus re-verified, positive control confirms
  the deletion is load-bearing for LLVM/CPU targets specifically, not incidental). Per-RID cost
  drops from ~126.2 MB to ~41.8 MB (~67% cut), not the ~37 MB estimate exactly but the same order
  — see §2 above and `plan/PHASE-66-appendix/slang-llvm-exclusion-probe/` for the full evidence.
  A2 vendors the no-LLVM set.
- **A2 — Native vendoring, win-x64 first. DONE (scaffolding), 2026-09-11.** Pinned slangc
  v2026.14.1 win-x64 (same pin `validation/SlangCorpus/Program.cs` already uses), added
  `tools/restore.ps1`/`restore.sh` entries mirroring the vkd3d/DXC-macOS pattern exactly
  (download the official release zip, SHA-256-verify the WHOLE zip before extracting — same
  discipline `SlangCorpus`'s oracle restore already uses — then extract only the needed files;
  restored unconditionally on every host, like `restore_vkd3d_shader`/`restore_dxc_macos`, so
  a Linux/macOS CI runner ends up pack-ready for the win-x64 RID too). Also went past A1's
  "probably droppable" guess on `slang.exe`/`slangd.exe`/`slangi.exe` and empirically tested
  **every other file** in the release, one removal at a time, re-running the same 24-entry-point
  corpus after each step (see `plan/PHASE-66-appendix/slang-native-minimal-set-probe/`):
  `slang.exe`/`slangd.exe`/`slangi.exe`, `gfx.dll`/`gfx.slang`, `slang-glsl-module.dll`,
  `slang-glslang.dll`, `slang.dll`, `slang-rt.dll`, `slang.slang`, and the whole
  `slang-standard-module-2026.14.1/` stdlib source directory all turned out droppable too. **The
  true minimal vendored set is `slangc.exe` + `slang-compiler.dll` only — 25,611,264 bytes
  (~24.4 MiB)** — a ~80% cut from the 126.2 MB unmodified release, ~39% further than A1's 41.8 MB
  no-LLVM figure. A negative control (deleting `slang-compiler.dll`) fails all 24 entries,
  confirming the corpus test discriminates rather than passing regardless.
  Side finding for **A3**: `slangc.exe` writes a runtime cache file (`slang-glsl-module.bin`,
  ~1.3 MB) into its own directory on first compile even for `-target hlsl` — the packaged
  `runtimes/win-x64/native/` directory must be writable at runtime, not just readable; not
  solved here.
  Scaffolded the new `src/ShadowDusk.Slang/ShadowDusk.Slang.csproj` package (mirrors
  `ShadowDusk.ShaderToy`'s metadata/multi-targeting shape, `ShadowDusk.HLSL`'s
  `Exists()`-conditioned native-packing shape for the two files above under
  `runtimes/win-x64/native/`, plus a `THIRD-PARTY-NOTICES.txt` for slangc's Apache-2.0
  licence): only depends on `ShadowDusk.Core`, zero native/managed footprint on anyone who
  doesn't add it. C# side is deliberately a stub —
  `SlangToolPath.Resolve()`/`ResolveOrThrow()` locate the packaged native and assert it
  exists; no compile logic (that's A3). Proved with `dotnet pack`: the resulting
  `ShadowDusk.Slang.<version>.nupkg` contains `runtimes/win-x64/native/slangc.exe` (276,480
  bytes) and `runtimes/win-x64/native/slang-compiler.dll` (25,334,784 bytes) at the expected
  paths (verified by inspecting the nupkg's zip entries directly), and `ShadowDusk.HLSL`'s
  own re-packed nupkg was diffed to confirm zero change (same natives, same size, same
  deps). Full solution build + full `dotnet test` both green.
  **Left open:** the release-gate hard-check (`.github/workflows/release.yml`'s
  `pack-desktop` job, `Verify ShadowDusk.HLSL contains all natives...` step and the
  desktop-nupkg-count assertion) is NOT wired for `ShadowDusk.Slang` yet — found but not
  extended, since it also touches the expected-package-count assertions shared with every
  other package; a later stage should add a `Verify ShadowDusk.Slang contains slangc
  (win-x64)` step mirroring the existing ones and bump the count. Other RIDs
  (linux-x64/aarch64, macos-x64/arm64) also follow once this shape is reviewed; Slang
  publishes all of them today (Phase 61 §2.2), so this is packaging work, not a coverage
  gap. Reference point for scope, not a promise: the last comparable native vendoring effort
  ([Phase 37](DONE/PHASE-37-cross-platform-native-availability.md)) ran 2026-06-07 to
  2026-06-11.
- **A3 — The compile route. DONE, 2026-09-11.** `ShadowDusk.Slang.SlangCompiler` (process-based,
  `Result<CompiledShader, ShaderError[]>`, verbatim slangc diagnostics on failure — same
  shape/spirit as `DxcShaderCompiler`, though it deliberately does NOT implement
  `IShaderCompiler`: that interface's own doc comment specifies HLSL `.fx` source, and this
  class's input is Slang). Entry discovery reuses `SlangEntryScanner` unchanged (new
  `InternalsVisibleTo` from `ShadowDusk.Compiler` to `ShadowDusk.Slang`); the `.fx` wrapper
  (`#if SM4` header + synthesized `technique`/`pass`) is new code in
  `ShadowDusk.Slang.SlangCompiler.AssembleFx` that mirrors `SlangFrontend.ConvertToFx`'s shape
  (can't call into it directly — its SD0600 rejection/attribute-stripping steps operate on raw
  Slang text and don't apply to slangc's HLSL output). `SlangFrontend.cs` and every other
  existing package/output byte are untouched.

  **Invocation shape, empirically settled:** ONE slangc process **per entry point**, not one
  multi-`-entry` invocation. Every ordering of `-entry X -stage S -o out.hlsl -entry Y -stage
  T -o out2.hlsl` (interleaved, `-target` repeated per group, `-o` omitted, extension-inferred
  target) failed identically with `error[E00070]: the output path '...' is not associated with
  any entry point` against v2026.14.1 — a real limitation/bug in that build, not a syntax
  mistake (confirmed: the SAME multi-`-entry` invocation with **no `-o` at all** DOES work and
  prints both entries' HLSL to stdout concatenated, which is what discovered the fix). Since
  `-o` is unusable for the multi-output case anyway, the invocation always omits it and reads
  a single result from stdout — which also means one-invocation-per-entry costs nothing extra
  (no second process just to work around `-o`) and gives per-stage error isolation for free.
  Source is piped over **stdin** (`-lang slang ... -- -`) rather than a temp file, avoiding
  temp-file cleanup/path-traversal surface entirely; diagnostics use the caller's logical
  source name unconditionally (`SlangDiagnosticReformatter`), since slangc's own echoed name
  (a real path or `<stdin>`) is never what the caller gave it and there is exactly one input
  per compile. **Confirmed empirically** (not assumed) that mangling is deterministic per
  source identifier, not per process: two separate invocations of the same file's VS and PS
  entries reproduce byte-identical declarations for their shared types, which is exactly what
  makes the merge step below valid.

  **The merge problem this surfaces, unanticipated in the original A3 scope:** slangc emits a
  FULLY SELF-CONTAINED translation unit per entry point (every used type/cbuffer redeclared in
  full), so a VS+PS pair's two outputs can't just be concatenated — HLSL rejects a struct
  redeclared twice even with identical text. `SlangHlslMerger` splits each unit on its `#line`
  directives (one per top-level declaration/function) and dedups by body text (ignoring the
  `#line` line itself), keeping first-seen order — which preserves dependency order for free
  since each entry's own block sequence is already correctly ordered internally. Covers all 3
  VS+PS shaders in the shipped corpus (`WaveVertex`, `ScrollUv`, `Desaturate`); a pixel-only
  shader (the common SpriteBatch shape) skips the merge entirely.

  **Two pipeline-compatibility fixes made along the way** (both narrow, additive, and outside
  A4's mangling scope — no name touched):
  - slangc's HLSL emission opens with `#ifdef SLANG_HLSL_ENABLE_NVAPI / #include
    "nvHLSLExtns.h" / #endif`. `ShadowDusk.Core.Preprocessor.Preprocessor` does a **textual**
    `#include` scan ahead of DXC (matching mgfxc's own flatten-before-compile shape) that does
    NOT track `#ifdef`/`#endif` nesting, so it always tried to resolve the include and failed
    every corpus shader with `SD0001`. Fixed by stripping this specific guarded-include shape
    from slangc's output before assembly (`SlangCompiler.StripUnresolvableConditionalIncludes`)
    — new code, the shared `Preprocessor` is untouched.
  - Added `-no-hlsl-pack-constant-buffer-elements` to every slangc invocation. Without it,
    slangc wraps every cbuffer's members in a generated `SLANG_ParameterGroup_*` struct and
    gives the cbuffer a single member of that struct type — legal HLSL, DXC compiles it fine,
    but `MonoGameGlslRewriter`'s OpenGL uniform-block lowering only models flat
    float/vec2/vec3/vec4/mat4 members directly inside a cbuffer, so it failed **every** corpus
    shader with a cbuffer on OpenGL specifically (`SD0210`), DirectX_11 unaffected either way.
    The flag makes slangc emit flat members directly; it does NOT touch the still-unfixed `_N`
    suffix (A4's job) — only the cbuffer's shape changed, not its member names.

  **Corpus pass rate** (17-shader shipped `.slang` corpus + the 4 Phase 65 Gum/generics-probe
  shaders, 21 files total, via the real pipeline — `SlangCompilerTests.Corpus_*`):
  **DirectX_11: 21/21 (100%). OpenGL: 18/21 (86%).** The 3 OpenGL failures are ALL and ONLY the
  shaders with a `float4x4` cbuffer member (`Desaturate`, `ScrollUv`, `WaveVertex`) — traced
  past the `SLANG_ParameterGroup_*` wrapping above to a SEPARATE, narrower residue: SPIRV-Cross
  reflects a `layout(row_major)` qualifier on the mat4 member (slangc's own
  `#pragma pack_matrix(column_major)` combined with how DXC/SPIRV-Cross round-trip it), and
  `MonoGameGlslRewriter.UniformMember`'s regex (`src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs`
  around line 193) does not accept a `layout(...)`-prefixed member **by explicit design** (its
  own comment lists "layout-qualified members" in the "FAILS LOUDLY" bucket). This is real,
  loud, structural failure (`SD0210`) — never silent corruption, which is A3's actual bar — and
  was deliberately NOT "fixed" here: whether accepting that qualifier is safe requires reasoning
  about the row/column-major transpose logic `BuildUploadedMat4` already carries scar tissue
  for (issue #70's "exploded cube"), which is real, correctness-sensitive design work, not a
  quick unblock, and belongs to A4/A6 below. `GenericsProbe.slang` (real generics + `interface`,
  the construct the subset frontend cannot compile at all) compiles on both targets — the
  actual point of this whole effort, proven.

  **Writable-cache-directory fix (A2's open finding, closed here):** confirmed empirically
  (copy `slangc.exe`+`slang-compiler.dll` to an isolated directory, run from a SEPARATE writable
  cwd) that the `slang-glsl-module.bin` cache write always targets the exe's OWN directory,
  never the process cwd — so no working-directory choice can dodge it, and `slangc -h`'s full
  text has no cache-path flag or env var (checked every category: General/Target/Downstream/
  Debugging/Experimental/Deprecated). Chosen fix: `SlangNativeCache.EnsureWritableToolDirectory`
  probes the packaged directory for write access first (zero extra I/O on a repo/dev build or a
  self-contained/RID-specific publish, where it's normally already writable) and only copies
  the two native files into a per-user `%LOCALAPPDATA%\ShadowDusk\Slang\<byte-length-keyed>\`
  cache directory (falling back to `Path.GetTempPath()`) when the probe fails — the scenario a
  framework-dependent build resolving straight out of NuGet's global-packages cache (marked
  read-only by convention) hits. Copy-to-temp-then-`File.Move` keeps a concurrent first-use race
  safe (every racing writer produces identical bytes; the rename is atomic same-volume).

  **Tests:** `tests/ShadowDusk.Slang.Tests` (new, added to `ShadowDusk.slnx`), 68 tests × 2 TFMs
  (net8.0/net10.0), all green: pure tests for `SlangHlslMerger`/`SlangDiagnosticReformatter`
  (fixed literal HLSL/diagnostic text, no process) and `SlangNativeCache` (a `Func<string,bool>`
  test seam replaces the real writability probe — Windows' directory `ReadOnly` attribute does
  NOT actually block file creation inside it, verified empirically, so there is no cheap
  portable way to fake an unwritable temp directory from a test); `[Trait("Category",
  "Integration")]` tests that spawn the real restored `tools/slang/win-x64/slangc.exe` for the
  full corpus sweep, the VS+PS merge (observed via an injectable stub `IShaderCompiler` so the
  assembled `.fx` text is assertable without paying for two native pipeline compiles), the
  generics probe, verbatim-diagnostic surfacing on a deliberately broken source, an MGFX-magic-
  header rung-2 structural check, determinism (same source → same bytes), and `-D` user-define
  forwarding to slangc's own preprocessor. Full solution `dotnet test` (not filtered) green
  before push — the new `SD0620`–`SD0623` codes required an `docs/error-codes.md` registration
  to satisfy `DiagnosticCodeRegistryTests`.

  **Left for A4/A6, precisely:** (1) the `_N` mangling suffix itself — `float BlurAmount`
  reaches the compiled effect's reflected parameter table as `BlurAmount_0` today, in EVERY
  passing corpus shader; slangc's experimental `-no-mangle` flag ("do as little mangling of
  names as possible") is untested and worth trying first before building a source-mapping shim.
  (2) The `layout(row_major)` OpenGL gap above (3 shaders) — needs the row/column-major
  transpose reasoning, not just a regex widen. (3) A6's full non-Gum-shaped corpus sweep this
  stage did not attempt (only the already-Gum-shaped/generics-probe set).
- **A4 — Mangling and the OpenGL row_major gap. DONE, 2026-09-11.** Both of A3's two open
  problems closed; corpus at a clean baseline, **21/21 on both DirectX_11 and OpenGL**.

  **Problem 1 (mangling) — fixed by adding slangc's experimental `-no-mangle` flag, not a
  demangling shim.** Measured directly (not assumed) against the full 21-shader corpus on
  both targets, with and without the flag: `-no-mangle` leaves every top-level declaration
  that feeds a consumer's reflected parameter table — cbuffer names, cbuffer members,
  `Texture2D`/`SamplerState` declarations — at the author's exact original spelling
  (`float TintAmount` stays `TintAmount`, never `TintAmount_0`), with zero collisions
  anywhere in the corpus, including `GenericsProbe.slang`'s real generic-over-`interface`
  function (a single instantiation, so no ambiguity to disambiguate). Local variables and
  struct field names (`VSOutput`/`PsInput` members, loop-body temporaries) still carry
  slangc's `_N` suffix, but those are never part of an `Effect`'s reflected parameter
  table, so they don't matter for the surface this flag exists to fix. Every corpus shader
  that compiled before still compiles with the flag added — no new failures on either
  target. Per the stage's own instruction, the simpler fix was taken: `-no-mangle` is now
  passed on every slangc invocation (`SlangCompiler.RunSlangc`); no separate
  demangling/renaming shim was built. Proven by
  `SlangCompilerTests.ParameterNames_RoundTripUnmangled_ThroughTheCompiledEffectsParameterTable`
  (DirectX_11 + OpenGL): compiles `GumTint.slang` through the real pipeline, parses the
  resulting `.mgfx` bytes with the same `MgfxBlobReader` the Integration test suite uses,
  and asserts `TintColor`/`TintAmount` land in `ParameterNames` verbatim with no
  `_[0-9]+$`-suffixed name anywhere in the table.

  **Problem 2 (OpenGL `layout(row_major)` gap) — root-caused to slangc's own HLSL emission
  silently overriding ShadowDusk's existing, already-correct OpenGL matrix-packing
  convention; fixed by stripping slangc's redundant pragma, NOT by touching
  `MonoGameGlslRewriter`.** Checked the ordinary (non-Slang) HLSL→GL route FIRST, per the
  stage's instruction: `DxcFlagBuilder` already carries a settled, load-bearing convention
  for exactly this — `-Zpr` (row-major HLSL packing) is added to every DXC invocation for
  OpenGL specifically (never for Vulkan/DirectX12, and DirectX_11 doesn't go through DXC at
  all — it compiles via `d3dcompiler_47` with `ShaderFlags.PackMatrixColumnMajor`). That
  flag exists so DXC's SPIR-V decorates a cbuffer matrix `ColMajor` (SPIR-V's inverted term
  for HLSL row-major — see that file's own comment), which matches GLSL's own
  column-major default, so SPIRV-Cross never needs to emit an explicit layout qualifier at
  all — this is *why* every ordinary `.fx` shader with a `float4x4` cbuffer member already
  works on OpenGL with no special-casing anywhere in `MonoGameGlslRewriter`.
  Slang's route hit the qualifier because slangc's `-target hlsl` emission opens
  EVERY translation unit with an unconditional `#pragma pack_matrix(column_major)`
  (confirmed present in every corpus shader's output, not just the ones with a `float4x4`
  member). An HLSL `#pragma` always overrides a compiler command-line flag, so this
  silently defeated `-Zpr` specifically on the Slang route: DXC decorated the SPIR-V the
  OPPOSITE way every other OpenGL shader gets (`RowMajor` instead of `ColMajor`), which no
  longer matches GLSL's column-major default, so SPIRV-Cross emitted the explicit
  `layout(row_major)` qualifier that `MonoGameGlslRewriter.UniformMember`'s regex rejects
  by design (Phase 66 A3's finding). Fix: `SlangCompiler.StripMatrixPackingPragma` strips
  the `#pragma pack_matrix(column_major)` line from slangc's merged HLSL before assembly —
  the same "strip slangc's own inapplicable boilerplate" pattern A3 already used for the
  NVAPI include guard, not a new shape. Measured to be a no-op for every other target: DXC's
  own default (no `-Zpr`) is column-major on Vulkan/DirectX12, and `d3dcompiler_47`'s
  explicit `ShaderFlags.PackMatrixColumnMajor` already matches HLSL's column-major default
  for DirectX_11 with or without the redundant pragma. **`MonoGameGlslRewriter.cs` was NOT
  touched** — the fix restores OpenGL to the exact convention every hand-written `.fx`
  shader already gets, rather than teaching the shared rewriter a parallel layout-qualifier
  special case for Slang's own output shape, per the stage's instruction to match the
  existing route rather than loosen the rejecting regex. Proven by
  `Float4x4CbufferMember_CompilesOnOpenGL_WithMatrixPackingPragmaStripped` (asserts the HLSL
  handed to the downstream compiler no longer contains `pack_matrix`) and
  `Float4x4CbufferMember_CompilesOnOpenGL_ThroughTheRealPipeline` (end-to-end compile of
  `WaveVertex.slang`, the VS+PS shape with the `float4x4` cbuffer member, through the real
  pipeline on OpenGL) plus the corpus sweep itself, which no longer carves out
  `Desaturate`/`ScrollUv`/`WaveVertex` as known OpenGL failures.

  **Tests:** `tests/ShadowDusk.Slang.Tests/SlangCompilerTests.cs` — the corpus sweep's
  `KnownOpenGlRowMajorFailures` carve-out removed (all 21 shaders now assert success on
  both targets), plus the two new tests named above and a `[Theory]`-parameterized
  mangling round-trip test across both targets. The MGFX-parsing test source-links
  `ShadowDusk.Integration.Tests/MgfxBlobReader.cs` into `ShadowDusk.Slang.Tests` (the same
  `<Compile Include=... Link=...>` pattern that project already uses for
  `MgcbErrorFormatter.cs`/`Fx2BinaryValidator.cs`) rather than a second hand-rolled MGFX
  parser. Full solution `dotnet test` (unfiltered) green on both net8.0/net10.0 before
  push.
- **A5 — Broaden the acceptance boundary.** Replace `SD0600`'s "reject Slang-only constructs by
  name" with Phase 61 §6 A6's three-band rule (table below), now that real slangc backs it:

  | Band | Behaviour |
  |---|---|
  | Slang `slangc` itself refuses (syntax error) | slangc's diagnostic, verbatim — file, line, column, text, never reformatted |
  | Slang that compiles but has nowhere to land in an `Effect` (compute/mesh entry points; SM6-only constructs on GL/FNA — OQ2) | a registered ShadowDusk diagnostic naming the construct **and the target**, never a generic parse error |
  | Everything else | compiles, no curated allow-list |

  Note en route: Phase 65 §5 found the shipped `SD0600` scan itself has a gap (bare
  `interface`/generics fall through to DXC's raw syntax error instead of a clean rejection) —
  moot once this band replaces `SD0600` for the real-slangc route, but worth fixing in the
  **subset** frontend too while this code is fresh (small, separately reviewable fix).
- **A6 — The real A5 residue sweep.** Phase 65's 33-shader corpus was a solid start, not the full
  sweep Phase 61 envisioned. Run the **whole** `tests/fixtures/shaders/` corpus (not just
  Gum-shaped shaders) through real slangc `-target hlsl` at every ShadowDusk target (OpenGL,
  DirectX11/12, Vulkan, FNA), record what changes shape or fails per target — this is what makes
  A5's diagnostic table (above) complete rather than a guess.
- **A7 — Validation.** A `SlangCorpus`-style gate through the **real-slangc route** specifically
  (distinct from the existing subset-route gate, which stays as-is and keeps validating the
  subset frontend): compile sweep across all targets, pixel-equivalence against slangc's own
  HLSL emission fed through the same DXC + SPIRV-Cross (the same evidence model Phase 61 §5.2
  already established for the subset route). Default-ON in `run-windows-render-gates.ps1`.
- **A8 — Docs and support-surface updates**, in the same PR per `CLAUDE.md`'s standing rule (new
  delivery shape, new package): README's supported-inputs section and package table,
  `docs/validation-matrix.md` (a distinct-evidence row, never a §1 cell, same as the subset
  frontend), `docs/pipeline-overview.puml` + regenerated SVG, `docfx/` guides mentioning Slang,
  `project_facts.md` (native/pins list gains a `ShadowDusk.Slang` row), `CHANGELOG.md`.

**Folded in alongside, cheaply, per the owner's "(a)+(b) too, not instead" framing (§1):**

- **(a) Messaging fix.** Correct the public "we support Slang" framing (Discord, README) to state
  the two-tier reality once this ships: the free-standing `ShadowDusk.Compiler` subset (HLSL-
  compatible Slang, no extra package) and the full `ShadowDusk.Slang` package (real slangc,
  everything). Do this as soon as the two-tier shape is settled (after A3), not gated on A8.
- **(b) `slang2fx` author-time CLI.** Still worth shipping independently — it satisfies Gum's
  narrow fixed-template case today, cheaply, without waiting on A1–A7. Can ship before or in
  parallel with the full package; not blocking and not blocked by it.

---

## 4. Non-goals

- Slang as a substitute compiler for DXC anywhere in the pipeline (Phase 61 §2.1, closed on
  measured evidence — unchanged by this phase).
- Emitting Slang (Phase 61 §4/§7, closed by owner direction 2026-08-13 — unchanged).
- Slang's compute/mesh/raytracing surface (Phase 58: stock MonoGame/KNI hold only VS and PS).
  Rejected loudly per A5's band table, never silently dropped.
- Claiming any `.slang` route is `mgfxc`-equivalent.
- Removing or changing the shipped subset frontend (`ShadowDusk.Compiler`'s `.slang` support) —
  it stays, for consumers who don't want the extra package weight.

## 5. Open questions

- **Linux/macOS RID parity** for A1's finding — both Phase 65's original measurement and this
  probe only checked the cached windows-x64 oracle; unverified whether the Linux/macOS releases
  also load and run `-target hlsl` cleanly with their platform's `slang-llvm.*` removed.
- **Whether `-no-mangle` stays collision-free outside the 21-shader corpus** — A4 measured zero
  name collisions on every shader tried (including a real generic-over-`interface` function),
  but only ever one instantiation of any given generic; a source that instantiates the same
  generic twice with different type arguments (two call sites needing genuinely different
  compiled bodies under the same surface name) is untested — A6's broader sweep is what would
  surface it, and the fallback (a demangling shim mapping slangc's mangled names back
  deterministically) documented in A3/A4's original scoping remains available if it does.
