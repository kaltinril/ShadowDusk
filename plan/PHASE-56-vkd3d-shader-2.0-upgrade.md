# Phase 56 — vkd3d-shader 1.17 → 2.0 upgrade

**Track:** Native toolchain currency (post-1.0, output-affecting).

**Status:** 📋 **Planned / not started** (created 2026-07-28). Opened so the "we are three releases
behind on the native that produces our DirectX and FNA bytes" question has a home and does not get
re-researched from scratch every time someone runs a dependency audit. **Nothing here is urgent and
nothing is broken** — this is a deliberate, scoped upgrade to be scheduled, not a routine bump.

**Depends on:** nothing. The research below is done; the work is buildable today.

**Blocks:** [Phase 51](PHASE-51-consolidated-remainder-backlog.md) A8 item 2 closes when this phase
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
[`Vkd3dLoader`](../src/ShadowDusk.HLSL/Vkd3d/Vkd3dLoader.cs) needs **no change**:
`libvkd3d-shader-1.dll` (Windows) · `libvkd3d-shader.so.1` (Linux) · `libvkd3d-shader.1.dylib`
(macOS). `age` rising in lockstep with `current` is libtool's "interfaces were **added**, none
removed or changed" signal — i.e. backward-compatible ABI.

### 1.2 Our call surface is untouched, and the new strictness is opt-in

[`Vkd3dNative`](../src/ShadowDusk.HLSL/Vkd3d/Vkd3dNative.cs) uses exactly one entry point,
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
  ([Phase 49](DONE/PHASE-49-apos-shapes-regression-corpus.md)). Upstream's own framing ("we may
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

- [ ] **A1 — Upside probe FIRST, before any packaging work.** Build vkd3d 2.0 locally for `win-x64`
      only, point a scratch build at it, and compile the known `SD0305` casualties (`BasicEffect`,
      `SkinnedEffect`) plus the FNA corpus. **Record whether the register-allocator work actually
      clears those rejections.** This is the decision gate: if it does, the phase has a concrete
      product win and is worth the churn; if it does not, the phase is pure currency and can be
      deferred again with that recorded.
- [ ] **A2** — Measure the blast radius: compile the full DX + FNA corpus with 1.17 and with 2.0 into
      scratch dirs and diff. Record how many artifacts change and by how much (the expectation is
      "all of them"; confirm rather than assume).
- [ ] **B1** — Build vkd3d 2.0 for all four desktop RIDs using the existing recipe in
      `tools/restore.*` / the vkd3d build workflow; host on a new `native-vkd3d-2.0` release tag.
- [ ] **B2** — Rebuild the **WASM** artifact via `vkd3d-wasm-build.yml` (emscripten) against 2.0 and
      host on `native-vkd3d-wasm-2.0`. **Confirm it still builds with zero source patches** — that
      was a Phase 4.1 finding for 1.17 and must be re-verified, not assumed.
- [ ] **B3** — Update `tools/restore.ps1` / `restore.sh` pins + SHA-256 for all five artifacts, and
      the release-gate "natives present" checks.
- [ ] **C1** — Regenerate the DirectX_11 and FNA goldens with the reference compilers (goldens are
      `mgfxc`/`fxc` output, so they do **not** move — verify that explicitly; what moves is
      *ShadowDusk's* output, which must still match them).
- [ ] **C2** — Regenerate the cross-host byte-identity manifest and re-prove macOS/Linux bytes equal
      Windows bytes on the new native.
- [ ] **D1** — Full render ladder: `dotnet test ShadowDusk.slnx` plus
      `./validation/run-windows-render-gates.ps1 -IncludeFna` (the FNA gate is **mandatory** here —
      SM1-3 is where the codegen changed most). Plus the node + real-browser vkd3d gates
      (`node-test-vkd3d-wasm.mjs`, `browser-vkd3d-gate.mjs`) for the WASM artifact.
- [ ] **D2** — Confirm **OpenGL / Vulkan / DirectX 12 output is byte-unchanged** (they do not use
      vkd3d). Any movement there means something is wrong.
- [ ] **E1** — Docs: `project_facts.md` pins, `docs/validation-matrix.md`, the third-party notices
      (LGPL-2.1+ attribution for the new version), `CHANGELOG.md`, and any `SD0305` /
      FNA-coverage claims that A1 invalidates.

---

## 4. Acceptance Criteria

- [ ] All five vkd3d 2.0 artifacts (4 desktop RIDs + WASM) are built, hosted on pinned tags, and
      SHA-256-verified by `tools/restore.*`; the release gate fails red if any is missing.
- [ ] `Vkd3dLoader` and `Vkd3dNative` are **unchanged** (per §1.1/§1.2). If either needed a change,
      the compatibility research was wrong and this phase must stop and re-assess.
- [ ] DirectX and FNA output still **matches the `mgfxc` / `fxc` goldens** at the same bar as today,
      on the real runtimes: `run-windows-render-gates.ps1 -IncludeFna` green (14/14 + FNA).
- [ ] OpenGL, Vulkan, and DirectX 12 bytes are **provably unchanged**.
- [ ] The cross-host byte-identity manifest is regenerated and green.
- [ ] The A1 upside finding is recorded either way — including "no change to `SD0305`", if that is
      the honest answer.
- [ ] No consumer-facing change: MonoGame pin, default MGFX version, and the public API are untouched.

## 5. Definition of Done

vkd3d-shader 2.0 ships in place of 1.17 on every host including the browser, ShadowDusk's DirectX
and FNA output is re-proven against the reference compilers on the real runtimes, the other three
backends are proven untouched, and the record says plainly what the upgrade bought — or that it
bought nothing, in which case the phase is closed as declined with the evidence attached.

## 6. Open questions / risks

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
