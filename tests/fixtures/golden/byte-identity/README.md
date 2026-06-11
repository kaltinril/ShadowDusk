# Cross-host byte-identity manifest (Phase 37 tail 1)

`manifest.json` is the committed SHA-256 of every `.mgfx`/`.fxb` the byte-identity corpus
produces — one entry per fixture×target, sorted ordinally. `CrossHostByteIdentityTests`
(`tests/ShadowDusk.Integration.Tests`) recompiles the corpus in-process on **every CI OS**
(windows / ubuntu / macos) and asserts each output hash against this ONE file. When that
passes off-Windows, the Linux/macOS bytes are proven equal to the win-x64 bytes — which
transfers the Windows rung-4 render proofs (Phases 17/18/39–40) to those hosts
byte-for-byte (Core Design Constraint 3: deterministic output).

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

## Input normalization (what makes the hashes host-independent)

- Fixture source text is read with line endings normalized to LF (git checkout EOL policy
  differs per OS; the experiment must feed identical input bytes everywhere).
- `CompilerOptions.SourceFileName` is the fixed fixture-relative name, never an absolute
  host path. The `SourceFileName_DoesNotAffect_OutputBytes*` tests prove the name does not
  leak into output bytes for any of the three targets.
- The corpus contains no `#include`-bearing fixtures.

## Provenance

- **Generated on:** win-x64 (Windows 11), 2026-06-11, via the regeneration path below.
- **Compiler git SHA:** the commit that last touched `manifest.json` — `git log -1 --format=%H -- tests/fixtures/golden/byte-identity/manifest.json`.
- **Pinned natives the hashes depend on:** DXC 1.7.2212.40 (Vortice.Dxc 3.3.4 on
  win/linux; our own dylib from the identical commit on macOS, tag
  `native-dxc-1.7.2212.40`), SPIRV-Cross (Silk.NET.SPIRV.Cross.Native 2.23.0),
  vkd3d-shader 1.17 (tag `native-vkd3d-1.17`). Bumping any of these legitimately changes
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
`plan/PHASE-37-cross-platform-native-availability.md` as an open finding.
