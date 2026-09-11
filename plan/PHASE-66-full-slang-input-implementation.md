# Phase 66 — Full, slangc-backed Slang input (general product capability)

**Track:** Additive package / reach. Additive only — `ShadowDusk.Compiler`'s existing `.slang`
subset frontend (`SlangFrontend.cs`) and every existing output byte are untouched, with ONE
narrow, deliberate exception: A5 also closed a pre-existing `SD0600` scan gap in that same file
(Phase 65 §5's finding — see A5 below). That is a strict improvement to an already-shipped
diagnostic (a construct that used to fall through to a raw DXC error now gets a clean, named
rejection), not new frontend behavior, and does not change any `.fx` output byte the subset
frontend already produces for input it accepts. Everything else about this phase is a new,
separate, opt-in package.

**Status:** 🟡 In progress. Scoped 2026-09-11; A1-A5 done same day (native vendoring +
`ShadowDusk.Slang.SlangCompiler`, the real compile route, the mangling fix and the OpenGL
row_major gap fix — corpus now 21/21 on both DirectX_11 and OpenGL; A5 replaced the placeholder
acceptance with the real three-band rule and closed the `SD0600` `interface`/generics gap).
A6 done same day (the broad residue sweep found and fixed a real platform-macro-forwarding
gap — see below). A7-A8 open.

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
- **A5 — Broaden the acceptance boundary. DONE, 2026-09-11.** `ShadowDusk.Slang.SlangCompiler`
  now implements Phase 61 §6 A6's three-band rule directly (no placeholder left):

  | Band | Behaviour | Codes |
  |---|---|---|
  | Slang `slangc` itself refuses (syntax error) | slangc's diagnostic, verbatim — file, line, column, text, never reformatted | `SD0622` (unparsed fallback), the slangc-native code otherwise |
  | Slang that compiles but has nowhere to land in an `Effect` | a registered ShadowDusk diagnostic naming the construct **and the target** | `SD0602` (non-VS/PS entry stage), `SD0624` (SM6-only Wave/Quad intrinsic on a target capped below SM6) |
  | Everything else | compiles, no curated allow-list | — |

  **Band 1 — verified already correct, not rebuilt.** `SlangDiagnosticReformatter.SelectPrimary`
  (added in A3) already does exactly this: splits slangc's `error[E####]:`/`warning[W####]:`
  blocks, keeps each block's text verbatim as `ShaderError.Message`, and falls back to `SD0622`
  with the complete raw stderr when slangc's failure mode doesn't match that shape at all (a
  native crash, an ICE). No change needed.

  **Band 2 part A — compute/mesh entry points. Verified already correct, not rebuilt.**
  `SlangEntryScanner.Scan` (used unchanged by `SlangCompiler.Compile`, before slangc is ever
  invoked) already rejects any `[shader("...")]` stage other than `vertex`/`fragment`/`pixel`
  with `SD0602`, naming the entry point and the stage — this is the same Phase 58/`SD0602`
  policy `SlangFrontend`'s subset route already uses, reused as-is. Extended
  `ComputeEntryPoint_RejectedLoudly_BeforeInvokingSlangc` (now
  `..._OnEveryTarget`, `SlangCompilerTests.cs`) from one OpenGL-only `[Fact]` into a `[Theory]`
  over all five targets with a REAL compute shader (`[numthreads(64,1,1)]`,
  `RWStructuredBuffer<float>`, `SV_DispatchThreadID` — not just a bare attribute stub), proving
  the rejection is entry-stage policy decided before any per-target step runs, plus a new test
  proving it never reaches (and would-be-fail-the-test-if-it-did) the downstream compiler.

  **Band 2 part B — SM6-only constructs on GL/FNA (OQ2). New: `SD0624`,
  `ShadowDusk.Slang.SlangSm6ConstructGuard`, a static source-text scan (chosen over
  catch-and-wrap).** Measured directly before choosing an approach (Phase 66 A5, real pipeline,
  not Phase 65's simplified DXC-only probe):

  A Wave/Quad intrinsic call (`WaveActiveSum(x)`) is syntactically an ordinary function call —
  slangc's `-target hlsl` emission preserves the identifier verbatim on every target (no
  target-specific spelling difference), so there is no way to detect it from the emitted HLSL's
  *shape* either; the only difference shows up downstream, and the downstream diagnostics
  disagree with each other and are phrased around each backend's own internals, not the Slang
  author's target:
  - **OpenGL** (DXC at the fixed `vs_5_0`/`ps_5_0` profile): DXC accepts the call (its grammar is
    always SM6) and only refuses at SPIR-V generation — `error: Vulkan 1.1 is required for Wave
    Operation but not permitted to use`. Confusing for an OpenGL target: ShadowDusk's use of
    Vulkan/SPIR-V as an OpenGL intermediate is an implementation detail no Slang author should
    need to know.
  - **DirectX (DX11, the default `DxbcBackend.Vkd3d`) and FNA** (both go through vkd3d-shader's
    SM≤3/5.1 HLSL frontend): `E5005: Function "WaveActiveSum" is not defined` — reads like a
    typo, not "this needs Shader Model 6."
  - **DirectX12** (raw SM6 DXIL, no capability gate): compiles successfully — the one target that
    reaches a real Wave-intrinsic Slang shader through ShadowDusk's pipeline today.
  - **Vulkan** (DXC at `vs_6_0`/`ps_6_0`, architecturally CAN hold SM6 HLSL): **also currently
    fails**, with the same "Vulkan 1.1 is required" message as OpenGL — `DxcFlagBuilder` never
    passes `-fspv-target-env=vulkan1.1` for ANY target, Slang-sourced or not. This is a
    **pre-existing, separate flag gap in the shared HLSL/DXC pipeline** every `.fx` author using
    wave intrinsics on Vulkan would hit — found here, but deliberately **not fixed**: fixing it
    would change what a hand-written Vulkan `.fx` gets too, well outside this Slang
    acceptance-boundary stage's scope. Left as an open finding (see §5 below) rather than folded
    in as an in-scope bug fix, since it is not a Slang-specific "nowhere to land" case.

  Given no clean signal survives to the downstream error text (three different, technically
  correct but differently-shaped failures for the identical construct, one target that succeeds,
  and one that fails for an unrelated reason), a **static source scan** was chosen over
  catching-and-wrapping the downstream failure: `SlangSm6ConstructGuard.FindConstruct` matches
  the closed, documented HLSL SM6 "Wave Intrinsics" vocabulary (plus the SM6 Quad intrinsics,
  same capability class) as whole identifiers against the raw Slang source — the same
  "closed language keyword set" shape `SlangFrontend`'s own `SD0600` scan already uses for
  `import`/`module`/`extension`/`associatedtype`/`__generic`. `SlangCompiler.Compile` runs it
  BEFORE spawning slangc at all, gated on
  `SlangSm6ConstructGuard.IsArchitecturallyBelowSm6(options.Target)` — true only for OpenGL,
  DirectX, and Fna (the three targets that can never represent SM6 HLSL by format/profile,
  regardless of any future flag fix; Vulkan and DirectX12 are excluded from the gate, so a
  Wave-intrinsic Slang shader targeting either surfaces whatever the downstream compiler reports,
  unmodified — today that is a real compile, on DirectX12, or the pre-existing Vulkan flag gap
  above, on Vulkan, never a silently mislabeled "SM6 unreachable"). This gives ONE consistent,
  correctly-targeted diagnostic (`SD0624`, naming the intrinsic, the reason, and the target) for
  the case that CAN be classified with certainty, is cheaper (no process spawn for an entry point
  that will be rejected regardless), and never depends on how any particular downstream backend
  phrases its own error.

  **Scope, stated plainly:** this covers the one construct class Phase 65 §1's OQ2 caveat named
  and this stage then measured concretely (wave/quad subgroup intrinsics). Raytracing and
  mesh/amplification SM6 entry-point shapes never reach this guard — `SD0602` already rejects any
  non-vertex/fragment stage before compilation starts. Rarer standalone SM6-only resource forms
  callable from an ordinary vertex/pixel body (a templated `ResourceDescriptorHeap` index, a
  64-bit interlocked op, …) are **not enumerated**: no construct in either the shipped
  17-shader corpus or Phase 65's broadened 33-shader sweep exercised one, so building a list for
  them now would be guessing at their exact diagnostic shape rather than measuring it — they fall
  through to whatever the downstream compiler reports, unmodified (band 3: a real compile
  failure, never silently dropped, just not specially re-labeled). A6's broader sweep is where
  evidence for extending this list, if any surfaces, would come from.

  **Tests:** `SlangSm6ConstructGuardTests.cs` (pure — the regex/line-detection logic and the
  per-target gate, no process) plus three new `SlangCompilerTests.cs` integration tests: SD0624
  on all three capped targets (message contains the intrinsic name and the target name), a proof
  the guard runs BEFORE slangc is spawned (an injected stub downstream compiler that would fail
  the test if `SlangCompiler` got as far as assembling/handing off an `.fx` body), and a
  DirectX12 success case (the one target that reaches a real Wave-intrinsic shader today).

  **The separate `SD0600` fix, folded in while this code was fresh (Phase 65 §5's finding):** the
  shipped subset frontend's `SD0600` scan (`src/ShadowDusk.Compiler/Slang/SlangFrontend.cs`) did
  not pattern-match bare `interface` or generic-type-parameter constraint syntax (only
  `import`/`module`/`extension`/`associatedtype`/`__generic`), so `GenericsProbe.slang`'s exact
  shape (an `interface IBlendMode` plus a generic free function `applyBlend<T : IBlendMode>(...)`)
  fell all the way through this courtesy scan to DXC, which rejected it with its own confusing
  native diagnostic (`X0000: expected ';' after __interface`) instead of a clean, named `SD0600`.
  Fixed by adding two patterns to `SlangOnlyConstructs`: a line-anchored `interface` keyword
  (matching the same shape `module`/`extension` already use — NOTE: stock HLSL also has a rare
  `interface` keyword of its own for dynamic shader linkage, which this scan cannot distinguish
  from Slang's usage by syntax alone and so also rejects; the same trade this whole courtesy scan
  already makes for every other entry, and nothing in the corpus or test suite uses HLSL's own
  dynamic-linkage interfaces), and a colon-constrained generic angle-bracket shape
  (`\w+<\w+ : \w+>\(` — deliberately requires the `:` constraint so it does NOT false-positive on
  ordinary HLSL templated resource types like `StructuredBuffer<float4>`/`Texture2D<float4>`,
  which never put a colon inside the angle brackets and remain perfectly legal subset-frontend
  input). Confirmed no currently-passing test asserted the old fallthrough as correct (grepped
  the whole test tree and every `.slang` fixture for `interface`: zero hits before this change).
  New tests: two `[InlineData]` rows on the existing `SlangOnlyConstructs_AreRejectedByName_WithSD0600`
  theory (`SlangFrontendPureTests.cs`), plus a dedicated
  `RealInterfacePlusGenericFile_RejectedWithSD0600_NamingInterface_NotARawDxcError` reproducing
  `GenericsProbe.slang`'s exact shape end to end through the subset frontend and asserting the
  message never contains `X0000`/`__interface` (the old raw-DXC symptom).

  Full solution `dotnet test` (unfiltered, both net8.0/net10.0) green before push.
- **A6 — The real A5 residue sweep. DONE, 2026-09-11.** Found and fixed a real, general
  bug (platform macros never reached slangc); everything else the sweep surfaced was
  either the already-known "legacy DX9 sampler syntax is a slangc syntax error" boundary
  (Band 1, expected) or an already-registered ShadowDusk diagnostic correctly firing
  through the new route too (Band 2/3, no new gap).

  **Sampling method.** `tests/fixtures/shaders/` is 153 `.fx` files; slangc rejects FX9
  `technique`/`pass`/`sampler_state` syntax outright (Phase 61, already known), so a
  file-for-file conversion would mostly re-measure that one fact 153 times. Instead: 15
  fixtures were hand-picked for **construct diversity**, favoring real vendored
  third-party shaders (`third-party/Nez`, `third-party/MonoGame`) and diagnostic-focused
  `examples/`/root fixtures over more Gum-shaped procedural shaders (A3-A5 already covered
  that shape via the Phase 65 probes). Each fixture's entry-point body was carried over
  with the `[shader("vertex")]`/`[shader("fragment")]` convention added, changing as
  little else as possible; every sample's own header comment names its source fixture and
  the construct it was picked for. Coverage aimed for: legacy DX9 `sampler`/`tex2D()` and
  `sampler_state{...}` declaration shapes (the dominant style across the non-Gum corpus,
  never exercised by the shipped 21-shader real-slangc corpus, which only ever used modern
  `Texture2D`/`SamplerState.Sample()`) in three sub-shapes (bare `sampler`, an
  address-mode `sampler_state` block, a texture-only `sampler_state` block); loops
  (literal-bounded, array-uniform-indexed); branches (simple, nested, ternary-chained);
  the `VPOS` semantic; helper-function calls; `Texture2DArray`/`TextureCube`/`Texture3D`
  sampling; `SampleGrad`; `ddx()` inside a loop with a conditional break; multiple render
  targets (a struct-typed PS return with `SV_Target0`/`SV_Target1`) plus `clip()`;
  literal-indexed array uniforms; and a VS taking a second, semantic-tagged parameter
  (`float4x4 : BLENDWEIGHT`) with an `#if VULKAN` branch. Samples live in
  `plan/PHASE-66-appendix/a6-residue-sweep/shaders/` (`README.md` there has the exact
  provenance/repro). Each of the 15 was compiled through the real `SlangCompiler.CompileAsync`
  route for all 5 targets (`OpenGL`/`DirectX`/`DirectX12`/`Vulkan`/`Fna`) — 75 compiles —
  via a temporary xunit exploration harness (not committed; its raw output is reproduced
  in the table below). This is a judgment sample, not exhaustive: it does not touch the
  MonoGame `BEGIN_CONSTANTS`/`DECLARE_TEXTURE`/`TECHNIQUE` macro-header shape
  (`DualTextureEffect.fx`, `SkinnedEffect.fx`, …, which route through `.fxh` includes this
  sweep did not resolve by hand) or the 1000+-line `apos-shapes-sm6.fx` SM6 corpus shader
  (excluded for size; its SM6-specific declaration shape is already covered by the shipped
  corpus's `WaveVertex.slang`/`GenericsProbe.slang` and A5's `SD0624` work).

  **Pass/fail table** (✓ = compiled through the real pipeline; a failure code is shown
  otherwise; targets are OpenGL / DirectX / DirectX12 / Vulkan / Fna):

  | Sample | GL | DX11 | DX12 | Vulkan | FNA |
  |---|---|---|---|---|---|
  | `GaussianBlurLoop.slang` (bare `sampler`) | E30015 | E30015 | E30015 | E30015 | E30015 |
  | `TwistBranch.slang` (bare `sampler`) | E30015 | E30015 | E30015 | E30015 | E30015 |
  | `PixelGlitchHelper.slang` (bare `sampler`) | E30015 | E30015 | E30015 | E30015 | E30015 |
  | `CrosshatchVposModulo.slang` (bare `sampler`) | E30015 | E30015 | E30015 | E30015 | E30015 |
  | `HeatDistortionDualSampler.slang` (`sampler_state` w/ address modes) | E20001 | E20001 | E20001 | E20001 | E20001 |
  | `BloomCombineHelperLerp.slang` (`sampler_state`, texture-only) | E20001 | E20001 | E20001 | E20001 | E20001 |
  | `ClipMaskCompare.slang` (`Texture2D` + legacy `sampler_state`) | E20001 | E20001 | E20001 | E20001 | E20001 |
  | `DeferredSpriteMrt.slang` (MRT + `sampler_state`) | E20001 | E20001 | E20001 | E20001 | E20001 |
  | `ArrayUniformTernary.slang` (`sampler_state`) | E20001 | E20001 | E20001 | E20001 | E20001 |
  | `DdxInDivergentLoop.slang` (`sampler_state`) | E20001 | E20001 | E20001 | E20001 | E20001 |
  | `CubeSamplerGeneric.slang` | ✓ | ✓ | ✓ | ✓ | ✓ |
  | `VolumeSamplerGeneric.slang` | ✓ | ✓ | ✓ | ✓ | ✓ |
  | `SampleGradExplicit.slang` | ✓ | ✓ | ✓ | ✓ | ✓ |
  | `TextureArraySampleVsPs.slang` | SD0210+SD0403 | ✓ | ✓ | ✓ | FX0013 |
  | `InstancingMatrixSemantic.slang` | SD0210 | ✓ | ✓ | ✓ | ✓ |

  **Classification of every failure:**

  - **`E30015`/`E20001` (10/15 samples, EVERY legacy-sampler sample, all 5 targets) —
    Band 1, slangc's own syntax error, expected and fine.** `E30015: undefined identifier
    'sampler'` for a bare `sampler s0;` declaration, and `E20001: unexpected token '{'` for
    any `sampler_state { ... }` initializer block (address-mode or texture-only) —
    slangc's HLSL-superset grammar has no support for DX9-era `sampler`/`sampler_state`
    at all, regardless of shape. This was never measured before A6 (the shipped 21-shader
    corpus and the Phase 65 probes only ever used modern `Texture2D`/`SamplerState`), and
    it is a real, concrete finding worth recording even though it needs no fix: **every**
    real-world shader in this sweep that used the legacy declaration style hit it, and
    that style is the dominant one across the non-Gum, non-Slang parts of
    `tests/fixtures/shaders/` (third-party Nez/vendored effects especially). A Slang
    author porting an existing legacy-style `.fx` shader must rewrite its texture/sampler
    declarations to `Texture2D`/`SamplerState`/`.Sample()` first; `SD0602`/`SD0622`/the
    existing Band-1 verbatim-diagnostic path already surfaces slangc's own message
    correctly (confirmed here, not just assumed) — no new diagnostic code needed.
  - **`SD0210` (GL, `TextureArraySampleVsPs.slang`: `sampler2DArray` unmodelled) and
    `SD0403` (GL, same file: unsigned-int/shift/bitwise ops in the `SV_VertexID`-driven
    triangle trick) — Band 2/3, already-known ShadowDusk GL limitations, correctly
    surfaced through the new route.** Both diagnostics are pre-existing and already
    covered by `MonoGameGlslRewriterTests`/`HidefGeneralityFixtureTests`'s own comments
    ("sampler2DArray... DO still fail loudly"); nothing new here beyond confirming the
    Slang route reaches the same rewriter code path and gets the same loud rejection a
    hand-written `.fx` with the identical construct would.
  - **`FX0013` (FNA, `TextureArraySampleVsPs.slang`: `Texture2DArray` has no D3D9 SM1-3
    equivalent) — Band 2/3, already-known, already-registered diagnostic**, matching
    `tests/fixtures/shaders/third-party/MonoGame/NOTICE.md`'s own row for the same
    construct on the ordinary `.fx` route (`TextureArrayEffect.fx`: FNA → `FX0013`).
  - **`SD0210` (GL, `InstancingMatrixSemantic.slang`: "stage interface identifier
    'in_var_BLENDWEIGHT' survived the I/O rewrite") — Band 2/3, already-known, already
    tested.** `MonoGameGlslRewriterTests.VertexStage_UnknownSemantic_ThrowsLoudly` already
    pins this exact rejection (an unmapped vertex semantic fails loudly on GL) for the
    identical `in_var_BLENDWEIGHT0` shape. Not a new gap; the Slang route correctly hits
    the same deliberate "fail loudly on an unmapped semantic" policy.
  - **A genuinely stale, unrelated finding (not fixed, out of this stage's scope):**
    `CubeSamplerGeneric.slang`/`VolumeSamplerGeneric.slang` passed on every target
    including GL, which at first looked like a Slang-route divergence from
    `examples/ExCubeSamplerHidef.fx`/`ExVolumeTextureHidef.fx`'s own header comments
    ("Expect: OpenGL compile FAILS with SD0210"). It isn't one: `HidefGeneralityFixtureTests.cs`
    confirms cube/volume sampling has been genuinely supported on GL since Phase 34 — those
    two fixtures' header comments were simply never updated after Phase 34 shipped and are
    stale documentation, unrelated to Slang. Left as-is; fixing unrelated fixture-comment
    staleness is outside A6's mandate.

  **The bug found and fixed: platform macros never reached slangc.** Discovered via
  `InstancingMatrixSemantic.slang`'s `#if VULKAN` branch (copied verbatim from
  `Instancing.fx`, which the ordinary `.fx` route already compiles correctly on every
  target it's tested on — DX11/DX12/Vulkan/FNA, per
  `third-party/MonoGame/NOTICE.md`). `SlangCompiler.RunSlangc` only ever forwarded
  `CompilerOptions.Defines` (the user's own `-D`s) to slangc's preprocessor pass — never
  `PlatformMacros.For(options.Target, options.Container)`, the `OPENGL`/`SM4`/`VULKAN`/
  `SM6`/`HLSL`/`GLSL`/`MGFX`/`FNA`/`SM3`/`__KNIFX__` set the ordinary `.fx` route already
  defines for DXC (`CompilationPipeline`). **Measured directly, not inferred:** before the
  fix, the HLSL slangc emitted for `PlatformTarget.Vulkan` and `PlatformTarget.DirectX`
  were IDENTICAL on the point that matters — both contained
  `mul(input.Position, transpose(worldTransposed))`, the branch meant only for non-Vulkan
  targets, because `VULKAN` was never defined during slangc's compile. After the fix,
  Vulkan's HLSL contains `mul(input.Position, worldTransposed)` (untransposed, correct)
  while DirectX's still contains the `transpose()` call — verified by inspecting the
  actual captured HLSL text for both targets (not just "both still compile", which stayed
  true throughout and would have hidden the bug forever). This is the exact "fix the
  class, not the repro" shape: any Slang author writing the SAME `#if OPENGL`/`#if VULKAN`/
  `#if SM4`/`#ifdef __KNIFX__` idiom the rest of this project's shaders already use for
  per-target correctness hit the identical silent-wrong-branch failure, independent of
  which specific construct sat inside the branch — fixing the macro-forwarding gap once
  fixes every instance of that condition, not just the `Instancing.fx`-shaped one that
  happened to surface it. No corpus regression: none of the shipped 21-shader corpus or
  the Phase 65 Gum probes reference any of these macro names (confirmed by grep before
  changing anything), so the fix is additive.

  Fixed in `src/ShadowDusk.Slang/SlangCompiler.cs` (`Compile`/`RunSlangc`, plus the class
  doc comment). **Tests:**
  `SlangCompilerTests.PlatformMacros_ForwardedToSlangc_IfOpenglBranchResolvesPerTarget`
  (asserts OpenGL and DirectX resolve a `#if OPENGL` branch to DIFFERENT literals, which
  distinguishes "forwarded correctly" from "silently ignored, both targets happen to still
  compile") and `...PlatformMacros_KnifxContainer_DefinesKniFxMacro` (asserts
  `EffectContainer.Knifx` defines `__KNIFX__`, `Mgfx` does not). Full solution `dotnet test`
  (unfiltered, both net8.0/net10.0) green before push.

  **Left open, precisely, for a future stage (not this one — needs real design work, not a
  quick unblock):** the MonoGame `BEGIN_CONSTANTS`/`DECLARE_TEXTURE`/`TECHNIQUE` macro
  header shape (`Macros.fxh`, used by `DualTextureEffect.fx`/`SkinnedEffect.fx`/most of
  the official MonoGame sample effects) was not exercised at all — converting one by hand
  means resolving several chained `#include`s and macro expansions manually, which this
  stage's per-sample budget did not cover. Read (not compiled) `Macros.fxh` directly:
  ALL THREE of its `DECLARE_TEXTURE` branches (`SM6`/`VULKAN`, `SM4`, and the DX9 `#else`)
  expand to a bare `sampler Name##Sampler : register(...)` or `sampler2D Name : register(...)`
  declaration — the exact shape every `E30015` failure in the table above already hit, in
  every branch, not only the legacy one. So these fixtures would almost certainly fail
  identically at Band 1 — but that is a reading of the macro source, not a measured
  compile through slangc, so it is flagged here rather than asserted as a table row.
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
- **`DxcFlagBuilder` never requests a Vulkan 1.1 SPIR-V target environment, so wave/quad
  intrinsics currently fail on Vulkan too** (found during A5, measured directly: `error: Vulkan
  1.1 is required for Wave Operation but not permitted to use`, identical to the OpenGL failure).
  Architecturally Vulkan CAN hold SM6 HLSL (it already compiles at `vs_6_0`/`ps_6_0`), so this is
  a fixable flag gap, not a format ceiling — but it is a **general HLSL/DXC pipeline gap**, not
  Slang-specific (a hand-written `.fx` calling `WaveActiveSum` on Vulkan hits the identical
  wall), so `SlangSm6ConstructGuard` deliberately does not gate Vulkan and this stage left the
  flag itself unfixed (out of A5's scope: broadening Slang's own acceptance boundary, not the
  shared Vulkan pipeline's capability floor). A future stage adding `-fspv-target-env=vulkan1.1`
  to the Vulkan case in `DxcFlagBuilder.Build` (with the render-gate re-verification that change
  implies) would make DirectX12 no longer the only target able to compile a real wave-intrinsic
  shader through ShadowDusk's pipeline.
