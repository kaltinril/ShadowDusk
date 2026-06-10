# FNA fx_2_0 golden fixtures

Reference D3D9 Effects Framework binaries (`0xFEFF0901`, "fx_2_0") produced by **Microsoft's
own effect compiler** — the format FNA consumes via FNA3D/MojoShader. They are the structural
cross-check oracle for ShadowDusk's `Fx2EffectWriter` (Phase 39): tests parse both the golden
and the ShadowDusk-produced `.fxb` with the same MojoShader-rule validator and compare
structure (parameters, techniques, passes, states, object wiring) — **never bytes**
(behavioral equivalence, not byte-equality, per `docs/the-purpose.md`).

## Provenance (2026-06-09)

Compiled on Windows 11 from the `.fx` sources in this directory:

```
fxc.exe /T fx_2_0 /Fo <name>.fxb <name>.fx     (Windows SDK 10.0.26100.0, x64)
```

`D3DCompile(pTarget: "fx_2_0")` from the system `d3dcompiler_47.dll` produces **byte-identical**
output for both effects (verified) — one canonical oracle, two invocation paths. Both emit
deprecation warning `X4717` ("Effects deprecated for D3DCompiler_47"); output is unaffected.

| File | Size | Contents |
|---|---|---|
| `minimal.fx` / `minimal.fxb` | 268 B | smallest valid effect: 0 parameters, 1 technique/1 pass, `PixelShader = compile ps_2_0` |
| `textured.fx` / `textured.fxb` | 544 B | `texture` + `sampler_state { Texture = <t>; MipFilter = LINEAR; }` + `tex2D` — exercises texture/sampler parameters, sampler-state records, and the small/large object sections |

These are **test oracles only** — fxc/d3dcompiler never ship in or drive the product pipeline
(same posture as the Phase 18 `d3dcompiler_47` DXBC oracle). Byte layout decoded against
MojoShader's parser in `docs/fx2-binary-format.md` (see the worked example and §16 citations).
