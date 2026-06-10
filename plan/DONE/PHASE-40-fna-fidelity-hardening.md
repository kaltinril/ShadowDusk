# Phase 40 — FNA fidelity hardening (post-Phase-39 evaluation findings)

**Status:** ✅ **DONE (2026-06-09, branch `feature/phase40-fna-fidelity-hardening`).**
**Track:** Fidelity / robustness — fixes every FNA-specific defect confirmed by the
five-lens evaluation of the whole Phase 39 body of work (security items deliberately
descoped by user direction; non-FNA items deferred — see *Deferred* below).

## Provenance

After Phase 39 + its VS-driven follow-up merged (PRs #30, #31), a five-agent evaluation
(QA, coder, security, shader-expert, cross-platform personas over the entire FNA surface)
plus a 15-agent adversarial diff review (12 confirmed / 0 refuted findings on PR #31)
produced a consolidated findings list. This phase implements the FNA-specific fixes.
The full agent reports lived in the session; everything load-bearing is recorded here
and in the code/tests it produced.

## What was fixed (finding → disposition)

| # | Finding (lens) | Fix |
|---|---|---|
| F1 | **Brace-form sampler blocks** (`sampler S { … };`, no `= sampler_state`) silently lost ALL states + the texture binding on the FNA path; `Texture = (X)` paren refs were a parse error. Live case: `ClipShaderNew.fx` — its `.fxb` contained **no `Mask` string at all** (544 B → 916 B after the fix). (shader-expert, HIGH) | `FxPreParser` recognizes the brace form (incl. `: register(s0)` between name and `{`) and paren texture refs in both block forms, both modes — same fxc semantics, shared parse path. Also fixed a latent bare-form misparse the register-clause ordering exposed. +9 parser tests; `ClipShaderNew.fx` promoted to a rung-4 GATE row and proven against the oracle. |
| D3/F2 | **FNA-honored render states silently dropped**: `SeparateAlphaBlendEnable`, `BlendFactor`, `MultiSampleMask`, `TwoSidedStencilMode`, `CCW_Stencil*`, `ColorWriteEnable1/2/3` were in the writer's honored allowlist but unreachable (parser/block never modeled them). FNA-*throwing* states (AlphaTest/fog/point-sprite/…, ~66 keys) were silently dropped while the fxc build throws at `EffectPass.Apply`. (coder + shader-expert) | `RenderStateBlock`/`RenderStateParser`/`Fx2EffectBuilder` model all 11 missing honored ops (deterministic emission order, existing value maps); known-FNA-throwing keys now fail **SD0303** naming the state. MGFX paths proven inert (fields not in the `Has*` gates; full-suite byte tests green). +33 tests. |
| D4/H3 | **Stage/profile cross-mismatch unchecked**: `VertexShader = compile ps_3_0 …` shipped a `.fxb` that breaks only inside the consumer's FNA. (coder) | `ResolveFnaProfile` rejects prefix/stage mismatch (SD0300); `Fx2EffectWriter` choke-point check that each blob's version-token kind (0xFFFE/0xFFFF) matches its Stage tag (SD0302). |
| F9 | **SM1 literal profiles** passed the policy but the patcher exempts SM1 streams under a false comment; vkd3d 1.17 ps_1_x has known gaps, never validated. (shader-expert) | Literal SM1 profiles now fail **SD0300** loudly (use ps_2_0+ — FNA's own guidance); patcher comment corrected. Policy/diagnostics now say "Shader Model 2–3". |
| D1 | **Patcher pass 2 threw raw exceptions** on malformed token streams (comment length lies, truncated def, zero-operand def duplicated a dword) — outside the `Result` contract. (coder + security) | Pass 2 (and a missed pass-1 site read) fully bounds-guarded; structural surprises return SD0305, never throw, never emit corrupt bytes. +3 byte-level tests. |
| D2/F8 | **Predicated texkill/texld silently skipped** though the SD0305 registry claims they fail. (coder + shader-expert) | Predicated would-be sites fail SD0305; predicated non-sites pass through. +2 tests. |
| F7 | **texld src0 *modifier* at SM3** escapes the patcher (MojoShader forbids modifiers at all SM2+; only sub-SM3 swizzles were patched) → load failure class. (shader-expert) | Site condition extended: modifiers route through the fresh-temp mov at every major; SM3 swizzle-only stays untouched. +1 token-exact test. |
| — | **Matrix parameter class** (found by the new golden): fxc's parameter table says `MATRIX_ROWS` (declaration-level D3DX convention) while we copied vkd3d's CTAB `MATRIX_COLUMNS` (register layout) through — behaviorally inert in FNA (no class-based transpose anywhere) but an `EffectParameter.ParameterClass` API divergence. | Builder maps matrix class → ROWS for the parameter table (CTAB in the blob untouched). Pinned by the new `matrix.fxb` golden cross-check; rung-4 matrix row delta 0. Residual: explicit `column_major` globals (indistinguishable in CTAB) also read ROWS — class metadata only. |
| QA-2/3 | **SD0210 / SD0300 guards untested or CI-invisible.** | SD0210 `ProfileOverride` refusal hoisted above the oracle's Windows guard (platform-independent policy → unit-testable everywhere; `D3DCompilerOracleTests`). The two SD0300 integration tests un-gated (plain `[Fact]` — they fail before any native). New pure `FnaProfilePolicyTests` (24 cases) runs in CI on every OS. |
| QA-5/8 | **No matrix golden; determinism only Grayscale.** | `matrix.fx`/`matrix.fxb` golden checked in (oracle-produced; provenance in the golden README) + wired into the structural cross-check; float4x4 writer round-trip test; FNA determinism extended to `FnaMultiPassStates` (multi-pass/multi-technique/VS+PS/states). |
| A1 | **Release pipeline would ship `ShadowDusk.HLSL` with zero vkd3d natives** (pack on a clean CI runner; csproj packing is `Exists(...)`-gated; only a count check existed). (cross-platform, HIGH) | `release.yml` `pack-desktop` now fails red unless the packed nupkg contains both vkd3d natives (mirror of the `pack-wasm` dxcompiler.wasm gate). **Deliberate consequence: a release stops at this gate until binaries are provisioned (Phase 37 C)** — documented in RELEASING.md. |
| — | Dead `Debug` plumbing on the FNA path; coder review style notes. | `EmbedDebugInfo = false` + the documented no-op rationale at the request site. |

### Harness discrimination upgrades (`validation/FnaValidation`)

- **Distinct deterministic mask texture** for second/mask-style textures (procedural,
  varies in every channel incl. alpha): feeding every slot the same cat is how the F1
  bug stayed invisible — a lost binding rendered pixel-identical to a correct one.
- **Parameter hit-set tracking**: SetParams returns the names it actually set per arm;
  asymmetries are printed (`ref-only`/`cand-only`) — exactly how a lost binding announces
  itself (benign for the documented optimized-out-globals case, but always surfaced).
- **Texture-slot reset in the Sprite scene too** (the VsQuad reset landed in Phase 39's
  hardening; slot-1 leak across arms could mask lost second-texture bindings).
- **Technique-by-name selector rows** (`FnaMultiPassStatesT2` renders `SinglePass`):
  FNA's technique-by-name lookup was otherwise never exercised at rung 4. The
  whole-binary scene↔VS guard is skipped for selector rows (documented).
- **Gate grew 14 → 17**: + `ClipShaderNew` (the F1 case), + `FnaMultiPassStatesT2`,
  + `matrix` (the calibration row, explicit non-symmetric exact-dyadic `M` upload).

## Evidence

- Full suite **851 passed, 0 failed, 0 skipped** (was 739 pre-Phase-40; +112 tests).
- Rung-3/4 gate **17/17 PASS** (13 PS-only + 4 VS-driven), max per-channel delta 0
  everywhere except Dots (1/255), real FNA 26.06 / D3D11 / RTX 3080; no parameter
  asymmetries; non-vacuousness guard green.
- `ClipShaderNew` end-to-end proof: pre-fix `.fxb` had no `Mask` binding at all;
  post-fix it embeds `Mask` + `MaskSampler` + all five states and renders
  pixel-identical to the fxc oracle with a visually distinct mask.

## Deferred (recorded, not lost)

- **Macro-profile honoring (F4)**: `compile PS_SHADERMODEL …` with
  `#define PS_SHADERMODEL ps_2_0` still compiles at the 3_0 ceiling — honoring it needs
  conditional evaluation in our preprocessor (ours only flattens includes; vkd3d's
  evaluates `#if`). Documented in Phase 39 Known limitations with the literal-profile
  workaround.
- **def-clamp narrowing (F3)**: kept, with the corrected justification + Known-limitations
  entry (candidate-only divergence for ≥2³² literals in real arithmetic, LP64-correct on
  the fxc arm); the upstream MojoShader printFloat fix remains the real cure.
- **Array-defaults propagation (D5)**: bake-as-zeros documented next to F2; propagation is
  safe in principle (dense rows, no majority ambiguity) but wants an fxc golden first.
- **Parameter semantics emission (F6)**: fxc carries `: SEMANTIC` typedefs; we emit none
  (CTAB has no semantics; would need pre-parser capture + plumb). API-level divergence
  only; revisit with a consumer need.
- **float4x3 skinning (F5)**: non-square stays SD0302-rejected until the F1 dims-order
  question is settled with a non-square golden (the new matrix golden settles the square
  case only). The biggest remaining FNA content-coverage unlock.
- **SD0304-on-WASM test**: `ShadowDusk.Wasm` is `net8.0-browser` (Razor SDK) — not
  referencable from the net8 test projects; needs the browser harness.
- **Non-FNA items** (user-descoped or tracked elsewhere): Node-24 actions bump
  (2026-06-16 deadline!), `.gitattributes`, linux glibc-baseline rebuild + artifact
  hosting (Phase 37 C), the non-FNA duplicate-render-state-key `ToDictionary` throw
  (coder D7), security findings (include containment, loader probe order, hash pinning —
  the last partially mitigated by the new pack gate).

## Related

`plan/DONE/PHASE-39-fna-fx2-output-target.md` (+ appendices, incl. the Appendix-G
editorial correction added this phase) · `docs/fx2-binary-format.md` ·
`tests/fixtures/golden/FNA/README.md` (matrix golden provenance) · RELEASING.md
(enforced vkd3d pack gate).
