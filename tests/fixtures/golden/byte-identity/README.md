# Cross-host byte-identity manifest (Phase 37 tail 1)

`manifest.json` is the committed SHA-256 of every `.mgfx`/`.fxb` the byte-identity corpus
produces — one entry per fixture×target, sorted ordinally. `CrossHostByteIdentityTests`
(`tests/ShadowDusk.Integration.Tests`) recompiles the corpus in-process on **every CI OS**
(windows / ubuntu / macos) and asserts each output hash against this ONE file. When that
passes off-Windows, the Linux/macOS bytes are proven equal to the win-x64 bytes — which
transfers the Windows rung-4 render proofs (Phases 17/18/39–40) to those hosts
byte-for-byte (Core Design Constraint 3: deterministic output).

`warnings-manifest.json` sits beside it (issue #335): the same keys, each mapped to the
`CompiledShader.Warnings` list the compile produced, one `ShaderError.FxcFormattedMessage` per
entry (`File(Line,Col-Col): warning CODE: message`), in order; an empty list for the many fixtures
that warn about nothing. A compiler's non-fatal diagnostics are part of the cross-host contract
exactly like the bytes: the same tests assert it on every CI OS, and
`tests/ShadowDusk.BrowserTests/browser-vkd3d-gate.mjs` compares the browser's warnings against it.
`ImplicitTruncationWarning.fx` is the fixture whose lists are non-empty (vkd3d `W5300` on
`DirectX_Vkd3d` and `FNA`, DXC's `-Wconversion` text on `OpenGL`), and the tests fail when no entry
of a target is non-empty, so a host that drops warnings on success can never match the manifest.

## Key format

`<Target>/<fixture path>` → lowercase SHA-256 hex of the compiled output bytes.

| Target key | Pipeline | Output |
|---|---|---|
| `OpenGL` | DXC → SPIRV-Cross → managed rewrite + MGFX writer | `.mgfx` (MGFX v10) |
| `DirectX_Vkd3d` | vkd3d-shader → DXBC + managed `RdefReader` + MGFX writer | `.mgfx` (MGFX v10) |
| `FNA` | vkd3d-shader SM1–3 → `Fx2EffectWriter` | `.fxb` (fx_2_0) |

`DirectX_Vkd3d` deliberately pins the **cross-platform vkd3d backend on every OS,
including Windows** — the default `d3dcompiler_47` oracle is Windows-only (host-dependent
by design) and must never appear in this manifest.

## `dxc-targets-manifest.json`: Vulkan and DirectX 12

The two targets whose shipped bytecode comes straight out of DXC, pinned by
`DxcTargetsCrossHostByteIdentityTests` over the **whole** corpus (every `.fx` under
`tests/fixtures/shaders`, includes served from memory under their relative names), each one
release and with `Debug` on. Keys are `Vulkan/`, `Vulkan.Debug/`, `DirectX12/` and
`DirectX12.Debug/` + the fixture path; each value is an object:

| Field | Meaning |
|---|---|
| `errors` | A fixture that does not compile: its diagnostics, verbatim. Compiling on one host and failing on another is a difference like any other. |
| `mgfx` | SHA-256 of the `.mgfx`. For DirectX 12, asserted **on Windows only** (see below). |
| `spirv` | Vulkan: a digest of every SPIR-V module DXC returned, raw. |
| `dxil` | DirectX 12: a digest of every DXIL container's disassembly, canonicalized (below). |
| `mgfxNormalized` | DirectX 12: the `.mgfx` with each DXIL container replaced by its canonical digest, its shader-record length and the header's effect key zeroed. |
| `warnings` | The warning list, verbatim. |

**Why DirectX 12 needs a canonical form.** A DXIL container differs by construction off
Windows: its digest is the `dxil.dll` signature, which only Windows produces (`SD0214`
elsewhere), and the bitcode embeds DXC's own identity string (`!llvm.ident`, and the debug
information's `producer`), so the `HASH` part, the PDB name derived from it, the container's
size, the MGFX record length and the effect key all follow. The canonical form removes exactly
those, and nothing else: the disassembly minus the `; shader hash:` and `; shader debug name:`
lines, the `!llvm.ident` node and its string, with every other occurrence of that string
replaced by a fixed token. The two unsigned-DXIL warnings (`SD0214`, and DXC's own *"DXIL.dll
not found"*) are left out of `warnings`/`errors` for the same reason. Never widen this.

Regenerate exactly as `manifest.json` (below). `SHADOWDUSK_BYTE_IDENTITY_DUMP=<dir>` writes every
fixture's SPIR-V, DXIL container and DXIL disassembly there; the CI integration lane sets it and
uploads the directory when the job fails, so a mismatch can be diffed against a win-x64 dump.

## Input normalization (what makes the hashes host-independent)

- Fixture source text is read with line endings normalized to LF (git checkout EOL policy
  differs per OS; the experiment must feed identical input bytes everywhere).
- `CompilerOptions.SourceFileName` is the fixed fixture-relative name, never an absolute
  host path. The `SourceFileName_DoesNotAffect_OutputBytes*` tests prove the name does not
  leak into output bytes for any of the three targets.
- The corpus contains no `#include`-bearing fixtures.

## Provenance

- **Generated on:** win-x64 (Windows 11), 2026-09-10 (regenerated for the vkd3d 1.17 to 2.1 bump, Phase 56: the DirectX_Vkd3d and FNA hashes moved, OpenGL did not), via the regeneration path below.
- **Compiler git SHA:** the commit that last touched `manifest.json` — `git log -1 --format=%H -- tests/fixtures/golden/byte-identity/manifest.json`.
- **Pinned natives the hashes depend on:** DXC 1.7.2212.40 (Vortice.Dxc 3.3.4 on
  win/linux; our own dylib on macOS, tag `native-dxc-1.7.2212.40`). These are NOT all one
  commit: the Windows DLL and our macOS dylib are `e043f4a1`, Vortice's linux-x64 `.so` reports
  `8c9d92be7` (the `v1.7.2212` tag, 28 commits earlier). The OpenGL hashes match on all three
  hosts anyway; this manifest pins no Vulkan or DirectX 12 output, so that gap is unmeasured there
  (see `project_facts.md`), SPIRV-Cross (Silk.NET.SPIRV.Cross.Native 2.23.0),
  vkd3d-shader 2.1 (tag `native-vkd3d-2.1`). Bumping any of these legitimately changes
  the manifest — regenerate and review.

## How to regenerate

Manifest churn on a legitimate, reviewed compiler-output change is expected and
reviewable, exactly like goldens. On **win-x64** (keep the provenance anchor one host):

```powershell
.\tools\restore.ps1     # vkd3d natives must be present
dotnet build ShadowDusk.slnx -c Release
$env:SHADOWDUSK_REGENERATE_BYTE_MANIFEST = "1"
dotnet test tests/ShadowDusk.Integration.Tests -c Release --no-build `
  --filter "FullyQualifiedName~CrossHostByteIdentityTests"
```

The tests rewrite `manifest.json` (in the source tree) instead of asserting; each target
section regenerates independently, so a vkd3d-only change leaves the OpenGL entries
untouched. Commit the diff and explain it in the PR.

## Honesty rule

If any OS produces different bytes, that is a **real fidelity finding** in the per-OS
native builds (e.g. a non-deterministic or divergent DXC/vkd3d build) — never loosen this
to structural equality or per-OS manifests. The test failure message carries both hashes
(manifest vs this-host) per mismatching fixture×target; capture them in
`plan/DONE/PHASE-37-cross-platform-native-availability.md` as an open finding.
