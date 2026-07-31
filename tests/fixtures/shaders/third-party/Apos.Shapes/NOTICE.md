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

**Render-proven on DirectX (Phase 51 A3, added 2026-07-23).** `validation/VsDrivenDx -- apos`
renders the SAME `apos-shapes-sm6.fx` fixture — no separate DX variant was needed, since the
DirectX macro set (`{MGFX, HLSL, SM4}`) takes the fixture's `#else` branch (legacy `sampler`/
`tex2D` syntax, `vs_4_0`/`ps_4_0`), the same branch a DirectX_11-profile `mgfxc` compile takes —
through the same 13-element vertex layout and the same non-identity asymmetric
`view_projection`, and pixel-diffs BOTH ShadowDusk DXBC backends (the `d3dcompiler_47` oracle
and `vkd3d-shader`) against the checked-in `mgfxc` golden
(`tests/fixtures/golden/DirectX_11/apos-shapes-sm6.mgfx`): **maxd 0** on both backends, no
tolerance needed.

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

**A separate, real GL portability gap surfaced by this same render-proof, FIXED same day: issue
[#149](https://github.com/kaltinril/ShadowDusk/issues/149).**
ShadowDusk's GL candidate for `apos-shapes.fx` — this exact fixture — contained 28 `isnan(`
occurrences and no `#version` directive (the real mgfxc golden has zero of either). `isnan`
needs GLSL 1.30+; this repo's versionless GL output is otherwise correct for the legacy
MonoGame GL runtime, but it meant the desktop NVIDIA/AMD/Intel drivers this render-proof runs
on tolerated `isnan` leniently while Apple's strict GL compiler rejected it outright — real
breakage for Apos.Shapes 0.7.6 on macOS, reported independently by Apostolique. This never
invalidated the maxd 2/255 pixel-diff above (a real result on this repo's established GL
evidence ladder); it was a pre-existing GL-backend gap (any shader using `min`/`max`/`clamp`),
not something this render-proof could have caught on this hardware, and not specific to
Apos.Shapes. **Fixed** by defaulting SPIRV-Cross's `RELAX_NAN_CHECKS` compiler option on for
the whole OpenGL profile: `apos-shapes.fx` now emits zero `isnan(` (was 28), with zero byte
changes anywhere else in the corpus and no change to this render-proof's maxd 2/255 result.
Full writeup: `plan/DONE/ISSUE-149-gl-isnan-versionless-glsl.md`.

## Phase 55 — the full `ShapeBatch` shape-gallery render-proof (2026-07-23)

The render-proofs above each exercise exactly ONE hand-built shape (a circle) per backend,
through a bespoke, hand-reverse-engineered vertex struct (`AposVertex`/`Pack11`/`Unpair` in each
`validation/VsDriven*` project). Requested directly by the owner: expand to Apos.Shapes' full
public shape/feature surface, using the REAL `Apos.Shapes` NuGet package as both the drawing
harness and the golden.

**NuGet version pin confirmed identical to the vendored fixture.** Latest is **0.7.7**
(`api.nuget.org`), whose `.nuspec` pins upstream commit `a85a31ca4ccbdcb4a5cf2321ea039d5352e5edcd`.
Diffed against the already-vendored `apos-shapes-sm6.fx` (commit `ea38c6d8`) — **identical except
one comment line** (`"Vulkan compiles"` → `"Vulkan and DirectX 12 compile"`). No new fixture
needed; the existing vendored file IS the NuGet 0.7.7 shader, confirmed rather than assumed.

**The mechanism: `ShapeBatch`'s effect-injection constructor.** Reading upstream
`Source/ShapeBatch.cs`: `public ShapeBatch(GraphicsDevice graphicsDevice, Effect? effect = null)`.
`null` makes it call a private `LoadEmbeddedEffect`, which reads a raw per-profile `.mgfx`/`.knifx`
byte blob embedded as an assembly manifest resource (`Apos.Shapes.apos-shapes.{ogl,dx11,dx12,vk}.mgfx`
/ `.knifx`) and calls `new Effect(graphicsDevice, bytes)` — **no `.xnb`, no `ContentManager`
involved anywhere**, exactly the same call every ShadowDusk validation driver already makes with
its own compiled bytes. A non-null `Effect` skips the embedded-resource load and uses the supplied
effect for every draw. So one `ShapeBatch(gd)` is the golden (the package's own embedded effect)
and a second `ShapeBatch(gd, shadowDuskEffect)` is the candidate, both driven through the SAME
public API — `DrawCircle`/`FillCircle`/`BorderCircle`, `DrawRectangle` (+ `CornerRadii`),
`DrawLine`, `DrawPath` (joins/caps/dashes), `DrawHexagon`, `DrawTriangle`/
`DrawEquilateralTriangle`, `DrawEllipse`, `DrawArc`, `DrawRing`, and their `Fill*`/`Border*`
variants — laid out across a fixed 30-cell grid (`validation/SharedDx/AposGalleryRenderer.cs`,
one shared file compiling unchanged against every backend's MonoGame flavor).

**GL gets no golden for this gallery — a locked decision, not an oversight.** `ShapeBatch`'s
`VertexShape` layout (13 fixed elements, Oklab-packed colors, dithering, clip distances) is
emitted identically on every backend, so GL is stuck driving the SAME current shader revision
DX11/DX12/Vulkan use — there is no swapping in the older, GL-safe `apos-shapes.fx` (3fb73b8d) pin
the single-shape proof above uses, because that revision predates this vertex contract. And the
single-shape proof above already found, independently, that `mgfxc`'s own GL compile of THIS
revision renders solid black for every non-textured shape (the confirmed MojoShader `-0.0 >= 0.0`
bug) — nearly the entire gallery. So GL renders the gallery through ShadowDusk's compile ONLY
(`validation/VsDriven -- apos-gallery`, a new mode alongside the untouched existing `apos` mode)
and asserts every one of the 30 shapes produces visible (non-black, non-transparent) output — no
pixel-diff claimed or implied.

**Measured results (2026-07-23, corrected same day — see below):**

| Backend | Golden source | Result |
|---|---|---|
| Vulkan | `Apos.Shapes`' own embedded Vulkan effect | **maxd 0**, all 30 cells |
| DirectX_11 (`vkd3d-shader`) | `Apos.Shapes`' own embedded DX11 effect | **maxd 0**, all 30 cells |
| DirectX_11 (`d3dcompiler_47` oracle) | the real, locally-generated `mgfxc` golden (`tests/fixtures/golden/DirectX_11/apos-shapes-sm6.mgfx`) | **maxd 0**, all 30 cells |
| DirectX_12 | the real, locally-generated `mgfxc` golden (`tests/fixtures/golden/DirectX_12/apos-shapes-sm6.mgfx`) | **maxd 1** on 11 pixels of 402,984 — root-caused 2026-07-31 to the **pinned DXC build**, not a ShadowDusk defect (see below) |
| OpenGL | none (candidate-only) | 30/30 shapes visible; no pixel-diff |

**Correction (2026-07-23, same day): the original DX11 "maxd 1 on 14/30 cells" finding was a
methodology bug, not a ShadowDusk fidelity gap.** It compared the `d3dcompiler_47` oracle
candidate against `Apos.Shapes`' own embedded DX11 effect as the "golden." Disassembling that
embedded resource found its header literally reads `// Generated by vkd3d-shader 1.17` — it is
**not** an `mgfxc`/`fxc`/`d3dcompiler_47` artifact, it is a `vkd3d-shader` one. That means the
original comparison was `d3dcompiler_47` output vs. an independent compiler implementation
(`vkd3d-shader`), which will always show small numeric drift regardless of any compile flag —
exactly why an attempted fix (adding `ShaderFlags.OptimizationLevel3` to
`D3DCompilerShaderCompiler`, matching mgfxc's own release flag) was tried, measured to make
**zero** difference to the output, and reverted. It also explains why the DX11 vkd3d-shader
candidate matched this "golden" at maxd 0 for a reason unrelated to fidelity: comparing
`vkd3d-shader` output to another `vkd3d-shader` output. Re-run against the REAL, locally-generated
`mgfxc` golden (the same one Phase 51 A3's single-shape proof used) — the correct oracle
reference — the `d3dcompiler_47` candidate matches at **maxd 0 across the full 30-cell gallery**.
No ShadowDusk defect, no fix needed, no follow-up.

**DX12's maxd-1 finding is real, and as of 2026-07-31 it is ROOT-CAUSED to the pinned DXC build.**
It is confirmed against ITS real, locally-generated `mgfxc` golden, so unlike the DX11 case above
it is not a wrong-reference artifact — but it is not a ShadowDusk defect either. The two sides use
different DXC binaries, each named in its own blob's `!llvm.ident`: ShadowDusk's is
`dxcoob 1.7.2212.40 (e043f4a12)` (the `Vortice.Dxc` 3.3.4 pin), the golden's is
`dxcoob 1.8.2505.32 (b106a961d)` (MonoGame 3.8.5's bundled DXC). Feeding **ShadowDusk's own**
pre-parsed HLSL and **ShadowDusk's own** DXC flags to a DXC 1.8 build reproduces the golden's
pixel-shader DXIL instruction-for-instruction, and rendering only that swapped payload gives
**maxd 0, zero differing pixels**. The two builds emit identical DXIL intrinsic counts and differ
only in `fast`-math-licensed rewrites; it reaches a pixel at all only because this shader adds
±half an 8-bit LSB of dither immediately before quantization
(`result.rgb += (DitherNoise(p.Pos.xy) - 0.5) * dither_scale`), so a sub-ULP difference flips
whichever pixels sit on the rounding boundary. **maxd 1 is the honest tolerance while the pins
differ**; closing it means bumping DXC, which re-baselines every target.

**Two harness bugs found while root-causing it, both now fixed** (and both had misled the earlier
readings): the per-cell breakdown used the *untransformed* gallery rectangles under a 1.15x view,
so it named the wrong shape — this delta was recorded as `DrawCircle`/`FillArc` here and as
`FillRing` on a re-run, when the pixels are `DrawEllipse`'s; and the render target was sized to the
untransformed layout, which clipped 10 of the 30 cells off-screen so they were drawn but never
compared, while the GL visibility check still read 30/30.

**The hand-rolled `AposVertex`/`BuildCircleQuad`/`Pack11`/`Unpair` code was deleted** from the
DX11, DX12, and Vulkan `AposShapesRenderer.cs` files — `AposGalleryRenderer` replaces it entirely
for those three backends. GL's existing single-shape proof (this section, above) is untouched.

Full writeup: `plan/DONE/PHASE-55-apos-shapes-shape-gallery-render-proof.md`.
