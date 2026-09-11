# A1 probe: is `slang-llvm.dll` excludable for a `-target hlsl`-only embed?

THROWAWAY measurement evidence for Phase 66 A1. Not shipped, not a gate, not referenced by any
product code.

## Method

1. Downloaded the pinned slangc v2026.14.1 win-x64 release
   (`slang-2026.14.1-windows-x86_64.zip`), verified SHA-256
   `5ED0A59D650A0AF0ACA45D5DB4E083B3D8FB5CEA05748747DD95DFBE9C580658` (matches
   `validation/SlangCorpus/Program.cs`'s pin).
2. Extracted to a scratch copy, deleted `bin/slang-llvm.dll` (84,398,592 bytes) from that copy
   only.
3. Ran `slangc -target hlsl` against every entry point in the 17-shader
   `tests/fixtures/shaders/slang/` corpus (20 entry points) and the Phase 65 Gum-shaped +
   generics-probe shaders (`plan/PHASE-65-appendix/slang-probe/shaders/`, 4 entry points,
   including the generics/interface probe) — `run-without-slang-llvm.txt`.
4. Positive control, same no-LLVM binary: confirmed `-target hlsl` still exits 0 while
   `-target shader-sharedlib -emit-cpu-via-llvm` now fails with
   `error[E52002]: pass-through compiler not found` — `positive-control-cpu-target.txt`. This
   shows the DLL's absence is actually load-bearing for LLVM-backed CPU codegen (so the
   corpus passing isn't "nothing calls it anyway"), and that `slangc` fails loudly and
   specifically rather than silently degrading when a route that does need it is requested.

## Result

All 24 entry points across all 21 files compile through `-target hlsl` with `slang-llvm.dll`
deleted. `slangc -v` also launches fine without it. Matches upstream: `slang-llvm.dll` is
Slang's LLVM/Clang-backed pass-through compiler for CPU/host codegen targets
(`-emit-cpu-via-llvm`, `host-callable`, `shader-object-code`, etc.) — orthogonal to the
`-target hlsl` source-to-source route this product uses.

## Size

Measured from the same extracted release (`bin/` directory, decimal MB):

| | bytes | MB |
|---|---|---|
| `slang-llvm.dll` alone | 84,398,592 | 84.4 |
| `bin/` total, `slang-llvm.dll` removed | 41,764,337 | 41.8 |
| `bin/` total, unmodified | 126,162,929 | 126.2 |

`bin/` here is the whole extracted release's binary directory (`slangc.exe` + `slang.exe` +
`slangd.exe` + `slangi.exe` + all DLLs + the `slang-standard-module-2026.14.1/` stdlib dir),
same basis Phase 65 §4c used for its ~121 MB figure (the 126.2 MB above is a fresh measurement
of the same thing, not a discrepancy — Phase 65 rounded per-component). A2's actual vendored
set would drop `slang.exe`/`slangd.exe`/`slangi.exe` (separate CLI/language-server/interpreter
binaries, not needed to invoke `slangc.exe`), which is a packaging-scope decision for A2, not
measured here.

## Repro

```
slangc <file> -target hlsl -entry <Entry> -stage <vertex|fragment> -o -
```
with `bin/slang-llvm.dll` removed from the pinned v2026.14.1 win-x64 release.
