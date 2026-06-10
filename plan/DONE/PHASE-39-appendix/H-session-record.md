# Appendix H — Session record: decisions, observations, gotchas (2026-06-09)

> Working knowledge from the implementation session that lives in no other artifact —
> design rationale, cross-phase observations, and operational gotchas future work will
> want. Appendices A–G hold the agent reports; this is the orchestrating session's own
> record.

## Design decisions and their reasoning (where the code comment alone doesn't carry it)

- **Profile policy is a shape test, not a known-profiles lookup.** The pre-parser runs
  before macro expansion, so `compile PS_SHADERMODEL …` arrives as the literal token
  `ps_shadermodel`. Classification: token matches `^(vs|ps)_[0-9]` → literal profile (major
  ≤ 3 honored as written for fxc fidelity; major ≥ 4 → `SD0300`, which also catches
  `ps_4_0_level_9_1`-style tokens *outside* the KnownProfiles list — the silent-downgrade
  hole the adversarial review found); anything else is a macro name → default
  `vs_3_0`/`ps_3_0` (the SM3 ceiling; per-stage consistent). Odd-but-shaped tokens
  (`ps_3_9`) pass through for vkd3d to reject loudly.
- **FNA macro set `FNA;HLSL;SM3`** — deliberately NOT `MGFX` (output isn't MGFX) and NOT
  `SM4/SM6/OPENGL/VULKAN` (MonoGame-template sources must fall through to their DX9/SM2
  branch). Note the macros never influence profile *selection* (techniques are stripped
  before preprocessing); they only gate body code.
- **PreserveSm3 strips only techniques + annotations** because the empirical gate proved
  vkd3d's HLSL frontend natively accepts `sampler_state { … }` initializers (both `<t>` and
  `(t)` forms), `texture` declarations, `tex2D`, `COLOR` semantics — and even whole
  technique blocks. We strip techniques anyway (metadata needed) and annotations (vkd3d
  acceptance unverified; metadata captured).
- **In-pass render states are emitted, not dropped**: everything `RenderStateBlock` models
  maps inside FNA's honored († ) state set, so emission is both safe and fxc-faithful. The
  MonoGame-ordinal → D3D9-value maps (Blend/BlendOp/Cmp/StencilOp/FillMode differ;
  CullMode/ColorWriteChannels coincide) are pinned by 24 exact-value unit facts.
- **CTAB is the FNA reflection source** because it is literally what MojoShader binds
  against at load — making reflection and runtime binding definitionally consistent. CTAB
  type-info stores Rows@+4/Columns@+6 (documented D3D order), the *opposite* of the fx_2_0
  typedef's dword5=columns/dword6=rows quirk — never conflate the two.
- **Defaults**: only scalar/vector single-row CTAB defaults propagate (matrix majority is
  the unresolved F2 ambiguity — wrong-major bakes corrupt silently; zeros match the MGFX
  writer's behavior). Value-blob dword encoding is type-aware (float bits / raw int / 0-or-1
  bool) because vkd3d's CTAB stores int/bool defaults as a float register image — copying
  bits through bakes `7.0f` where FNA reads back `1088421888` (adversarial-review major).
- **Texture params emit undimensioned TEXTURE(5)** (F3, cosmetic — fxc may dimension);
  sampler params take their type (SAMPLER/SAMPLER2D/…) from CTAB. Parameter order is
  numerics → textures → samplers because FNA builds the sampler→texture map scanning only
  parameters already converted.
- **`Fx2EffectWriter` lives in ShadowDusk.Core beside `MgfxWriter`** (dependency-free root;
  writer precedent) — and there is deliberately NO new `ShadowDusk.Fna` package: the release
  machinery is hardwired to the six-package set.
- **The FNA path always uses vkd3d on every host** — `DxbcBackend` is ignored by design and
  the d3dcompiler_47 oracle now *refuses* `ProfileOverride` (`SD0210`) so output can never
  silently depend on which backend a host picked (host-independence / cross-host
  byte-identity).
- **Validator independence method**: `Fx2BinaryValidator` was written by an agent
  instructed never to open `Fx2EffectWriter.cs`, deriving purely from
  `docs/fx2-binary-format.md` + the fxc golden hex dumps, then calibrated against the real
  fxc binaries. The writer passed the independently-derived validator on first contact —
  the strongest spec-conformance signal available short of the real runtime (which rung 3/4
  later supplied).
- **GL/DX byte-identity proof method**: a scratch `git worktree` of `main`, same fixtures
  through both CLIs (`Grayscale/textured/cbuffer/VertexAndPixel × OpenGL/DirectX_11`),
  SHA256 compare — 8/8 identical. Cheaper and stronger than reasoning from the diff.
- **vkd3d stays pinned at 1.17 — no bump.** The Phase-39 research doc's "needs 1.18" worry
  dissolved empirically (105/107 corpus entry-point compiles; the 1.18-landed ops are
  ps_1_x-era). Not bumping also keeps the validated DirectX SM5 vkd3d output byte-stable
  (a bump would have required re-baselining DX goldens + re-running Phase 18 validation).
- **`FnaFactAttribute`/`FnaTheoryAttribute`** gate the FNA integration tests on
  vkd3d-binary *availability* (file probe mirroring `Vkd3dLoader`), unlike the existing
  OS-gated `Vkd3dFactAttribute` — so a future restored Linux/macOS binary enables them
  automatically, and today's vkd3d-less CI skips instead of failing.

## Cross-phase observations (for whoever touches the neighboring areas next)

- **The DX validation harness may never have exercised Dissolve's dissolve path.** Running
  `validation/BaselineDx` today (mgfxc goldens, real MonoGame WindowsDX, the standard
  harness params) renders Dissolve as a *plain unmodified cat* — consistent with
  `e.Parameters["_dissolveTex"]?.SetValue(cat)` silently no-oping (the `?.`) or the slot-1
  binding not taking. Phase 17/18's comparisons stayed valid (both arms equal either way),
  but the spatially-varying clip path was likely untested there — echoing Phase 24's
  "sample-side unset slot-1 sampler state" find. The FNA harness's hand-computed-expected
  check (Appendix G §4) is the antidote pattern.
- Spatially-uniform kill tests are vacuous: a discard-everything or discard-nothing shader
  renders identically to its no-op twin over a primed background. Any future clip/discard
  validation needs a spatially-varying kill region AND a survivor color distinct from the
  primed image (Appendix G's methodological catch).
- `tests/ShadowDusk.ImageTests` `MgfxcCrossValidationTests` still skips Dissolve ("gap #3")
  — adjacent, pre-existing.
- **GitHub Actions Node 20 deprecation warnings** now annotate every CI run
  (`actions/{cache,checkout,setup-dotnet,upload-artifact}@v4`; Node 24 forced from
  2026-06-16, Node 20 removed 2026-09-16) — ci.yml/wasm.yml/release.yml action versions
  will need a bump pass.
- `ci.yml`'s Integration job runs on **main pushes only** — PRs run just the 3-OS
  Build & Test matrix (why PR #30 was green while main's Integration redness — Phase 37 —
  persists independently).

## Build & packaging gotchas (operational; the restore scripts have the recipes)

- **WSL vkd3d build**: run as `wsl -u root` (default user's sudo wants a password);
  configure hard-fails without `libjson-perl`; `make libvkd3d-shader.la` misses a generated
  header — run `make include/private/vkd3d_version.h` first; libtool outputs land in
  `.libs/` at the build ROOT (not `libs/.libs/`); the built so's internal version is
  `.so.1.15.0` (vkd3d's library versioning ≠ the 1.17 release number) — harvest by copying
  `.libs/libvkd3d-shader.so.1.15.0` → `tools/vkd3d/libvkd3d-shader.so.1`. glibc baseline =
  the build distro's (Ubuntu 24.04 here); release artifacts should build on the oldest
  supported distro.
- **macOS packing entries** expect per-arch subdirs (`tools/vkd3d/osx-x64/` and
  `osx-arm64/`, same `libvkd3d-shader.1.dylib` filename in each) — unlike win/linux whose
  distinct filenames sit flat in `tools/vkd3d/`.
- **NuGet consumer-testing traps** (hit while proving self-containment): a user-level
  `NuGet.Config` `packageSourceMapping` with `*`→nuget.org silently excludes a local folder
  feed — symptom is `NU1102 … Versions from local were not considered`; the project-local
  `nuget.config` needs its own more-specific `ShadowDusk.*` mapping. `%TEMP%`-style env
  syntax in `packageSources` paths did not expand — use absolute paths. And because
  `0.2.0` exists on nuget.org, local packs must use a unique version (`-p:Version=…-fnaN`)
  or the public package silently wins the restore.
- **`Vkd3dLoader` probe order matters**: base dir → `tools/vkd3d` walk-up →
  `NATIVE_DLL_SEARCH_DIRECTORIES` (the deps.json dirs — the ONLY route that finds
  package-cache natives, since our file names defeat default bare-name probing on every OS)
  → bare name (single-file publish).
- **Cross-host byte-identity receipt**: the 688-byte consumer `.fxb` hashed
  `9DCFEF04A695A7B607CFB43DFD8E1CB3B794EEE04DD1F8B2697A4D7CC8BFC509` on both Windows and
  Ubuntu (framework-dependent, package-cache natives, no repo access).

## D3d9BytecodePatcher engineering notes (beyond the code comments)

- Token-walk safety set: `def`/`defi`/`defb` payloads are skipped during operand scanning
  (raw literals bit-pattern like parameter tokens — a `-1.0f` looks like a temp-register
  dest); `dcl`'s first token is a usage descriptor, not a parameter; predicated
  instructions and relative-addressing operands bail loudly (`SD0305`) rather than rewrite
  wrong; CTAB lives in leading comment blocks, which also bounds the comment scan (a `def`
  float after the first real instruction could fake a comment token).
- Fresh-temp budget: one temp serves all sites (each inserted `mov` fully defines it
  immediately before its single use); limits 12 (SM2) / 32 (SM3); exhaustion = `SD0305`.
- Blind texkill mask-widening is UNSAFE (untested lanes hold garbage) — hence the
  replicated-component `mov`; the same routing incidentally fixes texkill-on-input-register
  (another documented MojoShader strictness).

## Error-code registry added by this phase

`SD0300` SM4+ profile under Fna · `SD0301` CTAB missing/corrupt · `SD0302` fx_2_0 writer
validation · `SD0303` FNA effect build (struct globals, sampler arrays, FNA-throwing or
unknown sampler states) · `SD0304` Fna on the WASM host · `SD0305` bytecode patcher cannot
canonicalize (no free temp / predicated / relative addressing) · `SD0210` (extended)
d3dcompiler oracle refusing `ProfileOverride`.
