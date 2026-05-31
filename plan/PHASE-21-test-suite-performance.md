# Phase 21 — Test-Suite Performance Investigation (21-minute Integration run)

**Status:** Largely resolved (2026-05-30, branch `phase21-test-perf`) — structural cost removed + dev-time AV mitigation documented. **Caveat:** the 21-minute *slow case* did **not** reproduce in-session (every run was fast), so the root cause is identified by structural analysis + evidence fit, not by catching it red-handed. See **Resolution** below.

---

## Resolution (2026-05-30)

**Reproduction attempt:** ran `ShadowDusk.Integration.Tests` alone with a TRX logger + `--blame-hang-timeout 120s`. Result: **108 passed in ~4 s** (and 667 ms on a `--no-build` re-run). Per-test durations were all **< 0.3 s** (sum 4.6 s, parallelized to ~4 s wall). So the cost is **not** in any test's logic and **not** a hanging test — it is **fixture setup**, and the slow case is environmental/non-deterministic (could not be forced on this otherwise-idle box).

**Root cause (by structural analysis, best fit to all evidence):** `CliBinaryFixture` ran a full **`dotnet publish -c Release`** into a fresh GUID `%TEMP%` directory on every construction. That is a nested SDK build that (a) **cold-compiles the entire dependency tree in Release** — a config the Debug test run never otherwise builds — and (b) **copies large native binaries** (`dxcompiler.dll`, SPIRV-Cross) into a brand-new directory the **antivirus has never scanned**. Both are classic warm-vs-cold cost cliffs: seconds when the Release cache + Defender cache are warm, many minutes when cold. This matches the observed ~400× non-determinism far better than any algorithmic cause.

**Fix applied:**
- The test project now has a `ReferenceOutputAssembly="false"` ProjectReference to `ShadowDusk.Cli`, so the CLI binary is always built alongside the tests.
- `CliBinaryFixture` now **reuses that build-output binary** (probing `src/ShadowDusk.Cli/bin/{Debug,Release}/net8.0/`), and only falls back to `dotnet publish` if no built binary exists. The per-run Release publish is gone from the normal path. CLI integration tests still pass (6/6); full suite 108/108.
- Documented dev-time **Defender exclusions** for `bin`/`obj`/`tools`/`%TEMP%` in `CLAUDE.md` (do not disable AV globally).

**Guardrail (recommended, for Phase 30 CI):** add a suite-level timeout so a future regression surfaces as a *failure* in minutes rather than a silent 20-minute hang; per-test `CancellationTokenSource` timeouts (30 s/60 s) already exist.

---

### Original investigation notes (kept for context)

**Status:** Planned (investigation)
**Prerequisite phases:** None — diagnostic; can run in parallel with any work.
**Priority:** Low (correctness unaffected — the slow run still passed), but worth fixing: a 20-minute suite kills the edit→test loop and will be brutal in CI (Phase 30).

---

## The observation

During Phase 17 closeout (2026-05-30), a full `dotnet test ShadowDusk.slnx` run produced this:

```
ShadowDusk.GLSL.Tests          7 passed    408 ms
ShadowDusk.HLSL.Tests         77 passed    491 ms
ShadowDusk.Compiler.Tests     13 passed    955 ms
ShadowDusk.Core.Tests        231 passed    331 ms
ShadowDusk.ImageTests         25 passed      1 s
ShadowDusk.Integration.Tests 108 passed  21 m 43 s   ← outlier
```

**All tests passed (exit 0).** The problem is purely wall-clock: `ShadowDusk.Integration.Tests`
took **21m43s**, while *the same 108 tests in the same session* had completed in **~3 s** on a
prior run (e.g. the `CompileExampleFixtureTests` filtered run, and earlier full runs this
session). Same machine, same build, ~400× variance. The box is strong and idle — nothing here
should take minutes, let alone 21.

> Non-determinism is the key clue: identical test logic, wildly different runtime → the cost is
> **environmental / external**, not algorithmic. The investigation must reproduce the *slow*
> case, not just confirm the fast one.

---

## What's different about `ShadowDusk.Integration.Tests`

It is the only project that touches heavyweight external machinery (the other 5 are fast and
in-process). Things only it does:

1. **Spawns the CLI executable as a child process** — `InvocationMode.CliProcess` via
   `TestHelpers.CompileViaCliAsync` + `CliBinaryFixture`/`Cli/CliIntegrationTest.cs`
   (`Process.Start` of `ShadowDusk.Cli.exe`). Repeated process launches.
2. **Invokes native DXC** (Vortice.Dxc) and **SPIRV-Cross** P/Invoke — `Dxc/`, `Glsl/`,
   `Reflection/` test folders load and call native libraries.
3. **Per-test timeouts of 30 s (×20) and 60 s (×4).** If a handful of tests *stall* (rather than
   fail), each can burn its full timeout silently — a few 30–60 s stalls across 108 tests
   accumulates fast, and a hang that's retried compounds it.
4. Writes per-test temp directories under `%TEMP%` and reads/writes `.mgfx` bytes.

---

## Hypotheses (ranked, to confirm/refute — do not assume)

1. **Antivirus / Defender real-time scanning of freshly-built native artifacts.** Each
   `Process.Start` of a just-rebuilt `ShadowDusk.Cli.exe`, and first load of `dxcompiler.dll` /
   `libspirv-cross`, can trigger an on-access scan that blocks for seconds — intermittently,
   depending on Defender state/cache. This fits the non-determinism perfectly (warm cache → 3 s;
   cold/just-rebuilt → minutes). **Check first.**
2. **CLI child-process startup cost / contention.** `Process.Start` per test, possibly with the
   CLI doing first-run work (native restore, JIT, tiered comp) on each launch; or many spawns
   serialized behind a lock. Measure process-start latency in isolation.
3. **A specific test (or fixture) intermittently hanging to its 30/60 s timeout.** Identify
   whether the 21 min is spread across many tests or concentrated in a few that hit their
   `CancellationTokenSource` deadline. Per-test timing will show this immediately.
4. **Native DXC/SPIRV-Cross load or compile stalls** (lock contention, repeated init, disk I/O on
   the native libs). Distinguish from #1 (AV) by testing with real-time protection paused.
5. **First-run / cold-cache effects** (NuGet, native lib extraction, JIT) — but the *first* full
   run this session was fast, so this is lower-probability; still worth ruling out.
6. **Disk / temp-dir contention** (`%TEMP%` churn, antivirus on temp, slow handle creation).

---

## Investigation plan

1. **Reproduce with per-test timing.** Run the project alone with diagnostics:
   `dotnet test tests/ShadowDusk.Integration.Tests/ShadowDusk.Integration.Tests.csproj -- RunConfiguration.TreatNoTestsAsError=true`
   plus `--logger "console;verbosity=detailed"` (or a TRX logger) to get **per-test durations**.
   Run it several times to catch a slow instance. Pinpoint whether the time is concentrated
   (few tests near their timeout) or spread (every test slower).
2. **Bisect by category.** Time each folder/trait separately (`--filter`): `Cli` (process-spawn)
   vs `Dxc`/`Glsl`/`Reflection` (native) vs `Tests` (pipeline) vs `Determinism`. Whichever
   subset carries the 21 min names the culprit.
3. **Toggle Defender.** With real-time protection temporarily off (or `ShadowDusk` build/temp/
   native-lib dirs added to the exclusion list), re-run. A large drop confirms hypothesis #1 →
   the fix is a documented dev-time exclusion, not code.
4. **Measure CLI spawn cost directly.** Time a bare `Process.Start` of the built CLI exe in a
   loop vs `InvocationMode.DirectPipeline` (in-process). If CLI-process tests dominate, consider
   defaulting more integration tests to `DirectPipeline` and keeping a small CLI smoke set.
5. **Watch for stalls live.** While a slow run is in progress, observe with Process Explorer /
   Resource Monitor: is `dotnet`/`ShadowDusk.Cli`/`MsMpEng.exe` (Defender) burning CPU or
   blocked on I/O? This catches an AV scan or a spin-wait in the act.
6. **Check for accidental work-amplification** — e.g. a fixture rebuilding the CLI per test, a
   retry loop, or `CliBinaryFixture` doing expensive discovery/build on each instantiation.

---

## Likely fixes (depending on root cause)

- **If AV (most likely):** document dev-time Defender exclusions for the repo's `bin`/`obj`/
  native-tool/`%TEMP%` paths; do **not** disable AV globally. Note it in `CLAUDE.md` Build & Test.
- **If CLI-spawn:** make `DirectPipeline` the default for bulk integration coverage; keep a
  minimal `[Trait]`-tagged CLI smoke suite (a few tests) for the actual process path. Possibly
  reuse one built CLI binary across the run (fixture already exists — verify it's not rebuilding).
- **If a hanging test:** fix the hang and/or tighten the timeout so a stall fails fast instead of
  silently eating 30–60 s.
- **If native init:** cache/reuse the DXC/SPIRV-Cross handles across tests where safe.

---

## Definition of Done

- [~] Root cause **identified** (the `dotnet publish -c Release` in `CliBinaryFixture`, best fit to the cold/warm non-determinism) — but the 21-minute slow case **did NOT reproduce** in-session (all runs fast); reproduction by structural analysis + evidence, not by catching it live.
- [x] Per-test timing captured (TRX): all tests < 0.3 s, ~4 s total — dominant cost localized to **fixture setup**, not test logic or a hanging test.
- [x] Fix applied: `CliBinaryFixture` reuses the build-output CLI binary instead of a per-run Release publish; suite stays seconds-range (108/108, 667 ms no-build).
- [x] Environmental cause (AV scanning freshly-built native binaries) documented in `CLAUDE.md` with dev-time Defender-exclusion guidance.
- [~] Guardrail: per-test timeouts already exist; a **suite-level CI timeout** is recommended and handed to [Phase 30](PHASE-30-cross-platform-ci.md).

---

## Notes / evidence to attach when picking this up

- The 21m43s run was the Phase-17 closeout full-suite check (commit context around `ac42d0f`).
- Earlier same-session runs of this exact project: ~3 s (e.g. the filtered
  `CompileExampleFixtureTests` run completed in 158 ms; full project runs were single-digit seconds).
- Platform: Windows 11 Pro, strong workstation, otherwise idle.
- Test framework: xUnit; project uses both `InvocationMode.CliProcess` and `DirectPipeline`,
  per-test `CancellationTokenSource` timeouts of 30 s / 60 s.
