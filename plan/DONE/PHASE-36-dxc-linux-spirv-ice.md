# Phase 36 — DXC SPIR-V "Internal Compiler error" on the Linux Debug-CLI / spawned-process compile path

**Status:** ✅ **RESOLVED via Phase 37 Finding B (2026-06-10)** — root cause was Vortice.Dxc marshalling the `LPCWSTR*` argument array as UTF-16 while DXC's Linux build uses the 4-byte native `wchar_t` (UTF-32); every Linux invocation through Vortice ICEd (`0x80AA000C`, empty `Internal Compiler error:`). Fixed by `src/ShadowDusk.HLSL/Dxc/DxcNativeInterop.cs` (platform-`wchar_t` argument encoding, same pinned native). Debug-vs-Release and spawned-vs-in-process were never factors; the "in-process works on Linux" evidence below was a false positive (ImageTests soft-skip-as-pass on headless CI — no GL context, DXC never invoked). Was: ABSORBED into [Phase 37](PHASE-37-cross-platform-native-availability.md) (2026-06-07). Created from the Phase 30 `wasm.yml` red badge investigation.

> ⚠️ **Scope re-corrected 2026-06-07 (Phase 37):** this doc's TL;DR claim that "Release in-process compile works on real Linux" is **DISPROVEN**. The `ci.yml` **integration-tests** job (Release, in-process `EffectCompiler`) ICEs on ~120 ubuntu tests — including `Minimal.fx` — while the **same run's** `build-and-test` ImageTests pass 27/27. That paradox is now the crux of **Phase 37 → Finding B**. Track the Linux DXC ICE there; this doc is retained for the original Debug-CLI/spawned-process evidence and the Vortice.Dxc native facts.
**Track:** Reach (Part 1 of THE PURPOSE) + CI quality. The desktop in-process compile works on real Linux; the gap is the `wasm.yml` browser-smoke's corpus-compile path.

---

## TL;DR (CORRECTED — read this; the original alarm was wrong)

The **real `ubuntu-latest` runner DOES compile via DXC.** Phase 30/26 CI runs `ShadowDusk.ImageTests` **27/27 green on ubuntu**, and `ImageRegressionTests` there call `new EffectCompiler().CompileAsync(... OpenGL ...)` — so the **Release, in-process** DXC→SPIR-V→GLSL path works on real Linux. My first conclusion ("Linux compile is broken") was **wrong**; the desktop reach is largely intact.

The **actual** red is narrower: the Phase 30 `wasm.yml` "Browser render smoke" → `compile-corpus-sd.mjs` — which builds the CLI in **Debug** and **spawns it as a child process** — ICEs on Linux with DXC `Internal Compiler error:` (→ `X0000`). And a freshly-installed **WSL Ubuntu** ICEs broadly, but that env is **degraded** (the `dotnet-install` script doesn't install DXC's runtime deps), so WSL is **NOT a faithful repro**. So what's actually failing is the **Debug-CLI / spawned-process corpus compile on Linux**, not the product's in-process compile.

## Likely-quick first experiment

Change `tests/ShadowDusk.BrowserTests/compile-corpus-sd.mjs` (+ `publish-sample-sd*.mjs`) to build/use the CLI in **`-c Release`** (not `-c Debug`), then re-run the browser smoke (PR + `run-browser` label). If Release-CLI compiles on the real runner like Release-in-process does, the badge goes green and the fix is one word. If not, the difference is **spawned-process vs in-process** DXC on Linux (static-init / a Debug-only native-resolution quirk) — investigate from there.

## Evidence

- **Real `ubuntu-latest`, Release, in-process:** `ImageTests` 27/27 pass (incl. `ImageRegressionTests` compiling via DXC for OpenGL) — DXC works on real Linux.
- **Real `ubuntu-latest`, Debug CLI, spawned (`wasm.yml` smoke):** `compile-corpus-sd` 0/10, raw DXC = `Internal Compiler error:`.
- **WSL Ubuntu (.NET 8 via dotnet-install):** CLI + in-process both ICE — but degraded env, treat as **unreliable**, not authoritative.
- **Not a missing backend:** `libdxcompiler.so` (Vortice.Dxc **3.3.4**, DXC **1.7.0.37**) has SPIR-V (SPIRV-Tools strings) and `ldd` resolves.

> The Phase-34 advanced-texture `X0000` on Linux (scoped Windows-only in Phase 30 CI) may be the same or a separate genuine intrinsic gap — re-check after the Debug-vs-Release question is settled.

> **Does NOT block the WASM/KNI publish or the consumer:** the in-browser package uses `dxcompiler.wasm` (a different compiler), and the published `.mgfx` output is render-validated on Windows. This is a CI/desktop-Linux investigation, not a release blocker.

## Why this is phase-sized (not a quick patch)

- The fix changes the **DXC native** → it changes **emitted SPIR-V → GLSL → `.mgfx` output on every platform, including Windows**. That is **fidelity-sensitive**: it must be re-validated against the `mgfxc` golden corpus (render, rung-4) AND ShadowDusk's own byte-identity tests on Windows, or it silently breaks the "renders like `mgfxc`" promise.
- It likely affects **macOS** identically (untested — same Vortice.Dxc native family) and interacts with the **DirectX-on-Linux (`vkd3d`) path**, which also has never been run-validated on Linux.

## Candidate approaches (investigate in order; reproduce-first via WSL/Docker)

1. **Bump `Vortice.Dxc`** to a newer version whose Linux native doesn't ICE (simplest if it works). Risk: output changes on all platforms → full re-validation. Pin the exact DXC commit and record it.
2. **Swap the native source** — e.g. Microsoft's `Microsoft.Direct3D.DXC` NuGet, or a Khronos/Google DXC release, or a custom DXC build with SPIR-V — bundled per-RID like the other natives. More control, more packaging work.
3. **Investigate the invocation** — confirm it's the native and not a Vortice managed-API/encoding/include-handler quirk on Linux (low probability given the raw "Internal Compiler error", but cheap to rule out).
4. If a working Linux DXC differs in output from the current Windows DXC, decide whether to **adopt the new DXC on Windows too** (keep one DXC everywhere — preserves cross-host byte-identity, the constraint-3 goal) and **regenerate** any ShadowDusk-anchored references, re-validating renders against `mgfxc`.

## Definition of done

- ShadowDusk compiles the PS-only (and VS-driven, Phase 28) corpus **on Linux** (and macOS) with **no ICE**, producing valid `.mgfx`.
- Rung-4: the Linux-compiled `.mgfx` **renders pixel-equivalent to `mgfxc`** (same bar as Phase 17/18), and Windows output is **unregressed** (golden corpus + byte-identity green on Windows).
- The Phase 30 `wasm.yml` browser smoke goes green; the Phase-34 advanced-texture tests can be **un-scoped from Windows-only** in `ci.yml`.
- `ci.yml`'s integration job actually runs the Linux compile to completion and passes (cross-platform reach proven — the thing Phase 30 was meant to prove).

## Prerequisites / related

- **PR #19** (CLI POSIX-absolute-path fix) is a **prerequisite** — without it the CLI can't even parse a Linux `/abs/path.fx` to reach the DXC step. Merge it first. (Real bug: `mgfxc /abs/shader.fx out.mgfx` was broken on Linux/macOS; verified, 22/22 parser tests.)
- Unblocks: Phase 30 §16 (browser/WASM CI green), Phase 34 Linux advanced-texture coverage, and the macOS/Linux halves of the reach claim in `CLAUDE.md` → "What success actually means".
- Repro tooling: WSL Ubuntu with `~/.dotnet` (.NET 8 installed during this investigation) is a faithful repro for the real flags; Docker `mcr.microsoft.com/dotnet/sdk:8.0` matches CI even more closely (daemon must be started).

## Provenance

Found 2026-06-07 while fixing the red `wasm.yml` badge (user: "get the red wasm thing fixed" → "investigate + fix it properly" → "if it requires a phase let me know"). The badge had two layers: (1) a CLI POSIX-path bug [fixed, PR #19], and (2) this DXC-Linux ICE [this phase]. See memory `phase30-ci-release-done` (which scoped the Phase-34 Linux X0000 Windows-only) and `private-repo-actions-minutes`.
