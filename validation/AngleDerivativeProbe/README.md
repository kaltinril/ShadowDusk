# ANGLE D3D11 derivative probe (issue #136)

Verifies, on a real Windows browser's ANGLE Direct3D11 backend (what WebGL uses in
every Chromium/Edge/Firefox on Windows), that ShadowDusk's fragment-shader
control-flow shapes keep gradient ops (`dFdx`/`dFdy`/`fwidth`) alive:

- **A (control)**: derivative at top level of `main`. Expect `red=255`.
- **B (pre-fix shape)**: the old Rule-9 one-shot `for` loop with a conditional
  `break` and the derivative inside. On ANGLE D3D11 any loop with a divergent
  exit silently zeroes gradients. Expect `red=0` (the issue-#136 bug).
- **C (post-fix shape)**: the Rule-9a unwrapped form (plain blocks + conditional
  early returns, derivative in straight-line code), mirroring the actual emitted
  GLSL of `tests/fixtures/shaders/examples/Issue136HelperGradient.fx`. Expect
  `red=255` (the fix).

The fragment bodies mirror the emitted GLSL structures verbatim, hand-converted
to ES 3.00 the way KNI's HiDef runtime converter does. A `RENDERER:` line is
printed; the probe is only meaningful when it names `Direct3D11` (headless
browsers fall back to SwiftShader without the `--use-angle=d3d11` flag).

## Run (Windows)

```powershell
& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" `
  --headless=new --use-angle=d3d11 --no-sandbox --disable-gpu-sandbox `
  --virtual-time-budget=8000 --timeout=15000 `
  --dump-dom "file:///c:/git/ShadowDusk/validation/AngleDerivativeProbe/probe.html" |
  Select-String "RENDERER|A-control|B-prefix|C-postfix"
```

Green run (2026-07-19, RTX 3080): `A red=255, B red=0, C red=255` under
`ANGLE (... Direct3D11 vs_5_0 ps_5_0, D3D11)`.

This is a SHAPE-level proof: it renders the emitted control-flow structures, not
a full `.mgfx` through KNI-in-browser. The full in-engine ANGLE gate (loading
ShadowDusk's actual container in a Windows browser via the KNI Blazor harness)
remains the tracked follow-up in `docs/validation-matrix.md` section 7.
