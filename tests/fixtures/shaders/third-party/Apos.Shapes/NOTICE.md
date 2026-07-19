# Third-party shader — Apos.Shapes

This directory vendors the real, shipping `.fx` shader from **Apos.Shapes**, the
SDF shape-rendering library that **Gum** (the UI tool) uses. It is used here as a
**compile-level regression input** for ShadowDusk's corpus (Phase 49, requested by
Victor Chelaru / vchelaru, Gum's author). It is NOT a render-equivalence proof —
see `docs/test-shader-corpus.md` for what it covers and on which targets.

Two revisions are vendored (both from the same upstream path,
`Source/Content/apos-shapes.fx`):

- **`apos-shapes.fx`** — the Phase 49 pin. Keeps the issue-#127 GL codegen
  fidelity tests (`pow`-square, reciprocal-of-quotient) anchored to the exact
  revision they were written against.
- **`apos-shapes-aa.fx`** — the later derivative-based-antialiasing revision
  (issue #136): `ddx`/`ddy` of the SDF and of an interpolated position drive the
  AA footprint. Pins that no gradient op lands inside a loop with a divergent
  exit in the emitted GLSL (ANGLE's D3D11 backend silently zeroes derivatives
  there — the issue-#136 poisoning).

## Upstream project

- **Project:** Apos.Shapes
- **Author / copyright:** Copyright (c) 2021 Jean-David Moisan (Apostolique)
- **Repository:** <https://github.com/Apostolique/Apos.Shapes>
- **License:** MIT (verbatim text in `./LICENSE`)
- **Commits fetched (pinned for reproducibility):**
  - `apos-shapes.fx`: `3fb73b8d0a51f86678269a4ad28391459cc771b1` (fetched 2026-06-27)
  - `apos-shapes-aa.fx`: `d507a73487335b6afceec4b2f518d167df28544a` (fetched 2026-07-19)
- **Upstream path:** `Source/Content/apos-shapes.fx`

Fetched with:

```sh
SHA=<commit above>
gh api "repos/Apostolique/Apos.Shapes/contents/Source/Content/apos-shapes.fx?ref=$SHA" \
  --jq '.content' | base64 -d > <file>
```

## Modifications

**The shader code itself is UNMODIFIED, byte-for-byte identical to upstream.**

The ONLY change is a provenance/attribution comment block **prepended** at the top
of the file (project, repo URL, commit SHA, upstream path, license, and a one-line
note of what it exercises). No shader statement, declaration, technique, profile, or
whitespace inside the original source was altered. The `LICENSE` file is the upstream
`LICENSE` fetched verbatim from the same commit.

## File, classification, and rationale

`Targets` = the delivery targets the shader compiles on through ShadowDusk
(OpenGL = MonoGame-GL / KNI, DirectX_11 = MonoGame-DX, FNA = D3D9 fx_2_0), classified
by an **actual compile probe** on 2026-06-27. A target NOT listed fails by a
legitimate shader-model limitation (noted), not a ShadowDusk parser defect.

| File | License | Targets (compile) | Why a target is excluded |
|---|---|---|---|
| `apos-shapes.fx` | MIT | **GL + DX** | **FNA:** the shader has no SM3 / FNA profile branch — its `#if OPENGL` arm selects `ps_3_0`, the `#else` selects `ps_4_0` (SM4), and the FNA target takes the SM4 arm. Combined with the dense pixel shader (the `EllipseSDF` Newton-iteration `for` loop, the 11-branch shape dispatch, the Oklab + ~11 gradient functions, 10 `TEXCOORD` interpolants), it exceeds the vkd3d `fx_2_0` / SM3 ceiling and is rejected (`X0000`). This is consistent with Apos.Shapes shipping for MonoGame OpenGL/DX, not FNA — a legitimate SM limit, not a ShadowDusk defect. |
| `apos-shapes-aa.fx` | MIT | **GL + DX** (probe 2026-07-19) | **FNA:** same as above, plus `ddx`/`ddy` gradient intrinsics (SM3+ only in fx_2_0 terms) and a third sampler (`BlueNoiseSampler : register(s2)`). |

**What it exercises (GL + DX):** a single large **VS + PS** effect with a 10-interpolant
I/O struct (`TEXCOORD0..9` + `SV_Position`); a `float4x4 view_projection` applied with
`mul()`; **two samplers**, one with explicit `register(s0)`; a `__KNIFX__` / `OPENGL`
macro target-branch; a runtime-bounded Newton-iteration `for` loop; `int` locals;
relational ternaries; an 11-way `if/else if` shape dispatch; float `%` modulo; `frac`,
`atan2`, `pow`, `clamp`, `smoothstep`, `normalize`; `discard`; `tex2D`; a `float2x2`
`mul`; and Oklab colour conversion. **Apos.Shapes compiling on GL + DX is the headline
result of Phase 49** — it is the shader Gum's shape rendering actually depends on.

## How it is exercised

`tests/ShadowDusk.Integration.Tests/ThirdPartyShaderCorpusTests.cs` compile-asserts it
on **OpenGL and DirectX_11** (well-formed `MGFX` container). The
`Phase41StructuralDivergenceMatrixTests` GL+DX census auto-globs this directory, so the
file also gets a GL+DX structural-census cell (its FNA exclusion is documented above,
not asserted as a passing cell).

**Scope:** a green compile to a well-formed container is the bar here — NOT a
pixel-equivalence claim to `mgfxc`/`fxc`. There is no committed golden for this shader
(yet); a rung-4 render-proof against the `mgfxc` oracle is a documented Phase 49 stretch
(it needs a bespoke vertex-buffer render driver because Apos.Shapes packs its shape
parameters into the vertex `TEXCOORD` attributes).
