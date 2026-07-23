# Issue #145 — Vulkan target broken for VS-driven and legacy-sampler shaders

**Status:** ✅ Root causes isolated, reproduced, **FIXED, and RENDER-PROVEN** — ShadowDusk's VS-driven
Vulkan output now renders **pixel-identical (maxd 0)** to the real mgfxc 3.8.5 golden on a real MonoGame
3.8.5 DesktopVK device (`validation/VsDrivenVulkan`), and restoring the bug turns that gate red at maxd 255.
Merged to `main` in [PR #146](https://github.com/kaltinril/ShadowDusk/pull/146) (`67a1dc47`, 2026-07-23); GitHub issue #145 CLOSED. Ships in 0.13.0.
See *§ What was implemented* and *§ Second pass* for the change record and what remains.
Driven by GitHub issue [#145](https://github.com/kaltinril/ShadowDusk/issues/145) (Apostolique / Jean-David Moisan,
author of Apos.Shapes): `apos-shapes.fx` compiled with ShadowDusk 0.12.1 `/Profile:Vulkan` does not work on a real
MonoGame 3.8.5 `DesktopVK` device, in two different ways depending on which branch of the shader is taken.

**Reported:** 2026-07-23 · **Researched:** 2026-07-22 (this doc) · **Reporter environment:** ShadowDusk.Cli 0.12.1,
MonoGame.Framework.Native 3.8.5 + MonoGame.Runtime.Windows.Vulkan 3.8.5, Windows 11, Intel GPU, Vulkan 1.4.
Working control: `dotnet-mgfxc 3.8.5 /Profile:Vulkan` renders correctly on the same device.

---

## TL;DR — two independent, confirmed ShadowDusk bugs

| # | Symptom reported | Root cause (confirmed) | Where |
|---|---|---|---|
| 1 | **SM6 path renders nothing** (loads + draws without error, output fully transparent/black) | ShadowDusk passes **`-Zpr` (row-major matrices) to DXC on the Vulkan path**; mgfxc does not. MonoGame uploads a `Matrix` parameter **transposed for HLSL's column-major default**, so ShadowDusk's VS reads `view_projection` **transposed** → `mul(v.Position, view_projection)` throws every vertex off-screen → nothing rasterizes. | `src/ShadowDusk.HLSL/Dxc/DxcFlagBuilder.cs:99` |
| 2 | **Legacy `tex2D` path access-violates in native `GraphicsDevice_DrawIndexed`** | FxPreParser's legacy-`sampler2D`→modern conversion synthesizes `Texture2D <name>_SDTexture;` with **no register**, and `VulkanTextureSamplerBindingRewriter` **deliberately skips `_SDTexture` names** — so the pair lands on **different** SPIR-V bindings: image at **0/1**, sampler at **32/33**. MonoGame's native Vulkan descriptor writer computes `int slot = binding - 32`, giving **`slot = -32`** → out-of-bounds `device->textures[stage][-32]` → AV. The separate `VK_DESCRIPTOR_TYPE_SAMPLER` entries additionally hit an unhandled `assert(0)` branch. | `src/ShadowDusk.Compiler/Internal/VulkanTextureSamplerBindingRewriter.cs:53` + `src/ShadowDusk.HLSL/FxPreParser.cs` (`SynthTextureName`) |

Both are **ShadowDusk-side**, both are **invisible to the current test + validation suites**, and both are a direct
consequence of the same validation gap: **the Vulkan corpus is 10 PS-only, modern-syntax, matrix-free shaders**
(`validation/CandidateVulkan`), so no Vulkan proof has ever exercised (a) a real vertex shader, (b) a matrix
parameter, or (c) legacy `sampler2D`/`tex2D` source.

Bug 1 is the Vulkan repeat of **issue #70** (OpenGL matrix transpose) and of the DXBC-oracle
`PackMatrixColumnMajor` fix (`src/ShadowDusk.HLSL/D3DCompiler/D3DCompilerShaderCompiler.cs:93-105`). GL and DX were
both fixed for exactly this; **Vulkan is the one backend that never got the treatment**, because its corpus has no
matrix to be transposed.

---

## Evidence — reference-compiler A/B on the reporter's actual shader

Everything below is reproduced locally (Windows dev box, 2026-07-22) against **real `dotnet-mgfxc 3.8.5`**
(installed side-by-side into a scratch `--tool-path`; the repo's global tool is 3.8.4.1, which has no Vulkan profile).

```bash
# the exact shader from the issue (Apos.Shapes main, SM6-ready)
curl -sL -o apos-shapes-upstream.fx \
  https://raw.githubusercontent.com/Apostolique/Apos.Shapes/main/Source/Content/apos-shapes.fx

# reference compiler
dotnet tool install --tool-path ./tools dotnet-mgfxc --version 3.8.5
./tools/mgfxc.exe apos-shapes-upstream.fx apos-upstream-vk-mgfxc.mgfx /Profile:Vulkan

# ShadowDusk (this repo, main @ dedac27)
./src/ShadowDusk.Cli/bin/Debug/net8.0/ShadowDuskCLI.exe apos-shapes-upstream.fx apos-upstream-vk.mgfx /Profile:Vulkan

# decode both containers
python validation/decode_mgfx_vulkan.py <file>.mgfx
```

Both compile cleanly (exit 0) and both decode to the footer with **zero leftover bytes** — the container shape is
right. The defect is in *what* is inside it.

### Container A/B (`apos-shapes.fx` upstream `main`, `/Profile:Vulkan`)

| Field | mgfxc 3.8.5 | ShadowDusk 0.12.x (`main`) | Verdict |
|---|---|---|---|
| header | v11, profile 80 | v11, profile 80 | ✅ match |
| cbuffer | `type.$Globals` size 80, off `[0,64,72,76]` | `$Globals` size 80, off `[0,64,72,76]` | ✅ layout match (name cosmetic) |
| PS bindings | `[(0,UBO_DYN),(32,COMBINED),(33,COMBINED),(34,COMBINED)]` | identical | ✅ match |
| VS bindings | `[(0,UBO_DYN,VERTEX)]` | identical | ✅ match |
| PS `textureSlots` | `0x7` | `0x7` | ✅ |
| PS **`samplerSlots`** | **`0x7`** | **`0x0`** | ⚠️ divergent (see §Secondary) |
| VS **attributes** | **13** (`POSITION0`, `TEXCOORD0..9`, `POSITION1`, `NORMAL0`) | **0** | ⚠️ divergent (see §Secondary) |
| SPIR-V entry point | `main` | `main` | ✅ |
| SPIR-V extensions | *(none)* | `SPV_GOOGLE_hlsl_functionality1`, `SPV_GOOGLE_user_type` | ⚠️ divergent (see §Secondary) |
| **`view_projection` matrix decoration** | **`RowMajor`** | **`ColMajor`** | 🔴 **BUG 1** |
| parameters | 7 (4 globals + 3 textures) | 10 (+3 sampler-named `Texture` params) | ⚠️ divergent |
| sampler record names | `TextureSampler` / `FontSampler` / `BlueNoiseSampler` | `ps_s0` / `ps_s1` / `ps_s2` | ℹ️ cosmetic on Vulkan |
| pass name (unnamed `pass {`) | `''` | `'P0'` | ℹ️ cosmetic, target-independent |

> **Reading the SPIR-V decoration.** DXC's SPIR-V backend inverts the HLSL majorness term (SPIR-V stores a matrix as
> an array of column vectors), so **`RowMajor` in SPIR-V ⇔ HLSL *column-major*** and vice-versa. This is not folklore
> here — it is demonstrated by the A/B itself: mgfxc passes no `-Zpr` (HLSL default = column-major) and gets
> `RowMajor`; ShadowDusk passes `-Zpr` (HLSL row-major) and gets `ColMajor`, from the same DXC on the same source.

---

## Bug 1 — `-Zpr` on the Vulkan path transposes every matrix (symptom: nothing renders)

### The chain

1. `DxcFlagBuilder.Build` adds **`-Zpr` unconditionally** for every DXC invocation
   (`src/ShadowDusk.HLSL/Dxc/DxcFlagBuilder.cs:99`), including `PlatformTarget.Vulkan`.
2. Real mgfxc's Vulkan DXC command line (`Tools/MonoGame.Effect.Compiler/Effect/ShaderProfile.Vulkan.cs`, v3.8.5) is
   exactly:
   `-nologo -spirv -fvk-use-dx-layout -fspv-reflect [VS: -fvk-invert-y -fvk-use-dx-position-w] [PS: -auto-binding-space 1] -fvk-t-shift 32 all -fvk-s-shift 32 all -T {vs|ps}_6_0 -E main` —
   **no `-Zpr`**, i.e. HLSL's column-major default.
3. MonoGame's runtime uploads matrices column-major, in two places, both explicit about it:
   - `EffectParameter.SetValue(Matrix)` — *"HLSL expects matrices to be transposed by default. These unrolled loops
     do the transpose during assignment"* (writes `M11,M21,M31,M41, M12,...`).
   - `ConstantBuffer.SetParameter` — *"HLSL assumes matrices are column-major, whereas in-memory we use row-major.
     **TODO: HLSL can be told to use row-major. We should handle that too.**"* (swaps rows/columns for
     `EffectParameterClass.Matrix`).
   That TODO is precisely the case ShadowDusk creates: the runtime has no idea the shader was built row-major.
4. Net effect: the VS reads `view_projection` transposed. For a 2D ortho `view_projection` the transpose maps
   geometry far outside clip space → **the quad never rasterizes → a fully transparent/black frame, no error, no
   crash.** Exactly the reported symptom, and exactly why the PS-only corpus never noticed (no matrix, no VS).

### Verified fix experiment (done, then reverted — repo is clean)

Patching `DxcFlagBuilder` to skip `-Zpr` when `platform == PlatformTarget.Vulkan`, rebuilding, and recompiling the
same shader:

```
before:  M type.$Globals member=0 ColMajor    MatrixStride=16     cb size=80 off=[0,64,72,76]
after:   M type.$Globals member=0 RowMajor    MatrixStride=16     cb size=80 off=[0,64,72,76]   ← == mgfxc
```

The decoration flips to match mgfxc **and nothing else in the container changes** (same cbuffer size, same offsets,
same bindings, same slot masks) — the reflection offsets are majorness-independent, same as the DX finding in
`D3DCompilerShaderCompiler.cs:102-104`. The one-line change is the candidate fix; it is **Vulkan-only** and cannot
disturb the other backends:

- **OpenGL** also compiles with `-Zpr` but *compensates in the rewriter* (`BuildUploadedMat4` reconstructs the
  matrix transposed — the issue-#70 fix). Untouched.
- **DirectX** does not use DXC at all (d3dcompiler_47 oracle with `ShaderFlags.PackMatrixColumnMajor`). Untouched.
- **FNA** goes through vkd3d. Untouched.

---

## Bug 2 — legacy `tex2D` shaders emit an un-paired image/sampler at binding 0 (symptom: native AV)

### What ShadowDusk actually emits

Compiling the **legacy** (pre-SM6) form — the repo's own vendored fixture
`tests/fixtures/shaders/third-party/Apos.Shapes/apos-shapes.fx`, which is the `sampler TextureSampler : register(s0);` +
`sampler FontSampler;` + `tex2D(...)` shape — for Vulkan:

```
PS vkLayout: bindings=[(0, SAMPLED_IMAGE, FRAGMENT), (1, SAMPLED_IMAGE, FRAGMENT), (32, SAMPLER, FRAGMENT), (33, SAMPLER, FRAGMENT)]
SPIR-V:  TextureSampler_SDTexture set=1 binding=0     TextureSampler set=1 binding=32
         FontSampler_SDTexture    set=1 binding=1     FontSampler    set=1 binding=33
```

i.e. **four separate descriptors instead of two combined ones**, with the images at raw bindings **0 and 1**.

### Why that is a guaranteed native crash (source-grounded, MonoGame 3.8.5 `native/monogame/vulkan/MGG_Vulkan.cpp`)

`MGVK_UpdateDescriptors` (≈ line 3487) writes each descriptor as:

```cpp
else if (w.descriptorType == VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER) {
    int slot = w.dstBinding - 32;
    ...->sampler = device->samplers[stage][slot]->sampler;          // no null check
    auto tex = device->textures[stage][slot]; ...
}
else if (w.descriptorType == VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE) {
    int slot = w.dstBinding - 32;                                   // ← binding 0 ⇒ slot == -32
    auto tex = device->textures[stage][slot];                       // ← OOB read
    ... : device->nullTexture[(int)shader->textureTypes[slot]]->view;// ← OOB read
}
else { /* VK_DESCRIPTOR_TYPE_SAMPLER lands here */ assert(0); }      // ← no handler at all
```

So ShadowDusk's legacy output hits **both** failure modes: a `SAMPLED_IMAGE` at binding 0/1 indexes the texture and
`textureTypes` arrays at **-32/-31**, and the two `SAMPLER` descriptors fall into the unhandled `else`, leaving an
uninitialised `VkWriteDescriptorSet` to be submitted to `vkUpdateDescriptorSets`. The reported
`AccessViolationException` inside `GraphicsDevice_DrawIndexed` is the expected outcome.

This also **retro-confirms with source** the Phase-32 empirical finding (*"separate SAMPLED_IMAGE+SAMPLER pair
crashes, combined works"*), which at the time was inferred from a bisect against a real device.

### Why our own rewriter doesn't prevent it

`VulkanTextureSamplerBindingRewriter` exists exactly to force a texture/sampler pair onto matching explicit
registers so `-fvk-t-shift/-fvk-s-shift 32` co-locate them. It fails here for two compounding reasons:

1. **The `_SDTexture` exclusion** (`VulkanTextureSamplerBindingRewriter.cs:53-58`) skips every texture FxPreParser
   synthesized for a legacy sampler — i.e. **every texture on the legacy path**. The guard was added in Phase 32 for
   a real reason (a synthesized declaration leaking in from a *non-Vulkan* `#if` branch), but its effect is that the
   legacy path is left entirely unpaired.
2. **Its regexes only match declarations with no register at all** (`Texture2D name;` / `SamplerState name;`). A
   legacy sampler that already carries `: register(s0)` / `: register(s2)` is skipped, so even without the
   `_SDTexture` guard the rewriter would never mirror `s2` onto the synthesized texture.

There is a **third, latent** defect in the same code: the index allocator (`int next = 0; ...`) does not reserve
register indices that are already explicitly used in the source. On upstream `apos-shapes.fx`'s legacy branch
(`register(s0)`, unregistered `FontSampler`, `register(s2)`) an unregistered pair can be assigned index 2 and
**collide with `BlueNoiseSampler`'s explicit `s2`** — two descriptor-set-layout bindings at the same binding number,
which Phase 32 already established is invalid and crashes.

---

## Secondary divergences found in the same A/B (not the reported failures, but real)

| # | Divergence | Runtime impact today | Notes |
|---|---|---|---|
| S1 | **VS attribute table empty** (0 vs mgfxc's 13). `CompilationPipeline.cs:1784` returns `noAttributes` for the Vulkan branch. | **None currently.** `MGG_InputLayout_Create` assigns `attrib.location = i` **positionally from the `VertexDeclaration`** and never reads the shader's attribute table; mgfxc's own writer even comments *"These are unused at runtime under the new native backends, we will remove them soon."* | Still a faithfulness gap: mgfxc emits `(usage,index)` per input location. Cheap to emit; protects against a future runtime that does read it. |
| S2 | **`samplerSlots` mask left 0** for combined descriptors (mgfxc sets *both* `textureSlots` and `samplerSlots`). `VulkanShaderCodeWrapper.cs:64-73` `continue`s past a sampler that shares its texture's binding before setting the bit. | Low. The mask is only used in `MGVK_UpdateDescriptors`' dirty early-out (`textureSamplerDirty & samplerSlots`); `textureSlots` covers the same slots today. | Proven harmless on the 10-shader corpus, but it is a divergence with a plausible "sampler-state-only change is missed" failure mode. Match mgfxc. |
| S3 | **Shipped SPIR-V still carries `-fspv-reflect` Google extensions** (`SPV_GOOGLE_hlsl_functionality1`, `SPV_GOOGLE_user_type`). mgfxc compiles **twice** — once with `-fspv-reflect` for reflection, then again **without** it for the bytes it ships, commented *"reflection info that forces Google VK extensions into the binary"*. | Unknown/driver-dependent. Works on this dev GPU and on the reporter's Intel GPU (his SM6 build loads + draws), so it is **not** the cause of #145. | **This resolves Phase 32's open item 5** ("is stripping precautionary?"): mgfxc definitively strips. A driver that rejects an unknown extension in `vkCreateShaderModule` would fail here; matching mgfxc removes the risk class. |
| S4 | **Extra parameters**: ShadowDusk emits a `Texture`-class parameter for each `SamplerState` (`TextureSampler`, `FontSampler`, `BlueNoiseSampler`) on top of the texture params; mgfxc emits only the 3 textures. | Benign for the reported symptoms — our per-sampler records point `param` at the *texture* parameter (4/5/6), matching mgfxc's semantics. | Changes `effect.Parameters` shape vs. mgfxc. Investigate whether this is Vulkan-only before touching (shared writer code paths reach GL/DX, which are rung-4 proven as-is). |
| S5 | cbuffer name `$Globals` vs mgfxc `type.$Globals`; sampler record names `ps_sN` vs the real HLSL sampler names; unnamed `pass {` written as `P0` vs `''`; `SourceFile` full path vs file name. | None known — MonoGame's Vulkan path binds by slot/index, not name. | Cosmetic. Listed for completeness so a future A/B doesn't re-litigate them. |

---

## Why the suites did not catch this (the validation-gap lesson, again)

- **The Vulkan corpus is PS-only.** `validation/CandidateVulkan` runs the same 10 post-FX fixtures as the GL/DX
  image corpus, each given an `#if VULKAN` modern-syntax branch. A pass with only a `PixelShader` leaves the vertex
  stage to MonoGame's own `SpriteEffect` — so **no ShadowDusk-produced vertex shader has ever been rendered on
  Vulkan**, and bug 1 (a VS-only symptom) was unreachable.
- **No matrix anywhere in the Vulkan corpus.** Bug 1 needs a non-identity matrix parameter to be visible — the same
  blind spot that hid issue #70 on GL and the DXBC transpose on DX. The lesson from `plan/DONE/ISSUE-70-*.md`
  ("identity matrices are transpose-invariant") was applied to GL and DX, **but the Vulkan gate was never extended**.
- **No legacy-syntax shader in the Vulkan corpus.** Phase 32 deliberately authored modern `Texture2D`/`SamplerState`
  branches because *"legacy `sampler2D`/`tex2D`/`COLOR` is rejected by DXC at SM6"* — but ShadowDusk's FxPreParser
  *converts* legacy source and happily compiles it for Vulkan, so the legacy path is reachable by real users
  (mgfxc simply errors out instead) and is 100 % broken there. Bug 2 lives entirely in that untested path.
- **`dotnet test` cannot see any of it** — this is shader-output/runtime behaviour, the `validation/*` render-gate
  category. And the Vulkan gate is **opt-in** (`run-windows-render-gates.ps1 -IncludeVulkan`), so even the manual
  Windows gate does not run it by default.

---

## Fix plan (proposed — nothing implemented yet)

Ordered by "closes the reported bug" first, then faithfulness.

### F1 — Drop `-Zpr` for Vulkan *(closes symptom 1)*

One line in `DxcFlagBuilder.Build`: don't add `-Zpr` when `platform == PlatformTarget.Vulkan` (matching mgfxc's
argument list exactly). Verified above to flip the decoration to `RowMajor` with no other container change.

**Regression test (must be RED before, GREEN after):** compile a matrix-carrying VS fixture for Vulkan and assert the
emitted SPIR-V decorates the `$Globals` matrix member `RowMajor` (HLSL column-major) — the project already has a
SPIR-V decoration parser (`src/ShadowDusk.Core/Reflection/Spirv/SpirvReflectionParser.cs` handles
`SpirvDecoration.RowMajor`), so this is a pure unit test, plus a `MatrixConventionSweep`-style Vulkan analogue.

### F2 — Pair legacy-converted texture/sampler declarations on Vulkan *(closes symptom 2)*

`VulkanTextureSamplerBindingRewriter` must produce **matching `t`/`s` register indices for every pair, including
FxPreParser's synthesized textures**, and must **never allocate an index that an explicit `register(tN)`/`register(sN)`
in the source already occupies**. Concretely:

1. Parse existing `: register(t#)` / `: register(s#)` annotations and **reserve** those indices.
2. For a synthesized `<name>_SDTexture` paired with a sampler that has an explicit `register(sN)`, emit
   `register(tN)` for the texture (mirror, don't re-number). The `_SDTexture` exclusion must become *"don't
   re-number a synthesized texture from an unrelated branch"*, not *"never touch it"* — the original Phase-32 hazard
   needs re-checking against the current FxPreParser behaviour.
3. Allocate a fresh **shared** index for a pair where neither half is registered, skipping reserved indices.
4. Guard: after the rewrite, no two resources may share a raw binding unless they are the two halves of one pair.

**Alternative worth evaluating:** have FxPreParser emit the register annotation on the synthesized `Texture2D` at
synthesis time (it already knows the sampler's register), which removes the need for a Vulkan-private regex pass over
the whole file. Cleaner, but touches a shared component used by every target — needs the full `dotnet test` bar.

**Regression tests:** compile the vendored legacy `apos-shapes.fx` for Vulkan and assert (a) every image/sampler pair
shares one raw binding, (b) every emitted `VkDescriptorSetLayoutBinding` is `COMBINED_IMAGE_SAMPLER` with
`binding >= 32`, (c) no duplicate binding numbers. All three are directly assertable from the compiled bytes.

### F3 — Emit the VS attribute table for Vulkan *(faithfulness, S1)*

Populate `attributes` from the SPIR-V input semantics (usage from `POSITION/TEXCOORD/NORMAL/...`,
`index = semantic index + locationIndex`, `location = 0`, `name = ""`), mirroring
`ShaderProfile.Vulkan.CreateShader`'s loop. Not load-bearing today (see S1) — do it as part of the same PR so the
container is byte-shaped like mgfxc's.

### F4 — Set the `samplerSlots` bit for combined descriptors *(faithfulness, S2)*

Small change in `VulkanShaderCodeWrapper.Wrap`: when a texture and sampler share a raw binding, set the bit in
**both** masks (mgfxc: `textureSlots |= 1 << slot; samplerSlots |= 1 << slot;`).

### F5 — Strip `-fspv-reflect` from the shipped bytecode *(faithfulness/robustness, S3)*

Reproduce mgfxc's two-pass compile: reflect from the `-fspv-reflect` module, ship the module compiled **without** it.
Cost is a second DXC invocation per Vulkan entry point. This closes Phase 32's open item 5 with a definitive answer.

### F6 — Close the validation gap (the part that actually prevents the next one)

1. **Add a VS-driven Vulkan render gate** — `validation/VsDrivenVulkan`, the Vulkan sibling of `validation/VsDriven`
   / `VsDrivenDx`, with a **non-identity, asymmetric** matrix (the issue-#70 input discipline).
2. **Add `apos-shapes.fx` (both the legacy and the SM6 branch) to the Vulkan corpus**, and refresh the vendored
   fixture — it is pinned at upstream commit `3fb73b8` and predates the SM6 branch, the third sampler, the dither
   globals, and the `FixSnorm` workaround the issue describes.
3. **Un-gate Vulkan in `validation/run-windows-render-gates.ps1`** (currently `-IncludeVulkan` opt-in), or at minimum
   require it for any Vulkan-affecting change, the same way KNI-GL and the ANGLE probe were folded in on 2026-07-19.
4. **A true reference-compiler A/B is now possible on Vulkan.** Phase 32 recorded that `validation/BaselineVulkan`
   could not render mgfxc's own goldens because of mgfxc's `SlotOffset` wraparound for *auto-numbered* resources
   (`texSlot=224/225`, still visible in `tests/fixtures/golden/Vulkan/Grayscale.mgfx`). That bug **does not trigger
   when every texture/sampler carries an explicit matching register** — as `apos-shapes.fx`'s SM6 branch does, which
   is why mgfxc's build renders correctly for the reporter. Regenerating the Vulkan goldens from explicit-register
   fixtures should unlock a genuine **pixel** diff against the reference compiler on Vulkan (evidence rung 4 with an
   oracle, instead of "our own output looks right").

### F7 — Run the WHOLE fixture corpus through the gates, not a hard-coded 10

The 10-shader list in `validation/Shared/ShaderInputs.cs:19` is shared by **every** render driver
(`Candidate`, `CandidateDx`, `CandidateVulkan`, `Baseline*`, the KNI drivers…). The repo has **122 `.fx`
fixtures**; the render gates exercise **10 of them, all PS-only, none with a matrix**. That single array is why
both bugs in this issue were unreachable.

How much the existing corpus would have caught, measured over `tests/fixtures/shaders/**.fx`:

| Property | Fixtures | Relevance |
|---|---:|---|
| total `.fx` | 122 | |
| use legacy `tex2D(` | **81** | every one takes FxPreParser's conversion path → **bug 2's crash shape on Vulkan** |
| carry a `float4x4`/matrix | **35** | **bug 1** is visible in any of them with a non-identity value |
| VS-driven (`VertexShader = compile …`) | **32** | the stage the Vulkan gate has never rendered |
| have an `SM6`/`vs_6_0` branch | 6 | the only ones Vulkan can compile *without* the legacy conversion |
| currently in the render gate | **10** | PS-only, matrix-free |

**F7.1 — A device-free, corpus-wide structural gate (do this first; it is CI-able).** Both bugs in this issue are
detectable **from the emitted bytes alone, with no GPU**: compile all 122 fixtures for every target they support and
assert per-target invariants —

- *Vulkan:* every `$Globals` matrix member decorated SPIR-V `RowMajor` (= HLSL column-major, matching mgfxc);
  every image/sampler pair on one shared raw binding; every emitted binding `>= 32` unless it is the uniform buffer
  at 0; no duplicate binding numbers; entry point named `main`; one cbuffer per stage.
- *All targets:* container decodes to the footer with zero leftover bytes (the `decode_mgfx*.py` invariant, already
  scripted); cbuffer offsets agree with the reflection; no parameter/sampler index dangles.

This is cheap, deterministic, runs on Linux CI, and would have turned **~81 fixtures red for bug 2 and ~35 for
bug 1** the day each was introduced. It is the highest-value item in this whole plan.

**F7.2 — Replace the hard-coded name array with a corpus manifest.** One declarative source (e.g.
`tests/fixtures/shaders/corpus.json` or per-fixture front-matter) giving each fixture its supported targets and the
harness shape it needs. Drivers then iterate **everything that qualifies**, default-in. Opt-out must be explicit and
carry a reason string, and each driver must **print the skipped count and reasons** — no silent caps, so a shrinking
corpus can't masquerade as a green run.

**F7.3 — Teach the harness the shapes it can't currently draw.** `EffectImageRenderer` draws a textured quad via
`SpriteBatch`, which is why the corpus is PS-only. To reach the other 112 it needs: a VS-driven path with its own
vertex buffer + non-identity asymmetric matrix (the issue-#70 input discipline), a parameter-driven path for the
stock effects (world/view/projection, lights, bones), and instanced / texture-array / cube+3D variants. This, not
the file count, is the actual work.

**F7.4 — Goldens.** Every added fixture needs a reference-compiler golden for each target it claims
(`mgfxc` per profile, `fxc /T fx_2_0` for FNA). Where the oracle genuinely cannot produce one — e.g. mgfxc 3.8.5's
`SlotOffset` wraparound on auto-numbered Vulkan resources, or a DX-only HiDef fixture on a GL gate — record the
evidence rung actually reached rather than dropping the fixture silently (`compare_vulkan.py` already reports this
honestly; keep that pattern).

**F7.5 — Tiering, so this stays runnable.** The structural gate (F7.1) runs on every PR in CI. The 10-shader render
smoke stays the fast pre-merge check. The **full** corpus render sweep runs on the Windows GPU box before a release
and on any change that touches shader output — which is already the `run-windows-render-gates.ps1` contract, just
over a corpus that means something.

> **Honest scope note:** "all 122" is the *default*, not a promise that 122 render on every backend. Include-only
> `.fxh` files (5), target-specific fixtures (FNA `fx_2_0`, DX-only HiDef VTF/cube), and anything needing a bespoke
> harness will be tagged out — the point of F7.2 is that every exclusion is written down and counted instead of
> being an unexamined array of ten names.

### F8 — Vendor MonoGame's own test effects (the reference compiler's acceptance set)

`MonoGame/MonoGame` `Tests/Assets/Effects/` ships **20 `.fx` + 3 `.fxh`** under the **Microsoft Public License
(Ms-PL)** (repo `LICENSE.txt`, "MonoGame - Copyright © 2009-2026 MonoGame Foundation, Inc") — permissive and
vendorable under the corpus rules in `docs/test-shader-corpus.md` §4 / the issue-106 license gate.

The decisive part: that folder contains **per-profile `.mgcb` build lists — MonoGame's own statement of what each
backend must compile**. `Vulkan.mgcb` builds 14 effects:

```
Bevels, BlackOut, ColorFlip, Grayscale, HighContrast, Invert, NoEffect, RainbowH,
Instancing, VertexTextureEffect, CustomSpriteBatchEffect, CustomSpriteBatchEffectComparisonSampler,
TextureArrayEffect, ParameterTypes
```

`DirectX.mgcb` builds the same set plus `ParserTest.fx` (and `DirectX12.mgcb` / `OpenGL.mgcb` give the other
targets), so this is a ready-made **cross-target matrix** with an oracle: `mgfxc 3.8.5` compiles every one of them
for every profile, so goldens are a scripted loop.

Why it matters for this issue specifically:

- **`Instancing.fx` and `VertexTextureEffect.fx` are VS-driven with matrices, and MonoGame itself builds them for
  Vulkan** — either one would have caught bug 1 the day Phase 32 shipped.
- **`ParameterTypes.fx`** sweeps the parameter-class/type space (the `$Globals` layout, `rows`/`columns`,
  arrays) — direct coverage for the cbuffer/parameter-table joins.
- **`TextureArrayEffect.fx`, `CustomSpriteBatchEffectComparisonSampler.fx`** cover sampler/texture shapes beyond
  one 2D sampler — the area bug 2 lives in.
- **`ParserTest.fx`, `PreprocessorTest.fx`, `DefinesTest.fx`, `Include.fxh`, `PreprocessorInclude.fxh`** are the
  reference compiler's own parser/preprocessor torture tests — straight regression value for `FxPreParser`
  (the issue-#106 bug class).
- **`Mobile/test.fx` + `Mobile/Macros.fxh`** additionally exercise the mobile macro layer.

Vendoring follows the established process: `tests/fixtures/shaders/third-party/MonoGame/` with `LICENSE` +
`NOTICE.md`, a provenance header on each file (repo, commit/tag `v3.8.5`, upstream path, license) with the shader
body **unmodified**, an entry in `docs/test-shader-corpus.md` §4, and manifest tags (F7.2) recording which targets
each one claims — taken directly from the `.mgcb` lists rather than guessed.

---

## Reproduction recipe (self-contained)

```powershell
# 1. get the reporter's shader
curl -sL -o apos-shapes-upstream.fx https://raw.githubusercontent.com/Apostolique/Apos.Shapes/main/Source/Content/apos-shapes.fx

# 2. reference + candidate
dotnet tool install --tool-path .\tools dotnet-mgfxc --version 3.8.5
.\tools\mgfxc.exe apos-shapes-upstream.fx ref.mgfx /Profile:Vulkan
dotnet build src\ShadowDusk.Cli\ShadowDusk.Cli.csproj
.\src\ShadowDusk.Cli\bin\Debug\net8.0\ShadowDuskCLI.exe apos-shapes-upstream.fx cand.mgfx /Profile:Vulkan

# 3. container A/B  (attributes 13 vs 0, samplerSlots 0x7 vs 0x0)
python validation\decode_mgfx_vulkan.py ref.mgfx
python validation\decode_mgfx_vulkan.py cand.mgfx

# 4. the matrix decoration (RowMajor vs ColMajor) + Google extensions
#    — scan each embedded SPIR-V module's OpMemberDecorate/OpExtension.
```

Bug 2 needs no device either: compile `tests/fixtures/shaders/third-party/Apos.Shapes/apos-shapes.fx`
(the legacy fixture) for Vulkan and read the binding table — `SAMPLED_IMAGE` entries at bindings 0/1 with
`SAMPLER` entries at 32/33 is the crash signature.

---

## Open questions / notes for the implementer

- **Does F1 alone make the reporter's SM6 build render?** The matrix transpose fully explains "geometry never
  appears", but it can only be *proven* by a real DesktopVK render (F6.1). Do not close #145 on the container diff
  alone — the project bar is the real runtime.
- **Is the `_SDTexture` guard still needed at all?** It was added for a synthesized declaration leaking from a
  non-Vulkan `#if` branch. Re-derive that case against today's FxPreParser before removing or narrowing it; if it is
  still real, the fix must distinguish *"synthesized for the branch being compiled"* from *"synthesized for another
  branch"*.
- **Do the extra sampler parameters (S4) come from Vulkan-specific code or the shared writer?** If shared, changing
  them re-opens the GL/DX rung-4 proofs — measure before touching.
- **The one-cbuffer-per-stage rule** (mgfxc throws for a second cbuffer on Vulkan; Phase 32 step 6 planned an
  `SD00xx` for it) — `apos-shapes.fx` has a single `$Globals`, so it is untouched by this issue, but verify it is
  still enforced while in this code.
- **`mgfxc` 3.8.5 side-installs cleanly** via `dotnet tool install --tool-path <dir> dotnet-mgfxc --version 3.8.5`
  and coexists with the repo's 3.8.4.1 global tool — useful for any future Vulkan oracle work (the repo's checked-in
  Vulkan goldens are 3.8.5 output).

## Files that will be touched by the fix

- `src/ShadowDusk.HLSL/Dxc/DxcFlagBuilder.cs` — F1 (and F5's second pass).
- `src/ShadowDusk.Compiler/Internal/VulkanTextureSamplerBindingRewriter.cs` (± `src/ShadowDusk.HLSL/FxPreParser.cs`) — F2.
- `src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs` — F3 (Vulkan attribute table), F5 (two-pass).
- `src/ShadowDusk.Core/VulkanShaderCodeWrapper.cs` — F4.
- `validation/VsDrivenVulkan/*`, `validation/run-windows-render-gates.ps1`, `tests/fixtures/shaders/third-party/Apos.Shapes/*` — F6.
- Support-surface docs per `CLAUDE.md`'s standing rule: `docs/validation-matrix.md` (§6 driver row + the Vulkan
  cells), `docs/the-purpose.md` (Vulkan proven-scope wording), `docfx/backends/*` + `contributing/validation.md`,
  and `CHANGELOG.md`.

---

## What was implemented (2026-07-22)

Everything below is on the working branch, with `dotnet test ShadowDusk.slnx` green
(**2,196 tests, 0 failures**) and the two headline fixes **mutation-verified** (reverting either
one turns its regression test red; restoring it turns it green).

### The two reported bugs

| Fix | Change | Proof |
|---|---|---|
| **F1 — matrix transpose** (symptom: nothing renders) | `DxcFlagBuilder` no longer passes `-Zpr` for `PlatformTarget.Vulkan`. OpenGL keeps it (its rewriter compensates, the issue-#70 fix); DirectX never uses DXC. | `Compile_Vulkan_PacksMatricesColumnMajorLikeMgfxc` asserts the shipped SPIR-V decorates the `$Globals` matrix `RowMajor` (= HLSL column-major, what mgfxc emits and what MonoGame uploads for). RED before, GREEN after. |
| **F2 — un-paired legacy sampler** (symptom: native AV) | `VulkanTextureSamplerBindingRewriter` rewritten: FxPreParser's synthesized `_SDTexture` declarations are paired like any other (the blanket exclusion was the bug), explicit `register(tN)`/`register(sN)` indices are **reserved** so an auto-assigned pair can never collide, an explicit register on either half fixes the pair's index, and every texture dimensionality + the `Texture2D<float4>` template form is matched. | `Compile_Vulkan_LegacyTex2DSource_EmitsOnlyCombinedImageSamplers` asserts every descriptor is `COMBINED_IMAGE_SAMPLER` at binding ≥ 32 with unique binding numbers. RED before, GREEN after. Plus 4 new rewriter unit tests. |

### The secondary divergences

| Fix | Change |
|---|---|
| **F3** — Vulkan VS attribute table | New `SpirvVertexInputReflector` recovers the inputs from the SPIR-V (`OpName` semantics + `Location`, matrix/array inputs expanded per location) and the pipeline writes the table for Vulkan vertex shaders. On the reporter's shader this now emits the **same 13 entries, same usage/index sequence** as mgfxc. |
| **F4** — `samplerSlots` mask | `VulkanShaderCodeWrapper` sets the sampler bit for a combined descriptor, as mgfxc does (`0x7` vs the previous `0x0` on apos-shapes). |
| **F5** — `-fspv-reflect` | Dropped from the Vulkan flags entirely. ShadowDusk reflects from core decorations + `OpName`, none of which need the Google extensions, so it ships the same clean module mgfxc does — in ONE compile rather than mgfxc's two. **Resolves Phase 32's open item 5.** |

### Three MORE defects the widened corpus found immediately

| # | Defect | Fix |
|---|---|---|
| 1 | **Anonymous techniques rejected.** `technique { pass { … } }` (no name) is legal FX and is what most of MonoGame's own test effects use; ShadowDusk raised `FX0001: Expected technique name`. | `FxPreParser.ParseTechnique` accepts it, writing an **empty** name — verified against mgfxc 3.8.5, which compiles these and does exactly that. Unlocked 5 more fixtures on both DirectX_11 and Vulkan (`Instancing`, `TextureArrayEffect`, `ParserTest`, `CustomSpriteBatchEffect`, `CustomSpriteBatchEffectComparisonSampler`). |
| 2 | **Native process crash on the FNA path.** `SamplerComparisonState` made vkd3d's SM1 lowering log "Invalid dimension" then hit "Unreachable code reached" and take the whole process down with an `AccessViolationException` (exit 139). A crash gives the user nothing — a direct violation of the fail-loudly constraint. | New `FX0013` guard in `FxPreParser` (PreserveSm3 mode) rejects SM4+ resource types before vkd3d sees the source. |
| 3 | **`TextureCube` / `Texture3D` were never paired on Vulkan** — the rewriter only matched `Texture2D`, so cube/volume shaders emitted a standalone `SAMPLED_IMAGE` at a low binding: the same shape that access-violates. Caught by the corpus-wide gate on `examples/ExCubeSamplerHidef.fx` and `ExVolumeTextureHidef.fx`. | The rewriter now matches every texture dimensionality (and the `sampler S;` shorthand MonoGame's macro layer declares). |

### The corpus and the gate (F7/F8)

- **`VulkanCorpusStructuralTests`** — a **device-free, corpus-wide** gate: every `.fx` fixture is a
  test case, compiled for Vulkan and required to either produce a structurally valid container
  (combined descriptors at binding ≥ 32, unique bindings, uniform buffer at 0, column-major
  matrices, `main` entry point, no `SPV_GOOGLE_*`) **or fail with a real diagnostic** — never an
  exception, never a crash. There is no skip list, and a `TheCorpusIsActuallyBeingEnumerated`
  guard fails if the enumeration ever silently shrinks. **140 cases, all green.** Both bugs in
  this issue are detectable by it with no GPU.
- **MonoGame's own test effects vendored** (`tests/fixtures/shaders/third-party/MonoGame/`,
  Ms-PL, tag `v3.8.5`): 17 `.fx` + 2 `.fxh`, including `Instancing.fx` (VS-driven with a
  `float4x4` vertex input), `VertexTextureEffect.fx`, `ParameterTypes.fx`, `TextureArrayEffect.fx`
  and the reference compiler's own `ParserTest`/`PreprocessorTest`/`DefinesTest`. Upstream's
  `Vulkan.mgcb` / `DirectX.mgcb` state which effects each backend must compile, so the corpus now
  carries the reference compiler's own acceptance set. Per-file measured status and every
  non-compile reason are recorded in that directory's `NOTICE.md`.

### Still open — what this change does NOT prove

- **The real DesktopVK render.** The container now matches mgfxc field-for-field on the
  reporter's shader, but the project bar is the real runtime. `validation/CandidateVulkan` covers
  only the PS-only corpus; a **VS-driven Vulkan render driver** (F6.1) is still the missing proof,
  and until it runs on a real device #145 should not be closed on the byte diff alone.
- **F6.2–F6.4** — apos-shapes into the Vulkan render corpus, refreshing the vendored fixture from
  the stale `3fb73b8` pin, un-gating Vulkan in `run-windows-render-gates.ps1`, and regenerating
  the Vulkan goldens from explicit-register fixtures to unlock a true pixel A/B against mgfxc.
- **S4** (the extra sampler-named `Texture` parameters) — deliberately untouched pending a check
  of whether that code is Vulkan-specific or shared with the rung-4-proven GL/DX writers.
- **FNA + anonymous techniques.** Now that they parse, the FNA validator rejects them with
  `SD0302` ("empty or non-ASCII technique name"). Whether `fxc /T fx_2_0` accepts an unnamed
  technique was not tested; if it does, that validator should be relaxed.

---

## Second pass (2026-07-22, later): the render proof and the remaining items

### F6.1 + F6.4 — the VS-driven Vulkan render gate, WITH a reference-compiler oracle ✅

`validation/VsDrivenVulkan` renders `VsTransformColorTexture.fx` on a real MonoGame 3.8.5
DesktopVK device through the custom vertex-buffer path (`VsEffectImageRenderer`), which uploads a
**non-identity asymmetric matrix** — the issue-#70 input discipline, since an identity matrix is
transpose-invariant and cannot detect this bug class at all — and pixel-diffs ShadowDusk's output
against the **real `dotnet-mgfxc 3.8.5` golden** in-process.

```
[vs-vulkan] load + render results:
  [OK  ] baseline-mgfxc   …/baseline-mgfxc.png
  [OK  ] candidate-sd     …/candidate-sd.png

[vs-vulkan] baseline-vs-candidate maxd: 0
[vs-vulkan] candidate drew visible content: True
[vs-vulkan] verdict: PASS
```

**maxd 0 against the reference compiler, on the real runtime.** That is rung 4 with an oracle —
and it is more than Phase 32 could achieve, which is the point of the next paragraph.

**Mutation-proven.** Restoring `-Zpr` for Vulkan and re-running turns the gate red at
**maxd 255** — the maximum possible divergence. The gate demonstrably catches the exact bug the
issue reported.

**How the oracle was unblocked.** Phase 32 recorded that no Vulkan pixel-diff was possible because
mgfxc's own output crashes: its `SlotOffset` arithmetic computes a texture slot as
`rawBinding - 32`, and only explicitly-annotated registers are shifted there, so AUTO-numbered
resources underflow to 224/225 and the container is unloadable. The fix is on the FIXTURE side, not
the compiler: giving the fixture's `#if VULKAN` branch **matching explicit registers**
(`register(t0)` / `register(s0)`) keeps mgfxc's arithmetic in range. With that, both compilers emit
the identical binding table, and mgfxc's golden loads and renders — so it can be diffed against.
This is the general recipe for extending the Vulkan oracle to more fixtures.

The container A/B for this fixture is now exact on every load-bearing field:

| Field | mgfxc 3.8.5 | ShadowDusk |
|---|---|---|
| VS bindings | `[(0, UBO_DYN, VERTEX)]` | identical |
| PS bindings | `[(32, COMBINED, FRAGMENT)]` | identical |
| PS slot masks | `textureSlots 0x1`, `samplerSlots 0x1` | identical |
| VS attributes | 3 — Position/0, Color/0, TexCoord/0 | identical |
| matrix decoration | SPIR-V `RowMajor` | identical |
| **rendered pixels** | — | **maxd 0** |

### F6.3 — the Vulkan gates are DEFAULT-ON ✅

`validation/run-windows-render-gates.ps1` now runs both Vulkan gates (the PS corpus and the new
VS-driven oracle) without a switch, following the 2026-07-19 precedent that folded in KNI-GL and
the ANGLE probe so a change cannot rely on someone remembering a flag. `-IncludeVulkan` is kept as
an accepted no-op for compatibility; `-SkipVulkan` is the new escape hatch for a box with no
Vulkan-capable GPU. `CLAUDE.md`'s HARD RULE block is updated to match.

### The FNA anonymous-technique question — ANSWERED and fixed ✅

The doc left open whether `fxc /T fx_2_0` accepts an unnamed technique. It does: compiling
`technique { pass { PixelShader = compile ps_2_0 PS(); } }` through `d3dcompiler_47` at `fx_2_0`
returns **S_OK** with only the usual `X4717` deprecation warning. ShadowDusk's `SD0302` was
therefore stricter than the reference compiler; it now rejects only a **non-ASCII** technique name.
Four more of MonoGame's own effects (`CustomSpriteBatchEffect`, `Instancing`, `ParserTest`,
`VertexTextureEffect`) compile on FNA as a result.

### S4 — the extra sampler parameters: MEASURED, deliberately not changed

The question was whether the three extra `Texture`-class parameters (named after the samplers) come
from Vulkan-specific code or from the writer shared with the rung-4-proven GL/DX paths. Measured
both ways:

- DirectX, legacy-sampler source (`MultiTexture.fx`): ShadowDusk emits 2 parameters, mgfxc's golden
  emits 2 — **no extras**.
- DirectX, modern-sampler source (`ExModernSample.fx`): ShadowDusk emits 2 (`TintColor`,
  `SpriteTexture`) — **no sampler parameter**.

So the extras are **Vulkan-path only**, and they are harmless: they land at the END of the
parameter list, so they shift no index; the per-sampler records still point at the correct TEXTURE
parameter (verified 4/5/6 on apos-shapes); and every render gate is green with them present. The
only real effect is that an app enumerating `effect.Parameters` sees three entries mgfxc would not
emit. **Left as-is on purpose:** parameter indices are referenced by both the sampler records and
the cbuffer parameter-index table, so trimming the list is an index-shifting change that deserves
its own focused pass rather than a late edit here.

### One more divergence found while measuring S4 (recorded, not fixed)

On the **DirectX** path a legacy `sampler s0;` produces a parameter named **`s0_SDTexture`**, where
mgfxc's golden names it **`s0`** — FxPreParser's synthesized texture name leaks into the
user-visible parameter table. Binding still works (MonoGame's `SetShaderSamplers` uses the
parameter INDEX from the sampler record, not the name), but
`effect.Parameters["s0"].SetValue(texture)` would find nothing where it works against mgfxc's
output. This is long-shipping behaviour on a rung-4-proven path, unrelated to #145, and is recorded
here rather than changed blind.

### F6.2 — the reporter's own shader, vendored and RENDER-PROVEN ✅

`tests/fixtures/shaders/third-party/Apos.Shapes/apos-shapes-sm6.fx` vendors upstream Apos.Shapes at
commit `ea38c6d8` — the current revision, with the `#elif SM6` branch the Vulkan target selects.
It is added ALONGSIDE the existing two pins rather than replacing them, because each pins a distinct
regression shape: `3fb73b8` is the legacy `sampler`/`tex2D` revision (bug 2's crash shape),
`d507a734` is the issue-#136 derivative pin, and `ea38c6d8` is the issue-#145 reproducer.

`validation/VsDrivenVulkan -- apos` renders it on a real MonoGame 3.8.5 DesktopVK device through a
bespoke renderer for its 13-element vertex layout (`POSITION0`, `TEXCOORD0-9`, `POSITION1`,
`NORMAL0`), with the same non-identity asymmetric `view_projection`, and pixel-diffs against the
checked-in mgfxc 3.8.5 golden:

```
[apos-vulkan] load + render results:
  [OK  ] baseline-mgfxc   rendered
  [OK  ] candidate-sd     rendered
[apos-vulkan] baseline-vs-candidate maxd: 0
[apos-vulkan] candidate drew visible content: True
[apos-vulkan] phase 2 (issue #145 reproducer): PASS
```

**The exact shader from the issue now loads, draws, and is pixel-identical to the reference
compiler on the reporter's runtime** — and mutation-proven: restoring `-Zpr` turns it red at
**maxd 255**.

Two implementation notes worth keeping:

- **Each phase is its own process.** DesktopVK does not survive a second `GraphicsDevice` in one
  process — creating a second `Game` after the first is disposed access-violates in the native
  Vulkan teardown/re-init. The gate script runs the driver twice.
- **Every sampler slot must be bound**, even ones the draw never samples:
  `MGVK_UpdateDescriptors` dereferences `device->samplers[stage][slot]->sampler` for every
  `COMBINED_IMAGE_SAMPLER` with no null check, so an unbound slot is a native null-deref. The
  renderer binds all three of this effect's textures.

Compile status for the new fixture (measured): OpenGL OK, DirectX_11 OK, Vulkan OK; FNA fails
`E5017` — the dense pixel shader exceeds what vkd3d lowers to `fx_2_0`/SM3, the same honest
shader-model ceiling the other Apos revisions hit.

### Still open

- **The GL macro-model gap** — every MonoGame test effect fails on OpenGL because `Include.fxh`
  takes its legacy DX9 branch (ShadowDusk's GL macro set has no shader-model macro) and the
  `sampler2D` inside a macro body is invisible to FxPreParser's converter. Known Phase-41 gap, now
  with 17 reproducible fixtures behind it.

---

## CI finding: the macOS test host crashes under the whole-corpus DXC sweep

Adding `VulkanCorpusStructuralTests` (139 fixtures compiled through DXC for Vulkan) turns the
**macOS** integration lane red with `The active test run was aborted. Reason: Test host process
crashed`. Established, not assumed:

- **It is this branch, not flake.** The macOS integration job is green on the last five `main`
  runs; it fails on this branch, and failed again on a targeted job re-run.
- **No test fails.** Pulling the TRX artifact shows every completed case PASSED, including all
  141 corpus cases in the first run. The host dies underneath the suite.
- **The crash point moves.** First run: 605 tests completed. Re-run: 158. Both crashed about
  **11 seconds** into the `Integration.Tests` assembly. A fixed time with a wildly different
  test count is the signature of **resource exhaustion under the parallel suite**, not one poison
  fixture.
- **Two hypotheses were checked and ruled out.** (a) A leaked native DXC compiler per compile:
  `CompilationPipeline.Run` already disposes it deterministically in a `finally`. (b) A race in
  the macOS dylib resolver: `DxcLoader.Register` is properly locked and already carries a comment
  about a previous intermittent macOS `DllNotFoundException` under test parallelism.

**First attempt, and why it was wrong.** Gating the corpus sweep off macOS did NOT fix the lane:
it failed again with the sweep skipped. So the sweep was never the trigger. The real driver is
the **enlarged corpus itself** — the 18 new fixtures also feed the pre-existing corpus-globbing
suites (`Phase41StructuralDivergenceMatrixTests` compiles every fixture for DX *and* GL,
`ThirdPartyShaderCorpusTests`, `FnaCompileFixtureTests`), so total native compilation went up
across the whole assembly regardless of the new class. Worth recording: the obvious suspect was
not the culprit, and skipping it would have shipped a coverage loss that bought nothing.

**Resolution taken:** cap xUnit's cross-collection parallelism to 1 **on the macOS lane only**
(`-- xUnit.MaxParallelThreads=1` in `ci.yml`), and run the corpus sweep on every platform. Linux
and Windows keep full parallelism. This targets the actual mechanism (concurrent native
compilation, not any one fixture) and keeps macOS coverage intact.

**Follow-up (not this issue):** root-cause why concurrent DXC compilation destabilises the macOS
test host at all. Likely candidates are peak memory across parallel xUnit collections (the corpus
includes large SM6 shaders such as `apos-shapes-sm6.fx`, whose PS SPIR-V alone is ~136 KB) and
DXC's behaviour on the Rosetta-2 x64 runners GitHub provides. Worth fixing on its own merits: it
bounds how much native compilation the integration suite can ever do on macOS.