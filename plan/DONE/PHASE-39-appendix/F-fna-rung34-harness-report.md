# Appendix F — FNA rung-3/4 harness build report (2026-06-09)

> The agent-built `validation/FnaValidation` harness: provenance, design decisions, and the
> FIRST run's results — i.e. the state **before** the `D3d9BytecodePatcher` fixes, preserving
> the discovery record of product bugs 1–2 (texkill writemask / texld swizzle). The final
> post-fix state is gate 10/10 with every row PASS (see the phase doc's Evidence ladder).
> Preserved near-verbatim from the build agent's final report.

## Files created (all under `validation/FnaValidation/`)

- `FnaValidation.csproj` — net8.0 Exe, x64, NOT in `ShadowDusk.slnx`; ProjectReferences
  `src/ShadowDusk.Compiler` + `external/FNA/FNA.Core.csproj`; copies fnalibs x64 natives +
  D3D12 Agility SDK to output; `DefaultItemExcludes=external/**` (**required** — the SDK
  globs otherwise swallow FNA's sources).
- `Program.cs` — orchestration, in-process pixel compare, verdict table, gate exit code.
- `FnaShaderInputs.cs` — corpus + `SetParams` (gate values are a verbatim port of
  `DxShaderInputs.SetParams`; second textures get the cat, mirroring the DX harness's
  `_dissolveTex` solution) + `FindRepoRoot`/`CatPath`.
- `ReferenceFx2Compiler.cs` — raw `d3dcompiler_47` `D3DCompile(pTarget:"fx_2_0",
  pEntrypoint:NULL)` P/Invoke (ID3DBlob via vtable), textual include inliner (corpus uses
  none), and the OPENGL macro-parity checker.
- `FnaEffectImageRenderer.cs` — one-process/one-GraphicsDevice FNA `Game`; scene mirrors
  `DxEffectImageRenderer` exactly (prime SpriteBatch VS → Immediate Begin with effect →
  fullscreen cat); hooks `FNALoggerEXT.LogError` to capture exact MojoShader text (**FNA3D
  logs parse errors, it doesn't throw**; the managed `Effect` ctor then throws — the hook is
  assigned in Program.cs BEFORE the Game is constructed, which stops FNA's default Console
  hook from claiming it).
- `restore-fna.ps1` — idempotent acquisition (verified skip-when-present).
- `README.md`, plus `validation/.gitignore` gains `output-fna/` and `FnaValidation/external/`.

## Provenance (pinned)

- **FNA tag 26.06** (latest release via GitHub API), cloned `--recursive --depth 1`;
  **MojoShader submodule `abdc8036`**. FNA ≥ 24.01 uses **SDL3** — confirmed (FNA.Core
  compiles `SDL3.Legacy.cs`; fnalibs ships `SDL3.dll`).
- **fnalibs**: the old `fna.flibitijibibo.com/archive/fnalibs.tar.bz2` URL is **404 —
  distribution moved** to the `fnalibs` CI artifact of `FNA-XNA/fnalibs-dailies` (per current
  FNA docs); downloaded via authenticated `gh` from run **27180614567** (main, success). The
  restore script defaults to latest-successful-run with a `-FnalibsRunId` override
  (**artifacts expire ~90 days** — re-restoring later picks a newer run).
- Profile parity held for all 22 sources: the parity checker flagged **zero** shaders
  macro-parity-unsafe (every `#if OPENGL` block is only the SHADERMODEL/`SV_POSITION`
  defines); the reference arm prepends `#define OPENGL 1\n#line 1\n`.
- **Tolerance**: PASS = zero pixels with per-channel delta > **4/255** — the
  `compare_dx.py` default used for the Phase 18 cross-compiler comparison. Backend pinned
  `FNA3D_FORCE_DRIVER=D3D11` (RTX 3080); results identical across two runs.
- The oracle arm's `X4717` deprecation warning behaved exactly as the golden README
  documents (warning only; output unaffected).

## First-run results (BEFORE the D3d9BytecodePatcher fixes)

| Shader | ref | cand load | maxd | verdict |
|---|---|---|---|---|
| Grayscale…Dots (9 gate PS-only) | ok | ok | 0 (Dots 1) | PASS |
| **Dissolve (GATE)** | ok | **FAIL** | — | product bug 1 |
| BasicShader…Teleport (ex. below) | ok | ok | 0 | PASS |
| **SpriteAlphaTest** | ok | **FAIL** | — | product bug 1 |
| minimal (golden) | ok | ok | 0 | PASS |
| **textured (golden)** | ok | **FAIL** | — | product bug 2 |

GATE 9/10; every shader that loaded rendered pixel-identical (maxd 0; Dots maxd 1,
mean 0.004).

## Product bugs found (candidate-only rung-3 load failures; the reference loaded fine)

1. **TEXKILL partial writemask** — `new Effect()` throws `InvalidOperationException:
   MOJOSHADER_compileEffect Error: TEXKILL writemask must be .xyzw` (Dissolve,
   SpriteAlphaTest). Byte-level: vkd3d 1.17 emits `texkill` with dest writemask `0x2`/`0x1`;
   fxc always emits `0xF`. MojoShader hard-fails non-.xyzw (`mojoshader.c:1753`,
   `state_TEXKILL`). Affects every FNA-target shader using `clip()`/`discard`, at any profile.
2. **TEXLD src0 swizzle below SM3** — `MOJOSHADER_compileEffect Error: TEXLD src0 must not
   swizzle` (golden `textured.fx`, literal ps_2_0). vkd3d emits `texld` with src0 swizzle
   `0x04` (.xyxx); fxc emits `0xE4` (none). MojoShader forbids src0 swizzle pre-SM3
   (`mojoshader.c:1963–1966`) — which is why the same vkd3d swizzle is accepted in the
   ps_3_0 shaders. Affects literal ps_2_0/ps_1_x profiles only.

Both are exactly the "MojoShader is stricter than fxc" class the Phase 39 research predicted.
Both were fixed by the `D3d9BytecodePatcher` fresh-temp routing (see the phase doc); the
later **Dissolve render divergence** (a third, subtler bug) is Appendix G.
