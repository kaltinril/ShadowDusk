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

## `apos-shapes-sm6.fx` — the issue #145 reproducer (added 2026-07-22)

A THIRD revision of the same upstream file, vendored alongside the other two rather than
replacing them, because each pins a distinct regression shape:

| File | Upstream commit | What it pins |
|---|---|---|
| `apos-shapes.fx` | `3fb73b8d` | the LEGACY `sampler` / `tex2D` revision — the shape that access-violated on Vulkan (issue #145 bug 2) |
| `apos-shapes-aa.fx` | `d507a734` | the derivative-AA revision — the ANGLE-D3D11 gradient pin (issue #136) |
| `apos-shapes-sm6.fx` | `ea38c6d8` | the CURRENT upstream revision with the `#elif SM6` branch — the exact shader from issue #145 |

`apos-shapes-sm6.fx` is what the reporter compiled: `vs_6_0`/`ps_6_0`, three
`Texture2D`/`SamplerState` pairs at explicit registers, wrapper functions around `.Sample`, a
13-element vertex input, a `float4x4 view_projection`, the `FixSnorm` workaround for MonoGame's
SSCALED vertex-format bug, plus `ddx`/`ddy` footprints, Newton loops, dash SDFs and Oklab
gradients.

**Compile status** (measured 2026-07-22): OpenGL OK, DirectX_11 OK, Vulkan OK. FNA fails with
`E5017` — the dense pixel shader exceeds what vkd3d can lower to `fx_2_0`/SM3, the same honest
shader-model ceiling the other two Apos revisions hit (Apos.Shapes ships for MonoGame GL/DX/VK,
not FNA).

**Render-proven on Vulkan.** `validation/VsDrivenVulkan -- apos` renders it on a real MonoGame
3.8.5 DesktopVK device through its own 13-element vertex layout, with a non-identity asymmetric
`view_projection`, and pixel-diffs ShadowDusk against the checked-in `mgfxc 3.8.5` golden
(`tests/fixtures/golden/Vulkan/apos-shapes-sm6.mgfx`): **maxd 0**, with a non-vacuity check that
rejects the blank frame the issue reported. Restoring the `-Zpr` bug turns it red at maxd 255.

**GL: compiles, but its real mgfxc golden renders wrong (Phase 51 A3, discovered 2026-07-23).**
`apos-shapes-sm6.fx` compiles cleanly on OpenGL (confirmed above), and a `validation/VsDriven --
apos` attempt against it was built first, matching the DX/Vulkan pattern. It diverged completely
(maxd 255, solid black) against the real `mgfxc /Profile:OpenGL` golden. Reverse-engineering the
golden's embedded GLSL (MojoShader's ps_3_0 translation) found the final pixel-shader output is
`ps_oC0 = ((-ps_r0.w >= 0.0) ? ps_r1 : ps_r4)`, where `ps_r4` is hardcoded to zero for every
non-texture shape (which is every shape this render-proof exercises) and `ps_r0.w` is set from
the same hardcoded `0.0` in that case — so the branch condition is `-0.0 >= 0.0`, which IEEE-754
defines as true (`-0.0 == 0.0`) but which this GPU/driver's evaluation of MojoShader's translated
comparison resolves false, permanently selecting the hard-zeroed branch. An independent
double-precision recomputation of the shader's OkLab conversion (bypassing the shader entirely)
confirmed ShadowDusk's GL candidate is the mathematically correct one. This is a genuine bug in
mgfxc's own GL/MojoShader compile of this specific fxc-optimized revision — the same class of
"the reference compiler's own output is wrong" situation the Vulkan `SlotOffset` bug already is —
not a ShadowDusk defect. It is specific to `apos-shapes-sm6.fx`'s fxc-SM3-optimized shape
dispatch; it is not exercised by, and does not indicate a gap in, ShadowDusk's own GL codegen.

**Render-proven on GL via the older `apos-shapes.fx` pin instead.** That revision's plain
sequential `if/else if` shape dispatch and Cantor-pair color packing (`Unpair`, not
`apos-shapes-sm6.fx`'s `Pack11`/`DecodeDigit`) sidestep the codegen quirk above. `validation/
VsDriven -- apos` renders it on a real MonoGame DesktopGL device through its own 10-element
vertex layout (`POSITION0, TEXCOORD0-8` — this earlier revision predates the sm6 fixture's
clip-distance split), with the same non-identity asymmetric transform, and pixel-diffs ShadowDusk
against the checked-in `mgfxc` golden (`tests/fixtures/golden/OpenGL/apos-shapes.mgfx`): **maxd
2/255** (216/16384 pixels, 1-2/255 drift) — the documented transcendental-math GLSL-dialect
precision drift between SPIRV-Cross and MojoShader on the shader's `RgbToOklab`/`OkLabToRgb`
round-trip (cube roots + fractional `pow()`), not a structural mismatch. GL, DX, and Vulkan are
all now render-proven for Apos.Shapes; FNA stays permanently excluded (see the compile-status
paragraph above).

**A separate, real GL portability gap surfaced by this same render-proof: issue [#149](https://github.com/kaltinril/ShadowDusk/issues/149).**
ShadowDusk's GL candidate for `apos-shapes.fx` — this exact fixture — contains 28 `isnan(`
occurrences and no `#version` directive (the real mgfxc golden has zero of either). `isnan`
needs GLSL 1.30+; this repo's versionless GL output is otherwise correct for the legacy
MonoGame GL runtime, but it means the desktop NVIDIA/AMD/Intel drivers this render-proof runs
on tolerate `isnan` leniently while Apple's strict GL compiler rejects it outright — real
breakage for Apos.Shapes 0.7.6 on macOS, reported independently by Apostolique. This does not
invalidate the maxd 2/255 pixel-diff above (a real result on this repo's established GL
evidence ladder); it is a pre-existing GL-backend gap (any shader using `min`/`max`/`clamp`),
not something this render-proof could have caught on this hardware, and not specific to
Apos.Shapes. Tracked as Phase 51 A7, not part of this render-proof's scope.
