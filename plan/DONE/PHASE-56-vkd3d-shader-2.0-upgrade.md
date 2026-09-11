# Phase 56 — vkd3d-shader 1.17 → 2.1 upgrade

**Track:** Native toolchain currency (post-1.0, output-affecting).

**Status:** ✅ **Done (2026-09-10)** — landed as 1.17 → **2.1**, not the researched 2.0.
[Issue #212](https://github.com/kaltinril/ShadowDusk/issues/212) (an FNA shader with a loop vkd3d
could not unroll) set the schedule; since the churn is paid once either way, 2.1 was taken over 2.0
for its SM1-3 dead-store-elimination pass. **§6 records the measured outcome**; §1-§5 are the
original plan, kept as written so the research and the result can be compared.

**Depends on:** nothing. The research below is done; the work is buildable today.

**Blocks:** [Phase 51](../PHASE-51-consolidated-remainder-backlog.md) A8 item 2 closes when this phase
either lands or is explicitly declined.

> **The headline, so nobody re-derives it:** the scary part of "1.17 → **2.0**" is not real. It is a
> project-version bump, **not an ABI break**, and our loader and interop need **zero changes**. The
> real cost is that **every DirectX `.mgfx` and FNA `.fxb` byte moves**, so this is a full goldens +
> byte-identity-manifest regeneration and a full render-gate re-proof. There is genuine upside too.

---

## 1. Compatibility research (done 2026-07-28 — do not repeat this)

### 1.1 The soname is unchanged — `-version-info` proves it

vkd3d builds `libvkd3d-shader` with libtool `-version-info current:revision:age`, where the soname
major is `current - age`:

| Release | `-version-info` (Makefile.am) | soname major |
|---|---|---|
| **vkd3d-1.17** (our pin) | `16:0:15` | **1** |
| **vkd3d-2.0** | `19:0:18` | **1** |

So the on-disk names we probe stay correct and
[`Vkd3dLoader`](../../src/ShadowDusk.HLSL/Vkd3d/Vkd3dLoader.cs) needs **no change**:
`libvkd3d-shader-1.dll` (Windows) · `libvkd3d-shader.so.1` (Linux) · `libvkd3d-shader.1.dylib`
(macOS). `age` rising in lockstep with `current` is libtool's "interfaces were **added**, none
removed or changed" signal — i.e. backward-compatible ABI.

### 1.2 Our call surface is untouched, and the new strictness is opt-in

[`Vkd3dNative`](../../src/ShadowDusk.HLSL/Vkd3d/Vkd3dNative.cs) uses exactly one entry point,
`vkd3d_shader_compile()`, with a `vkd3d_shader_compile_info` chained to a
`vkd3d_shader_hlsl_source_info`, and critically **`Options = NULL, OptionCount = 0`**. Everything
2.0 adds is opt-in through machinery we do not touch:

- `VKD3D_SHADER_COMPILE_OPTION_DENORMAL_MODE_F16/F32/F64` and
  `VKD3D_SHADER_COMPILE_OPTION_CONST_GLOBAL_UNIFORMS` are **compile options we do not pass**.
- The stricter behaviour ("when targeting `VKD3D_SHADER_API_2_0`, compilation will fail when a
  required floating-point denormal mode can't be specified in the target shader") only applies if
  the caller **declares** that API version. We never set an API-version option, and upstream
  explicitly preserves old behaviour for older declared versions ("never try to emit denormal modes
  for API version <= 1.19").

**Conclusion: no API break, no ABI break, no loader change, no interop change.**

### 1.3 What *will* change: the emitted bytes

This is the whole cost of the phase. 1.18 → 2.0 landed substantial HLSL codegen work:

- **A common-subexpression-elimination pass** (2.0).
- **A better register allocator plus an output-write-hoisting pass** (2.0) — upstream calls this out
  as *"particularly relevant for shader model 1-3 target profiles, where the number of temporary
  registers is relatively limited, and we may otherwise not be able to compile some shaders."*
  **SM1-3 is exactly our FNA target.**
- **Flattening of branched code** into conditional moves (1.18), including where SM2.0-or-earlier
  makes it mandatory.
- **More constant folding** (1.18/2.0): `asfloat`/`asint`/`asuint`/`cos`/`mad`/`round`/`sin`,
  `true ? x : y` → `x`, floating-point modulo.

Any one of these changes instruction selection, so assume **100% of DirectX `.mgfx` and FNA `.fxb`
outputs differ**. They should still be *correct* — but "different bytes" means every committed
golden, the cross-host byte-identity manifest, and every render proof has to be re-established.

### 1.4 The upside — this is not currency for its own sake

- **The register-allocator work may lift real, currently-documented rejections.** `SD0305` register
  pressure is why `BasicEffect` and `SkinnedEffect` do not compile on FNA today
  ([Phase 49](PHASE-49-apos-shapes-regression-corpus.md)). Upstream's own framing ("we may
  otherwise not be able to compile some shaders") is precisely this failure class. **Check this
  first — it may be the strongest reason to do the phase at all.**
- **Initial support for loops in shader model 2-3 target profiles** (2.0) — widens FNA coverage.
- **`tex3Dbias()`, `tex3Dlod()`, `texCUBElod()`** intrinsics (2.0).
- **`SV_ClipDistance` / `SV_CullDistance`**, and `SV_StencilRef` as a PS output (2.0).
- `BACKCOMPAT_MAP_SEMANTIC_NAMES` now also maps SM3 `VFACE`/`VPOS` to their SM4+ equivalents (2.0).
- Corrected `InterlockedMin`/`InterlockedMax` signedness handling; locale-independent float literal
  parsing (2.0) — the latter is a latent correctness fix for any host with a non-`C` locale.

---

## 2. Scope & Non-Goals

**In scope:**
- Build vkd3d **2.0** for all four desktop RIDs (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`)
  **and** the emscripten/WASM artifact, host them on a new pinned release tag, and update
  `tools/restore.{ps1,sh}` with new SHA-256 pins.
- Regenerate the DirectX and FNA goldens and the cross-host byte-identity manifest.
- Re-prove the full render ladder against the new bytes.
- Measure and record the upside in §1.4 — especially whether `SD0305` FNA rejections clear.

**Out of scope / Non-Goals:**
- **Declaring `VKD3D_SHADER_API_2_0`.** Keep the current implicit/older API version so we opt into
  the codegen improvements without the new failure modes. Changing the declared API version is a
  separate, later decision.
- Adopting any new compile option (denormal modes, `CONST_GLOBAL_UNIFORMS`). Additive, opt-in, and
  not needed to get the codegen wins.
- The OpenGL, Vulkan, and DirectX 12 targets — those go through DXC + SPIRV-Cross, not vkd3d, and
  must come out **byte-identical**. Any change there is a red flag, not an expected outcome.
- Changing the MonoGame pin, the default MGFX version, or anything consumer-facing.

---

## 3. Tasks

- [x] **A1 — Upside probe FIRST, before any packaging work.** Build vkd3d 2.0 locally for `win-x64`
      only, point a scratch build at it, and compile the known `SD0305` casualties (`BasicEffect`,
      `SkinnedEffect`) plus the FNA corpus. **Record whether the register-allocator work actually
      clears those rejections.** This is the decision gate: if it does, the phase has a concrete
      product win and is worth the churn; if it does not, the phase is pure currency and can be
      deferred again with that recorded.
- [x] **A2** — Measure the blast radius: compile the full DX + FNA corpus with 1.17 and with 2.0 into
      scratch dirs and diff. Record how many artifacts change and by how much (the expectation is
      "all of them"; confirm rather than assume).
- [x] **B1** — Build vkd3d 2.0 for all four desktop RIDs using the existing recipe in
      `tools/restore.*` / the vkd3d build workflow; host on a new `native-vkd3d-2.0` release tag.
- [x] **B2** — Rebuild the **WASM** artifact via `vkd3d-wasm-build.yml` (emscripten) against 2.0 and
      host on `native-vkd3d-wasm-2.0`. **Confirm it still builds with zero source patches** — that
      was a Phase 4.1 finding for 1.17 and must be re-verified, not assumed.
- [x] **B3** — Update `tools/restore.ps1` / `restore.sh` pins + SHA-256 for all five artifacts, and
      the release-gate "natives present" checks.
- [x] **C1** — Regenerate the DirectX_11 and FNA goldens with the reference compilers (goldens are
      `mgfxc`/`fxc` output, so they do **not** move — verify that explicitly; what moves is
      *ShadowDusk's* output, which must still match them).
- [x] **C2** — Regenerate the cross-host byte-identity manifest and re-prove macOS/Linux bytes equal
      Windows bytes on the new native.
- [x] **D1** — Full render ladder: `dotnet test ShadowDusk.slnx` plus
      `./validation/run-windows-render-gates.ps1 -IncludeFna` (the FNA gate is **mandatory** here —
      SM1-3 is where the codegen changed most). Plus the node + real-browser vkd3d gates
      (`node-test-vkd3d-wasm.mjs`, `browser-vkd3d-gate.mjs`) for the WASM artifact.
- [x] **D2** — Confirm **OpenGL / Vulkan / DirectX 12 output is byte-unchanged** (they do not use
      vkd3d). Any movement there means something is wrong.
- [x] **E1** — Docs: `project_facts.md` pins, `docs/validation-matrix.md`, the third-party notices
      (LGPL-2.1+ attribution for the new version), `CHANGELOG.md`, and any `SD0305` /
      FNA-coverage claims that A1 invalidates.

---

## 4. Acceptance Criteria

- [x] All five vkd3d 2.0 artifacts (4 desktop RIDs + WASM) are built, hosted on pinned tags, and
      SHA-256-verified by `tools/restore.*`; the release gate fails red if any is missing.
- [x] `Vkd3dLoader` and `Vkd3dNative` are **unchanged** (per §1.1/§1.2). If either needed a change,
      the compatibility research was wrong and this phase must stop and re-assess.
- [x] DirectX and FNA output still **matches the `mgfxc` / `fxc` goldens** at the same bar as today,
      on the real runtimes: `run-windows-render-gates.ps1 -IncludeFna` green (14/14 + FNA).
- [x] OpenGL, Vulkan, and DirectX 12 bytes are **provably unchanged**.
- [x] The cross-host byte-identity manifest is regenerated and green.
- [x] The A1 upside finding is recorded either way — including "no change to `SD0305`", if that is
      the honest answer.
- [x] No consumer-facing change: MonoGame pin, default MGFX version, and the public API are untouched.

## 5. Definition of Done

vkd3d-shader 2.0 ships in place of 1.17 on every host including the browser, ShadowDusk's DirectX
and FNA output is re-proven against the reference compilers on the real runtimes, the other three
backends are proven untouched, and the record says plainly what the upgrade bought — or that it
bought nothing, in which case the phase is closed as declined with the evidence attached.

## 6. Outcome (measured 2026-09-10)

Landed as **1.17 → 2.1**. The compatibility research in §1 held exactly: `Vkd3dLoader` and
`Vkd3dNative`'s call surface needed no change, the soname stayed `1` (2.1's `-version-info` is
`20:0:19`, major still `current - age` = 1), and `VKD3D_SHADER_API_2_0` was never declared.

### 6.1 A1 — the upside probe, answered

`SD0305` **partly clears**, which was the decision gate. Sweeping every `.fx` fixture at
`PlatformTarget.Fna`, **119 → 129 compiling, zero regressions**:

| Newly compiling | Why it failed on 1.17 |
|---|---|
| `BasicEffect.fx`, `EnvironmentMapEffect.fx` | `SD0305` SM2 register pressure — the register allocator now fits them |
| `DeferredSprite.fx`, `ForwardLighting.fx` | `E5017 SM1 cmp expression of type int` (the `clip((c < x) ? -1 : 1)` class) |
| `Nez/Reflection.fx` | `E5017 Flatten "if" conditionals branches` |
| `Apos.Shapes/apos-shapes{,-aa,-sm6}.fx`, `examples/Sd0402UniformBoundedLoop.fx` | `E5017 Instruction type HLSL_IR_LOOP` — the issue #212 class |

`SkinnedEffect.fx` still does not fit: 2.1 gets it from `r19` down to `r12`, but the `vs_2_0`
register file is 12, so it fails loudly (now vkd3d's own `E9015` rather than our patcher's `SD0305`).

**Two SM ≤ 3 gaps remain open on 2.1**, each now pinned by a test so they move when the pin does: a
vector store through a runtime index (`E5017 Non-constant vector addressing on store`), and a loop
whose trip count is a user-declared `int` **uniform** (`E5017 Loops with user-defined limiter int
uniform` — 2.0 implemented SM3 loops for every other bound). The native-build smoke probes the
int-uniform case on every RID and reports if upstream closes it.

### 6.2 A2 / D2 — the blast radius was exactly as predicted

Every `DirectX_Vkd3d` and `FNA` entry in the cross-host byte-identity manifest moved (**76 of 76**);
**every `OpenGL`, `Vulkan` and `DirectX_12` entry is unchanged**, which is the D2 check that nothing
leaked into the DXC path.

### 6.3 Two defects found on the way, both fixed at the root

1. **2.1 rejects a user-defined semantic on an SM4/5 pixel-shader output** (`E5013: Invalid semantic
   'COLOR'`). `fxc` accepts it — that is how every MonoGame `.fx` written against SM3 still builds at
   `ps_4_0` — and `DeferredSprite.fx` (a `struct` with `COLOR0`/`COLOR1` fields, i.e. MRT) hit it on
   `DirectX_11`. `FxPreParser` already rewrites the `) : COLOR<n>` **return** semantic, but by design
   cannot touch the same semantic on a struct **field**, since the struct may be a vertex-shader
   output where `COLOR` is legal. Fixed by passing `BACKWARD_COMPATIBILITY`/`MAP_SEMANTIC_NAMES` on
   the SM4+ target only (a no-op on SM1-3, where those are the native semantics) rather than
   widening the source rewrite per shader.
2. **2.1 types a sampler parameter from its USAGE where `fxc` types it from its DECLARATION.** A bare
   `sampler s0` read with `tex2D` came back `D3DXPT_SAMPLER2D` where `fxc` says `D3DXPT_SAMPLER`, and
   FNA binds off that table. `Fx2EffectBuilder` now takes the declared type from `SamplerInfo`, with
   the CTAB as the fallback for declarations we do not model.

Both were caught by comparison against the reference compiler, not by a hash: the second surfaced in
`FnaCompileFixtureTests.Golden_Fna_OutputStructurallyEquivalentToFxc`, which diffs our parameter
table against a real `fxc /T fx_2_0` golden.

### 6.4 B1/B2 — provenance moved into CI

`build-vkd3d-natives.yml` and `vkd3d-wasm-build.yml` now take `version` + `tarball_sha256` as
dispatch inputs, so a future bump needs no workflow edit. **win-x64 is built in CI for the first
time** (MSYS2 MINGW64, `libgcc`/`winpthread` linked statically, gated on an `objdump` system-only
linkage check) instead of being a local build a maintainer uploaded. All five artifacts are hosted on
`native-vkd3d-2.1` / `native-vkd3d-wasm-2.1` and SHA-256-pinned in `tools/restore.{ps1,sh}`.

**The WASM open question is answered: 2.1 still builds under emscripten 3.1.34 with zero source
patches**, and emscripten did not need to move.

Every RID's build smoke now compiles a `[loop]` with a data-dependent break at `ps_3_0`, so a native
that cannot do SM3 loops can never be published as a ≥ 2.0 artifact by accident.

### 6.5 D1 — the render ladder

`dotnet test ShadowDusk.slnx` green (5,566 tests). `run-windows-render-gates.ps1 -IncludeFna`:
**19/21**, with **every DirectX gate green** — the DX11 corpus, DX12 corpus, KNI-DX, DX Apos.Shapes,
the DX ShaderToy route and DX modern features — which is the proof that 2.1's DXBC renders
identically to `mgfxc`'s, semantic option included.

The two reds are the same shader, `Dots`, on the FNA and KNI-GL corpora, and are **pre-existing and
unrelated to this phase**: the KNI gate's own `ShadowDusk@MonoGame` column reads maxd 0 (our bytes
render identically across engines), the GL path never touches vkd3d and its manifest hashes did not
move, and an A/B on unmodified `main` with the 1.17 native reproduces the FNA numbers exactly.
Tracked as its own row in `docs/validation-matrix.md` §7.

### 6.6 Tests that pinned a 1.17 gap now pin a 2.1 gap

`FnaDiagnosticLocationTests`, `FnaCompileFixtureTests`'s loud-failure theory and the
`Vkd3dShaderCompilerTests` codegen case all used shaders whose gaps 2.1 closed. They cover the
**diagnostic locator**, never a particular gap, so they were re-pointed at the gaps that are still
open rather than deleted — and the class doc now says so, so the next pin bump does the same thing.

---

## 7. Open questions / risks (as written before the work; §6 answers them)

- **Does the WASM build still need zero patches?** Phase 4.1's headline finding was that pinned
  vkd3d 1.17 built to WASM via emscripten 3.1.34 with no source changes. 2.0 is three releases on;
  if it needs patches, the WASM artifact becomes the long pole and the phase may split.
- **Does `SD0305` actually clear?** §1.4 is upstream's framing, not our measurement. A1 exists
  precisely so this is not assumed.
- **FNA is the highest-risk target.** The register allocator, output-write hoisting, and branch
  flattening all concentrate on SM1-3. The FNA gate is off by default in the render-gate script;
  for this phase it is not optional.
- **Emscripten version drift.** The WASM build pins emscripten 3.1.34; vkd3d 2.0 may want newer,
  which is a second moving part in the same change.
- **One-way churn.** Once the goldens/manifest are regenerated, reverting to 1.17 means regenerating
  again. Do A1/A2 before B*, so the decision is made on evidence while backing out is still free.
