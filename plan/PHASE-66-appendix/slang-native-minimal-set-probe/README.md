# A2 probe: the TRUE minimal slangc file set for a `-target hlsl`-only embed

THROWAWAY measurement evidence for Phase 66 A2. Not shipped, not a gate, not referenced by
any product code. Builds on A1's finding (`slang-llvm.dll` excludable,
`plan/PHASE-66-appendix/slang-llvm-exclusion-probe/`) by testing every other file in the
release for droppability too, empirically, rather than trusting the "probably also
droppable" guess A1 left open.

## Method

1. Downloaded the pinned slangc v2026.14.1 win-x64 release
   (`slang-2026.14.1-windows-x86_64.zip`), verified SHA-256
   `5ED0A59D650A0AF0ACA45D5DB4E083B3D8FB5CEA05748747DD95DFBE9C580658` (same pin as A1 and
   `validation/SlangCorpus/Program.cs`).
2. Extracted to a scratch copy. Started from A1's no-LLVM baseline (`bin/` minus
   `slang-llvm.dll`, re-measured at 41,764,337 bytes — matches A1 exactly) and removed
   candidates incrementally, re-running the SAME corpus after each step:
   `slangc <file> -target hlsl -entry <Entry> -stage <vertex|fragment> -o -` against all 24
   entry points across the 17-shader `tests/fixtures/shaders/slang/` corpus (20 entry
   points) and Phase 65's Gum-shaped + generics-probe shaders
   (`plan/PHASE-65-appendix/slang-probe/shaders/`, 4 entry points, generics/interfaces
   included).
3. Removal order and result, each step cumulative on top of the last:
   - `slang.exe`, `slangd.exe`, `slangi.exe` (separate CLI / language-server / interpreter
     binaries) — **droppable**, 24/24 still pass.
   - `gfx.dll`, `gfx.slang` (graphics-API abstraction layer), `slang-glsl-module.dll`,
     `slang-glslang.dll` (glslang, GLSL-target support) — **droppable**, 24/24 still pass.
   - `slang.slang` (core-module source) + the whole `slang-standard-module-2026.14.1/`
     stdlib directory (25 files, 5.9 MB) — **droppable**, 24/24 still pass. The stdlib
     `slangc.exe` actually needs at runtime is embedded in `slang-compiler.dll`.
   - `slang.dll`, `slang-rt.dll` — **droppable**, 24/24 still pass.
4. A **fresh, clean** test (not an incremental copy — a brand-new 2-file directory
   containing only `slangc.exe` and `slang-compiler.dll` copied straight from the
   unmodified release) confirms the finding isn't an artifact of incremental testing:
   still 24/24 (`run-minimal-set.txt`, captured from the actual restored
   `tools/slang/win-x64/` the new `tools/restore.ps1`/`restore.sh` entries produce).
5. **Negative control**: from the same clean 2-file baseline, deleting `slang-compiler.dll`
   itself fails all 24 entries — confirms the corpus test discriminates (it isn't just
   "passes no matter what's removed").
6. **Positive control** (A1's CPU/LLVM-target check, re-run against the 2-file minimal
   set): `slangc -v` launches and prints `2026.14.1`; `-target hlsl` still exits 0;
   `-target shader-sharedlib -emit-cpu-via-llvm` still fails loudly with
   `error[E52002]: pass-through compiler not found` (expected — `slang-llvm.dll` was
   dropped in A1's step, and this route was never in scope).

## Result

**True minimal vendored set: `slangc.exe` + `slang-compiler.dll` only.**

| File | Bytes |
|---|---|
| `slangc.exe` | 276,480 |
| `slang-compiler.dll` | 25,334,784 |
| **Total** | **25,611,264 (~24.4 MiB / 25.6 MB decimal)** |

Versus the unmodified release (126,162,929 bytes) this is a **~80% cut**; versus A1's
no-LLVM figure (41,764,337 bytes) it is a **further ~39% cut**.

SHA-256 of the two files as extracted from the pinned v2026.14.1 win-x64 release (informational — the shipped pin in `tools/restore.ps1`/`restore.sh` verifies the whole release zip before extracting, the same pattern `validation/SlangCorpus/Program.cs` already uses, rather than re-pinning individual post-extraction file hashes):

```
B9F786A651569AA4F968E4014D04B6E2F4F1A6C9584F47EA9F4CCC841FDEEEEB  slangc.exe
2271CA931FFA18FB59A649BB91B22F36AFD6EC34584E17F8BB28E00143614CA4  slang-compiler.dll
```

## Important side finding: `slangc.exe` writes to its own directory at runtime

Running the corpus against the 2-file minimal set causes `slangc.exe` to create a new file,
`slang-glsl-module.bin` (1,312,404 bytes), in its OWN directory — confirmed by a fresh
`LastWriteTime` after a clean run against a directory that had no such file beforehand.
This happens even for the `-target hlsl` route this product uses, so it isn't a GLSL-target
side effect we can route around. It reads as a compiled/serialized stdlib-module cache
`slangc` writes on first use (consistent with `slang-compiler.dll` embedding the stdlib
source and lazily materializing a compiled form next to itself).

**This is NOT part of the vendored set** (it's regenerated on demand, not shipped), but it
means the packaged `runtimes/win-x64/native/` directory a consumer's build copies the
native into **must be writable at runtime**, not just readable. Flagged here for **A3** to
resolve or explicitly accept (e.g. a consumer publishing self-contained to a read-only
container filesystem would need this addressed); not solved by A2, which is packaging
plumbing only.

## Repro

```
slangc <file> -target hlsl -entry <Entry> -stage <vertex|fragment> -o -
```
with only `slangc.exe` and `slang-compiler.dll` present in the working directory (every
other file from the pinned v2026.14.1 win-x64 release removed).
