# Phase 39 appendices — full evidence record

Companion record for [`../PHASE-39-fna-fx2-output-target.md`](../PHASE-39-fna-fx2-output-target.md):
the complete agent reports and session knowledge behind the phase's conclusions, preserved so
nothing load-bearing lives only in a chat transcript.

| Appendix | Contents |
|---|---|
| [A — doc-vs-code review](A-doc-vs-code-review.md) | Pre-implementation audit of the research doc against the codebase: claim corrections, the full seam-by-seam integration map (FxPreParser/vkd3d/pipeline/writer/CLI/tests/packaging), corpus classification (FNA-targetable vs TECHNIQUE-macro-blocked vs SM4-only) |
| [B — product-purpose review](B-product-purpose-review.md) | Constraint-by-constraint audit vs THE PURPOSE: the FNA bar + evidence ladder definition, naming/API rationale (`PlatformTarget.Fna`, writer in Core, no new package), risk register, the packaging-blocker finding (later closed), wording/doc constraints |
| [C — empirical vkd3d gate](C-empirical-vkd3d-gate.md) | The 1.17 feasibility proof: D3D9-HLSL→D3D_BYTECODE probes with CTAB dumps, the 136-row corpus sweep (105/107), sampler_state/technique tolerance, the fxc + `D3DCompile("fx_2_0")` oracle discovery (byte-identical), annotated golden hex walk |
| [D — fx_2_0 spec extraction notes](D-fx2-spec-extraction-notes.md) | Provenance + trickiest-rules summary for [`docs/fx2-binary-format.md`](../../../docs/fx2-binary-format.md) (the spec itself); the F1–F6 golden-flagged ambiguities |
| [E — adversarial review](E-adversarial-review.md) | All four reviewers' verdicts + every finding verbatim (spec-vs-writer incl. the INT/BOOL-defaults major, regression, security, completeness) — fixed or recorded before the first commit |
| [F — FNA rung-3/4 harness report](F-fna-rung34-harness-report.md) | `validation/FnaValidation` provenance (FNA 26.06, MojoShader `abdc8036`, fnalibs-dailies), design decisions (FNALoggerEXT hook, parity checker, tolerance), and the pre-fix first run preserving the texkill/texld discovery record |
| [G — Dissolve bisection](G-dissolve-bisection-report.md) | The MojoShader `printFloat` LLP64 bug: full diagnostic path (dead ends preserved — preshader/registers/param-swallow), probe-ladder method, token + translated-HLSL evidence, the proven def-clamp recipe |
| [H — session record](H-session-record.md) | The orchestrating session's own decisions/rationale, cross-phase observations (incl. the DX-harness Dissolve binding question and the Actions Node-20 deprecation), WSL-build + NuGet-testing gotchas, patcher engineering notes, the SD03xx error-code registry |
