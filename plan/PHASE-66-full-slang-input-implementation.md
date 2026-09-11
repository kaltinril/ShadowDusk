# Phase 66 — Full, slangc-backed Slang input (general product capability)

**Track:** Additive package / reach. Additive only — `ShadowDusk.Compiler`'s existing `.slang`
subset frontend (`SlangFrontend.cs`) and every existing output byte are untouched; this phase adds
a new, separate, opt-in package.

**Status:** 🔵 Open — implementation not started. Scoped 2026-09-11.

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
- **A3 — The compile route.** A `SlangCompiler` wrapper (same shape as `DxcShaderCompiler` —
  process-based, structured `Result<T, ShaderError[]>`, `mgfxc`-style verbatim diagnostics on
  failure) that runs real slangc as `-target hlsl`, feeding the result into the **unchanged**
  DXC pipeline. Entry discovery and technique/pass synthesis reuse `SlangEntryScanner`/
  `SlangFrontend` unchanged (§1 above).
- **A4 — The demangling shim.** Real slangc renames every symbol with an `_N` suffix and wraps
  cbuffers in generated `SLANG_ParameterGroup_*` structs (confirmed still true 2026-09-11 against
  v2026.14.1, Phase 65 §2). Left unfixed, a consumer's `float BlurAmount` would surface as
  `BlurAmount_0` in the compiled effect, breaking `effect.Parameters["BlurAmount"]` lookups.
  Build the shim that restores the author's names before the body reaches reflection — this was
  an estimate in Phase 65 (§7: "shim-fixable in principle, not validated by building it"); this
  is where it gets built and proven, with a positive-control test asserting a mangled name never
  reaches a consumer-visible parameter table.
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
- **Whether A4's demangling shim is complete** for every construct real slangc's corpus surfaces,
  not just the `_N`/`SLANG_ParameterGroup_*` cases already seen — A6's broader sweep is what
  would surface anything missed.
