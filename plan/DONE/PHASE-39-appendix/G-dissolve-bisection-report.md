# Appendix G — Dissolve render-divergence bisection (2026-06-09)

> After the texkill/texld load fixes, Dissolve loaded but rendered wrong (broad orange, no
> discards) while the fxc oracle rendered correctly. This records the full diagnostic path —
> including the dead ends, which are as instructive as the answer — and the proven fix.
> Probe sources live in `validation/FnaValidation/FnaProbe*.fx`; the scratch tools are
> `validation/FnaValidation/MojoHlslDump/` (P/Invokes `MOJOSHADER_parse` from the harness's
> own FNA3D.dll to print the translated HLSL) and `dump_d3d9_tokens.py` (D3D9 token
> disassembler).

## Root cause (one sentence)

vkd3d 1.17 lowers `discard` as `texkill` of a register loaded with the def constant
**`0xCF800000` = −2³² = −4294967296.0**; MojoShader's `MOJOSHADER_printFloat`
(`mojoshader_common.c:974–1056`) converts the magnitude through a **32-bit `unsigned long`**
(Windows is LLP64), which overflows for |f| ≥ 2³² and prints **`-0.0`** — so the translated
HLSL's kill test `if (any(rK.xyz < 0.0)) discard;` with −0.0 is **false** and discard never
fires. fxc's biggest def literals are ±1 (it kills via `cmp` selecting −0.0/−1.0), so the
oracle never trips this; on LP64 Linux (`unsigned long` = 64-bit) the conversion is exact,
which is why it never bit upstream. The shared `floatstr` means MojoShader's **GLSL and
Metal profiles have the same defect** (i.e. FNA's GL backend too) — upstreaming a fix is a
recorded follow-up; ShadowDusk's def-clamp works regardless.

## Diagnostic path (dead ends preserved deliberately)

1. **Harness param-set swallow** (`try { SetParams } catch { }`) — fixed to record the error
   instead: a swallowed `SetValue` failure renders an arm with default zeros and *fabricates*
   a divergence. No error surfaced → not the cause, but the swallow itself was a real
   harness bug worth keeping fixed.
2. **Preshader suspicion** — fxc hoists Dissolve's uniform-only math into a CPU preshader
   (its shader CTAB lists only `[_dissolveTexSampler, _progress, s0]`; ours lists all four
   uniforms; ref 884 B vs cand 568 B for the probe). `FnaPreshaderProbe.fx` (uniform-only
   `a+b` rendered as a color) → **maxd 0** — FNA executes fxc preshaders correctly.
   Eliminated.
3. **Sampler-register swap suspicion** — CTAB dump of both arms: **identical**
   (`s0`→s0, `_dissolveTexSampler`→s1). Eliminated.
4. **Hand-computed expected image** — with the harness params (progress 0.5, θ 0.04,
   dissolve tex = the cat itself), the faithful render is *plain cat + thin orange bands*:
   discarded bright pixels reveal the harness's priming draw (the same plain cat), so the
   correct image *looks* untouched. That flipped the verdict: the reference was RIGHT and
   the candidate's discard wasn't firing.
5. **Probe ladder** (each isolates one vkd3d idiom; all ran against the live 21-PASS
   baseline): `cmp`-comparison → PASS · bool-as-lerp-factor → PASS · `ifc x ne -x`
   negate-modifier branch → PASS · **clip-in-branch over a gradient → FAIL (maxd 255,
   420 206 px ≈ exactly the kill region)**. Methodological catch: the clip probe first
   passed *vacuously* (survivors returned the sampled cat — identical to the primed-cat
   pixels a discard leaves); fixed by returning the inverted color. **Spatially-uniform
   kill tests cannot discriminate** — SpriteAlphaTest/ClipShader "passing" proved nothing
   about texkill.
6. **Token + translation dump** — the candidate kill region decodes as
   `def c3 = (1, 0, −4.29497e9, −1)` → `cmp` → `ifc_ne r1.y, −r1.y` → `mov r1.y, c3.zzzz` →
   (patched) `mov r7, r1.yyyy` → `texkill r7.xyzw`; MojoShader's actual translated HLSL
   (via `MOJOSHADER_parse` on the exact FNA3D.dll the harness runs) shows
   `const float4 c3 = float4(1.0, 0.0, -0.0, -1.0);` — the sentinel destroyed at the
   printer, everything else correct (the earlier inference that the bool `b` also broke was
   wrong: "broad orange" is fully explained by kills not firing).
7. **Recipe proven before landing** — harness rows `DissolveClamped`/`FnaProbeClipClamped`
   applied the one-dword clamp (`CF800000 → CF7FFFFF`) to the candidate bytes and rendered
   **maxd 0** vs the unmodified oracle in real FNA.

## The fix (now `D3d9BytecodePatcher` fix #3)

In-place, size-preserving: for `def` (opcode 0x51) float payloads, any finite literal with
|f| ≥ 2³² (`mag ∈ [0x4F800000, 0x7F800000)`) is rewritten to the same-signed largest float
**below** 2³² (`±0x4F7FFFFF` = ±4294967040.0), which `MOJOSHADER_printFloat` round-trips
exactly. The sentinel's only observable property is its sign (it reaches `texkill` via
`mov`), which is preserved. `defi`/`defb` (integer payloads) untouched; CTAB untouched.
The broad |f| ≥ 2³² domain was chosen over clamping only the exact `0xCF800000` sentinel:
fxc-compiled effects with such literals are equally destroyed by MojoShader (both arms
identical ⇒ no fidelity gap), and the broad form is robust to vkd3d changing its sentinel.

> **[Editorial correction, Phase 40 (2026-06-09).]** The "both arms equally destroyed ⇒ no
> fidelity gap" justification above is wrong: the clamp patches the **candidate only**, so
> for a ≥2³² literal in real arithmetic (not the kill sentinel) the arms diverge — on LP64
> FNA (Linux/macOS, where `unsigned long` is 64-bit) fxc's arm renders the exact literal
> *correctly* while ours uses 2³²−256, and on Windows/LLP64 both arms are destroyed but
> *differently* (fxc's prints ~0.0, ours prints 2³²−256). The clamp is retained as the
> documented tradeoff (working `clip()`/`discard` everywhere vs a rare-idiom divergence) —
> see the phase doc's Known limitations; the upstream printFloat fix dissolves it.
