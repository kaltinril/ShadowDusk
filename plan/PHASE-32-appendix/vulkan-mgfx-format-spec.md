# The real Vulkan `.mgfx` container — source-grounded spec

**Status:** authoritative, read directly from MonoGame's own source (not reverse-engineered by
hex-diffing). Source: `github.com/MonoGame/MonoGame`, tag `v3.8.5`,
`Tools/MonoGame.Effect.Compiler/Effect/{ShaderProfile.Vulkan.cs, EffectObject.writer.cs,
ShaderData.writer.cs, ConstantBufferData.writer.cs, ConstantBufferData.Vulkan.cs}`.

## Final status (2026-07-18): rung-4 render-proven, 10/10

ShadowDusk's own Vulkan output renders correctly on real MonoGame 3.8.5 DesktopVK for the full
10-shader corpus (`validation/CandidateVulkan`) — visually confirmed for representative samples
(grayscale, sepia, tint), not merely "doesn't crash." Getting there required root-causing and
fixing three distinct, real ShadowDusk-side bugs, each isolated via a demonstrated native crash
before the fix (a real `AccessViolationException`/`IndexOutOfRangeException` in MonoGame's
native Vulkan draw path, or a red regression test) — see the dated subsections below for the
full investigation:

1. **Combined descriptor, not separate** (§ *Root cause isolated via minimal repro*) — a
   texture+sampler pair used together must be ONE `COMBINED_IMAGE_SAMPLER` descriptor, not two.
   Fixed by `VulkanTextureSamplerBindingRewriter` (forces paired declarations onto matching
   explicit registers so `-fvk-t-shift`/`-fvk-s-shift` co-locate them) plus
   `VulkanShaderCodeWrapper`'s combined-detection logic (raw-binding equality, not SPIR-V type).
2. **Entry point must be named `"main"`** (§ *Root cause fully confirmed and fixed: the entry
   point name*) — MonoGame's native Vulkan pipeline creation hardcodes this expectation, mirroring
   real mgfxc's own behavior. Fixed by `RenameEntryPointToMain` in `CompilationPipeline`, Vulkan-only.
3. **The implicit `$Globals` cbuffer must bind to 0** (§ *Third root cause: the implicit `$Globals`
   cbuffer's raw binding*) — DXC auto-numbers it wherever, and the native pipeline hardcodes 0.
   Fixed with DXC's own `-fvk-bind-globals` flag in `DxcFlagBuilder`, no source rewrite needed.

Full regression suite green throughout (whole `dotnet test ShadowDusk.slnx`, ~1970 tests). The
one gap that remains, not a ShadowDusk defect: real mgfxc's own compiled Vulkan output crashes on
all 10 corpus shaders in real DesktopVK (a separate, confirmed MonoGame bug — the `SlotOffset`
arithmetic wraparound, § *An apparent real-mgfxc edge case* / § *Step 8 render attempt*), so a
literal pixel-diff against the reference compiler's own output isn't currently possible — ShadowDusk's
own render correctness is the evidence tier reached, `validation/compare_vulkan.py` reports this
plainly rather than masking it.

## Headline finding

**The container is structurally identical to ShadowDusk's existing MGFX v10/v11 shape in every
section — header, constant buffers, parameters, techniques, render-state blocks, and even the
per-shader sampler/cbuffer-index/attribute tables — except for two things:** the profile byte
value, and what the per-shader "bytecode" field actually contains for a Vulkan shader. This is a
much smaller change than `plan/PHASE-32-vulkan-backend.md`'s original "provisional placeholder"
framing (or this phase's initial planning pass, which assumed a wholly separate sibling writer
in the `KnifxWriter` shape) — **no new sibling writer class is needed.**

## Header

`"MGFX"`(4) + version(1 byte) + profile(1 byte) + effectKey(int32).

- **Profile byte is `80`**, not `3`. Straight from source: `VulkanShaderProfile() : base("Vulkan", 80)`.
  `MgfxProfile.Vulkan` in `src/ShadowDusk.Core/MgfxProfile.cs` must change from `3` to `80`.
- **Version is hardcoded to `11` for every profile as of MonoGame 3.8.5**
  (`internal const int Version = 11;` in `EffectObject.writer.cs`, not gated by target/profile).
  This means the per-shader record always includes `SourceFile`/`Entrypoint` (see below) — not
  opt-in the way ShadowDusk's v10/v11 split works today. Since DesktopVK is new in 3.8.5 with no
  prior-version reader to preserve compatibility with, ShadowDusk's Vulkan writer should force the
  v11 shader-record shape unconditionally, regardless of `CompilerOptions.MgfxVersion`.
- effectKey is FNV-1a + an avalanche mix of the body bytes (`ComputeHash` in `EffectObject.writer.cs`),
  not MD5. This doesn't matter for correctness — confirmed elsewhere that the reader never
  validates this value, it's a cache key only — so ShadowDusk's existing MD5-based effectKey stays
  fine for Vulkan too.

## Body order (identical to existing v10/v11): ConstantBuffers → Shaders → Parameters → Techniques

### ConstantBufferData.Write — byte-identical to ShadowDusk's `ConstantBufferInfo`/`WriteConstantBuffers`

```
Name                 string
Size                 ushort
ParameterIndex.Count int32
per param (interleaved): ParameterIndex int32, ParameterOffset ushort
```

**Constraint (real, enforced by mgfxc):** Vulkan supports at most **one** constant-buffer struct
per shader stage. `VulkanShaderProfile.CreateShader` throws if a second cbuffer-typed descriptor
variable is found: *"Building effects for Vulkan currently doesn't support more than one constant
buffer (cbuffer) structures."* ShadowDusk's Vulkan compile must enforce the same (fail loudly with
a new `SD00xx`) rather than silently emit a second cbuffer the real container can't represent.

### ShaderData.Write — byte-identical shape to ShadowDusk's existing per-shader record

```
IsVertexShader   bool
SourceFile       string   (always written in 3.8.5, unconditional)
Entrypoint       string   (always written in 3.8.5, unconditional)
ShaderCode.Length int32
ShaderCode        bytes    <-- THE ONE REAL DEVIATION, see below
samplerCount     byte
  per sampler: Type byte, TextureSlot byte, SamplerSlot byte, hasState bool [+ state fields], Name string, Parameter byte
cbufferCount     byte
  per index: byte
attributeCount   byte
  per attribute: Name string, Usage byte, Index byte, Location short
```

Every field here — including the sampler record (`Type/TextureSlot/SamplerSlot/hasState/.../Name/Parameter`,
exactly matching `MgfxSamplerInfo`), the cbuffer-index list, and the attribute table (exactly
matching `MgfxVertexAttributeInfo`) — is identical to what `MgfxWriter.WriteShaders` already
writes for GL/DX. **No new IR types are needed for these.**

### The one real deviation: `ShaderCode` for Vulkan is not raw SPIR-V

For GL/DX, `ShaderCode`/`Bytecode` is the raw compiled bytes. For Vulkan,
`VulkanShaderProfile.CreateShader` builds a **Vulkan descriptor-layout-prefixed wrapper** around
the SPIR-V before it's ever handed to `ShaderData.Write`:

```
uniformBufferCount     int32     (cbufferIndex.Count — 0 or 1)
uniformSlotsBitmask    uint32
textureSlotsBitmask    uint32
samplerSlotsBitmask    uint32
textureTypes[16]       uint32 each (64 bytes total; MGTextureType per texture slot)
bindingCount           uint32
per binding:
  binding              uint32   (the real Vulkan descriptor binding number, SlotOffset-shifted, see below)
  descriptorType       uint32   (VkDescriptorType — UNIFORM_BUFFER_DYNAMIC / COMBINED_IMAGE_SAMPLER / SAMPLER / SAMPLED_IMAGE)
  descriptorCount      uint32   (always 1)
  stageFlags           uint32   (VkShaderStageFlags — VERTEX_BIT or FRAGMENT_BIT)
  pImmutableSamplers   uint64   (always 0)
<raw SPIR-V bytecode, appended verbatim>
```

This wrapper is what `ShaderData.ShaderCode` actually is for a Vulkan shader — it's what gets
length-prefixed and written into the `ShaderCode.Length` + `ShaderCode` field above. This exactly
explains why `validation/decode_mgfx.py` (which assumes the shader blob is raw SPIR-V immediately
followed by the sampler table) crashed partway through the shader entry this session: the real
"bytecode" is this whole prefixed blob, several dozen bytes longer than the SPIR-V module alone,
so every field after it in the naive decode is misaligned.

### `-fvk-t-shift 32 all` / `-fvk-s-shift 32 all` — a DXC flag ShadowDusk's `DxcFlagBuilder` is missing

Real mgfxc's Vulkan DXC invocation (`ShaderProfile.Vulkan.cs`, `CreateShader`):

```
-nologo -spirv -fvk-use-dx-layout -fspv-reflect
  [VS only] -fvk-invert-y -fvk-use-dx-position-w
  [PS only] -auto-binding-space 1
  -fvk-t-shift 32 all -fvk-s-shift 32 all
  -T {vs|ps}_6_0 -E main
```

`SlotOffset = 32` shifts every texture (`t`) and sampler (`s`) HLSL register up by 32 before DXC's
auto-binding assigns SPIR-V `Binding` decorations, so texture/sampler bindings never collide with
the uniform-buffer binding (always `0`) or across the VS/PS descriptor-space split. The reflection
code then subtracts `SlotOffset` back off (`samplerVariable.BindingSlot.Value - SlotOffset`) to
recover the real 0-based texture/sampler slot for the `MgfxSamplerInfo`-shaped record, and adds it
back when constructing the `VkDescriptorSetLayoutBinding` entries above.

**ShadowDusk's current `DxcFlagBuilder` Vulkan case has neither `-fvk-t-shift` nor `-fvk-s-shift`.**
This needs adding for Step 3/4.

### Two-pass compile (real mgfxc does this; ShadowDusk currently does one pass)

Real mgfxc compiles **twice**: once with `-fspv-reflect` (to get a `.reflect` text dump via `-Fc`
for reflection parsing), then a second time **without** `-fspv-reflect` for the actual shipped
bytecode — because `-fspv-reflect` "forces Google VK extensions into the binary" the comment says
should not ship. ShadowDusk's `DxcCompiler` currently does one compile with `-fspv-reflect` always
on and ships that SPIR-V directly. Whether this actually breaks anything in MonoGame's Vulkan
loader (vs. just being defensive/unnecessary bytes) is an open question for Step 3/4 to resolve
empirically — worth testing both ways before committing to reproducing the two-pass compile.

### Parameters / Techniques / render-state blocks

Confirmed byte-identical in field order to ShadowDusk's existing `WriteParameters`/`WriteParameter`
and `WriteTechniques`/`WriteRenderStateBlock`: `Class, Type, Name, Semantic, Annotations(count-only
in practice since mgfxc never emits populated annotations), Rows, Columns, Elements, Members,
[raw leaf bytes]` for parameters; `Name, Annotations, VertexShaderIndex, PixelShaderIndex,
Blend/DepthStencil/Rasterizer state blocks` for technique passes. **Render state IS still baked
into the effect for Vulkan** (not externally managed via a `VkPipeline` object) — resolves the
open question from the original plan. `MgfxWriter.WriteRenderStateBlock` is directly reusable,
unchanged.

## Empirical validation (2026-07-18)

The spec above was cross-checked against 10 real `dotnet-mgfxc 3.8.5 /Profile:Vulkan` goldens
(the existing PS-only corpus, each fixture given an `#if VULKAN` branch at `vs_6_0`/`ps_6_0` with
modern `Texture2D`/`SamplerState`/`.Sample()`/`SV_Target` syntax — legacy `sampler2D`/`tex2D`/
`COLOR` is rejected by DXC at SM6, same requirement Vulkan and the real mgfxc share). All 10
decode **cleanly to the trailing footer with zero leftover bytes** via
`validation/decode_mgfx_vulkan.py`, confirming the spec is exactly right.

**One quirk worth flagging so a future implementer doesn't "fix" it:** the legacy per-sampler
`TextureSlot`/`SamplerSlot` byte fields (still written for every profile by `ShaderData.Write`)
come out as **byte-underflow-wrapped values** for Vulkan, e.g. `224`/`225` instead of `0`/`1` —
`(byte)(0 - 32)` and `(byte)(1 - 32)` wrap to `224`/`225` in real mgfxc's own
`textureSlot = (int)imageVariable.BindingSlot.Value - SlotOffset` when the raw DXC-assigned
binding happens to be smaller than `SlotOffset (32)`. This is exactly what real mgfxc emits, not a
bug — the actual Vulkan descriptor binding comes from the `vkLayout.bindings` table inside
`ShaderCode`, not these two legacy bytes, which appear to be structural leftovers from the
shared `ShaderData.Write` method rather than something MonoGame's Vulkan loader reads for binding.
ShadowDusk's own writer does not need to reproduce the exact wrapped values (its `SpirvReflector`
does clean 0-based class-relative renumbering, not raw DXC binding arithmetic) as long as the real
`vkLayout.bindings` table it constructs is correct — but this is **unverified** until a real
DesktopVK `Effect` load+render proves the legacy bytes are truly unused (Step 6/8).

## An apparent real-mgfxc edge case (open question, does not block ShadowDusk)

Decoding `Sepia.mgfx` (a shader with both a cbuffer-packed global AND a texture/sampler pair)
shows `vkLayout.bindings = [(0, 8, 1, 16, 0)]` — **only the cbuffer's binding, none for the
texture/sampler**, even though `samplers: count=1` lists `s0`. Tracing why: `s.textureSlot`/
`s.samplerSlot` in `ShaderProfile.Vulkan.cs` are computed as `rawBinding - SlotOffset(32)`, and the
binding-table generator only adds a `SAMPLED_IMAGE`/`SAMPLER` entry `if (s.textureSlot > 0)` /
`if (s.samplerSlot > 0)`. For my auto-numbered (no explicit `: register(tN)`) `Texture2D`/
`SamplerState` declarations, the wrapped legacy bytes (`224`/`225`) imply the raw SPIR-V bindings
DXC actually assigned were **0 and 1** — meaning `-fvk-t-shift 32 all`/`-fvk-s-shift 32 all`
apparently did **not** shift them (those flags likely only remap *explicit* HLSL register
annotations, not DXC's auto-assigned implicit ones). With `rawBinding - 32` landing at `-32`/`-31`,
the `> 0` check is false for both, and neither binding is written.

**This looks like a real gap in mgfxc 3.8.5's own Vulkan writer for auto-numbered resources at
low slots, not something to reproduce.** Two hypotheses, untested: (a) it's a genuine bug and
such shaders quietly fail to bind their texture at runtime, or (b) MonoGame's Vulkan `Effect`
loader reflects descriptor bindings directly from the SPIR-V's own decorations at load time
(the wrapper table only matters for the uniform-buffer-dynamic offset, hence why it's
`UNIFORM_BUFFER_DYNAMIC` specifically), making the wrapper's texture/sampler entries advisory
rather than load-bearing. **ShadowDusk should not copy the `> 0` bug either way** — build a
complete, correct binding table from its own reflection regardless of slot number. Step 8's real
DesktopVK render is the actual tie-breaker for which hypothesis is true; until then this is
documented as an open question, not a blocker.

## Step 8 render attempt (2026-07-18): both sides fail to draw, for different reasons

Built `validation/BaselineVulkan` (loads the real `dotnet-mgfxc 3.8.5` goldens) and
`validation/CandidateVulkan` (compiles the same corpus via ShadowDusk) against the real,
stable DesktopVK runtime confirmed earlier this session. Neither renders successfully yet:

- **Baseline (real mgfxc goldens) crashes at the MANAGED level**: `TextureCollection.set_Item`
  throws `IndexOutOfRangeException` inside `EffectPass.Apply` → `SetShaderSamplers`. This
  confirms the legacy `TextureSlot`/`SamplerSlot` byte fields are **not** vestigial as
  hypothesized earlier — MonoGame's shared managed `Effect`/`EffectPass` code (used by every
  backend, GL/DX/Vulkan alike) uses them to index a fixed-size `TextureCollection` array. Real
  mgfxc's own underflow-wrapped values (224/225, from `rawBinding - 32` going negative — see
  the "apparent real-mgfxc edge case" section above) are genuinely out of range and crash. This
  is a real bug in mgfxc 3.8.5's own output for auto-numbered (non-explicit-register) resources,
  not something to reproduce.
- **Candidate (ShadowDusk's output) gets past that step** (its `TextureSlot`/`SamplerSlot` use
  the clean 0-based `BindSlot`, not an underflowed value) but crashes with a **native**
  `AccessViolationException` inside `MGG.GraphicsDevice_DrawIndexed`, on the very first
  (simplest) shader in the corpus (`Grayscale`, one texture + one sampler, no cbuffer). This is
  deeper in the native Vulkan pipeline-creation/draw path than anything reflection or the
  managed writer controls.

**Root cause not yet isolated.** No successful "real mgfxc renders correctly here" reference
point exists on this runtime to diff against (the baseline never gets past its own managed
crash), and this machine has no Vulkan SDK / validation layers installed
(`VULKAN_SDK` unset, no `VK_LAYER_KHRONOS_validation` registered) — only the driver's runtime
ICD — so the native crash currently surfaces as an opaque access violation instead of an
actionable Vulkan validation message. Plausible causes, unconfirmed: the wrapper's flat
`bindings` list has no descriptor-SET field (matching the real format) and relies on an
assumption about which set number MonoGame's native reader applies it to; DesktopVK is 3 days
old at time of writing and may simply not yet robustly support a custom Vulkan `Effect` through
`SpriteBatch`; or something else in the SPIR-V/binding construction this session didn't surface.

**This means the render-proof (rung-4) is not achieved yet, on either side, for reasons
external to the compile-level correctness work (Steps 1-7, which stands fully tested and
green).** Installing the Vulkan SDK for validation-layer diagnostics is the natural next
investigative step but wasn't done without checking in first, given it's a real environment
change.

### Root cause isolated via minimal repro (2026-07-18, later)

Built a from-scratch minimal repro (no `EffectImageRenderer`, no ShadowDusk-corpus complexity):
a `Game` with a `GraphicsDeviceManager`, a 1x1 dummy `Texture2D`, and `SpriteBatch.Draw` with a
custom effect applied — as close to the exact failing call (`SpriteBatcher.FlushVertexArray` →
`GraphicsDevice.DrawUserIndexedPrimitives` → `MGG.GraphicsDevice_DrawIndexed`) as possible with
everything else stripped out. Bisected by resource shape, real-mgfxc-only first:

1. **Trivial constant-color PS, no resources at all, real mgfxc** → draws successfully. Rules
   out "DesktopVK / SpriteBatch / custom-Effect is broadly too immature" — the native path
   itself works fine.
2. **Texture + sampler, EXPLICIT `register(t0)`/`register(s0)`, real mgfxc** → the slot-index
   bug does NOT occur here (clean `texSlot=0`), and real mgfxc's `-fvk-t-shift`/`-fvk-s-shift`
   land both resources at the SAME raw binding (32), which its own code (correctly) reads as
   "same slot → combine" and emits **one `COMBINED_IMAGE_SAMPLER` binding**. **Draws
   successfully** — the first genuinely-working real-mgfxc Vulkan render observed this session.
3. **The exact same `.fx` source compiled by ShadowDusk** → also lands both resources at raw
   binding 32 (same DXC behavior), but ShadowDusk's reflector classifies them as **separate**
   resources (keyed off the SPIR-V type, `OpTypeSampledImage` vs `OpTypeImage`+`OpTypeSampler`,
   not off raw-binding equality), emitting **two** binding entries — `SAMPLED_IMAGE` and
   `SAMPLER`, **both at binding 32**. Loading this in the exact same minimal repro reproduces
   the identical `AccessViolationException` in `MGG.GraphicsDevice_DrawIndexed`.
4. Cross-checked against the original `Grayscale` crash (auto-numbered registers, genuinely
   **non-colliding** raw bindings 0 and 2): it crashes too, with two separate binding entries,
   no collision. So the common factor across both ShadowDusk crashes is **"separate
   `SAMPLED_IMAGE`+`SAMPLER` descriptor pair"**, not specifically the binding collision — the
   collision in case 3 is a second, compounding bug, not the sole cause.

**Verdict: this crash is very likely NOT a MonoGame bug — it's ShadowDusk's.** The only
observed *working* Vulkan texture render this session used a single **combined**
`COMBINED_IMAGE_SAMPLER` descriptor; every ShadowDusk-produced **separate**-descriptor pair
crashed, collision or not. Two candidate fixes, either of which may be needed: (a) ShadowDusk
should merge a texture+sampler pair into one combined descriptor when they're always used
together, matching what a working real-mgfxc compile produces for the same source; (b)
implement mgfxc's own two-pass compile (once **with** `-fspv-reflect` to gather metadata, once
**without** it for the actual shipped bytecode) — real mgfxc's comment says the reflect-enabled
compile "forces Google VK extensions into the binary" it doesn't want to ship, and the
reflect-*disabled* second compile may be exactly what produces DXC's legalized/combined SPIR-V
in the first place. ShadowDusk currently only does the one, reflect-enabled compile and ships
those bytes directly.

**Separately, the baseline crash (real mgfxc's own slot-index wraparound, `TextureSlot`/
`SamplerSlot` bytes going negative and byte-wrapping to 224/225) remains a genuine, confirmed
MonoGame/mgfxc bug**, independent of anything above — it reproduces with 100% real mgfxc output
and zero ShadowDusk involvement, and is a real, reportable issue in `VulkanShaderProfile
.CreateShader`'s `SlotOffset` arithmetic for auto-numbered (non-explicit-register) resources.

### Root cause fully confirmed and fixed (2026-07-18, later): the entry point name

The combined-descriptor fix above was necessary but **not sufficient** — a fixed-up `Grayscale`
(one combined descriptor, matching the working `trivial_tex` shape byte-for-byte in wrapper
structure) **still crashed** with the identical `AccessViolationException`. Byte-diffing the two
720-byte SPIR-V payloads (real mgfxc's working `trivial_tex.mgfx` vs. ShadowDusk's, both with one
combined descriptor) found **exactly 6 differing bytes, all inside the `OpEntryPoint`
instruction's name string**: mgfxc's shipped SPIR-V names its entry point literally **`"main"`**;
ShadowDusk's names it **`"MainPS"`** (the shader's real HLSL function name).

This is intentional on mgfxc's side — `VulkanShaderProfile.CreateShader` renames the target
function to `main` in the source text before compiling
(`Regex.Replace(shaderContent, entryFunctionPattern, "main")`) and compiles with `-E main`.
MonoGame's native Vulkan pipeline creation evidently expects the entry point to be named `main`
unconditionally (a common simplification for Vulkan-consuming wrappers) rather than reading the
real name from the module or from `ShaderData`. Shipping any other name is silently accepted by
DXC and produces perfectly valid SPIR-V, but crashes MonoGame's native interop at
`vkCreateGraphicsPipelines`-adjacent pipeline creation.

**Fix, mirrored exactly from mgfxc**: `CompilationPipeline.CompileEntryPoint`'s Vulkan branch now
renames the target entry point to `main` in the source (`RenameEntryPointToMain`, a
whitespace-then-`(`-bounded regex matching only the function definition/call, never a substring
of another identifier) and compiles with `-E main`, for both VS and PS, per invocation. Combined
with the earlier register-pairing (`VulkanTextureSamplerBindingRewriter`) and combined-descriptor
(`VulkanShaderCodeWrapper`) fixes, **the full 10-shader corpus now compiles AND renders correctly
via `validation/CandidateVulkan` on real DesktopVK — 10/10, visually confirmed correct (grayscale,
sepia, invert, dissolve, etc. all show the right effect on the cat photo), not merely
"doesn't crash."** This is genuine rung-4 evidence for ShadowDusk's own Vulkan output.

**Scope note**: this closes the crash for ShadowDusk's own compiler. The baseline (real mgfxc)
crash remains separately confirmed as mgfxc's own bug (the `SlotOffset` wraparound) and is
untouched by any of these fixes — a `validation/BaselineVulkan` run against real mgfxc goldens
would still need mgfxc's bug fixed upstream (or the goldens regenerated with explicit registers)
to render successfully.

### Third root cause, found on regression (2026-07-18, later still): the implicit `$Globals` cbuffer's raw binding

An unrelated fixture-scoping fix to `VulkanTextureSamplerBindingRewriter` (making it whole-file
instead of `#if VULKAN`-scoped, needed because several fixtures declare `Texture2D` unconditionally
with only the sampler differing per branch) required re-verifying the full 10-shader corpus.
9 of 10 rendered; **`Sepia.fx` crashed alone, in isolation**, with the identical
`AccessViolationException` in `GraphicsDevice_DrawIndexed`.

Structurally, `Sepia`'s compiled container looked identical in shape to `TintShader` (which
rendered fine): one combined texture/sampler descriptor plus one `$Globals` cbuffer holding a
single loose global (`float3 _sepiaTone` vs. `float4 TintColor`). Parsing the raw SPIR-V
`OpDecorate` instructions directly (bypassing ShadowDusk's own reflection entirely, to rule out a
reflection bug) showed the actual difference: `TintShader`'s `$Globals` decorated at
`Binding=0`; `Sepia`'s at `Binding=1`. Both landed in `DescriptorSet=1` (from `-auto-binding-space
1`, applied uniformly to every auto-bound resource including the implicit cbuffer — not itself a
bug, both shaders agree on this). Isolating the variable: manually wrapping `Sepia`'s loose
global in an explicit `cbuffer Globals : register(b0) { ... }` forced `Binding=0` and the crash
disappeared — confirming the raw binding *value* (not the descriptor shape, already fixed above)
was the trigger, and that MonoGame's native Vulkan pipeline hardcodes an expectation that the
constant buffer descriptor is bound at 0.

DXC has no control over where it auto-numbers an *implicit* cbuffer (unlike explicit
`register(t#)`/`register(s#)`, which `-fvk-t-shift`/`-fvk-s-shift` can pin) — but it does expose
exactly this case: **`-fvk-bind-globals <binding> <set>`**, a dedicated flag to pin the
auto-generated `$Globals` cbuffer's Vulkan binding/set, no source rewrite required. Applied to
`DxcFlagBuilder`'s Vulkan VS/PS cases (`-fvk-bind-globals 0 0` for VS, `-fvk-bind-globals 0 1` for
PS — matching each stage's already-established auto-binding-space so `$Globals` lands in the same
set as everything else auto-bound in that stage), the entire 10-shader corpus renders correctly
again, `Sepia` included, without touching any fixture source.

A synthetic hand-written repro shader with the same "texture/sampler declared before the loose
global" shape did **not** reproduce the crash — DXC's auto-numbering for the implicit cbuffer is
sensitive to more of the real preprocessed source than declaration order alone, and a shape that
looks equivalent on paper isn't guaranteed to trigger the same allocation. The regression test
(`VulkanEffectCompilerTests.Compile_Sepia_ImplicitGlobalsCbufferBindsToZero`) compiles the actual
`Sepia.fx` fixture through the real pipeline rather than a hand-built analog, and was confirmed RED
(`Binding=1`) with the flag removed before being confirmed GREEN with it restored.

## Revised implementation approach for Step 3/4 (supersedes the "new sibling writer class" call)

No new `EffectContainer` value, no new sibling writer class. Concretely:

1. Fix `MgfxProfile.Vulkan` from `3` to `80` (`src/ShadowDusk.Core/MgfxProfile.cs`).
2. Force the v11 shader-record shape (`SourceFile`/`Entrypoint` always written) for Vulkan
   compiles regardless of `CompilerOptions.MgfxVersion`.
3. Add a small helper (e.g. `VulkanShaderCodeWrapper` or similar, in `ShadowDusk.Core`) that,
   given the raw SPIR-V bytes plus the reflected cbuffer/sampler binding info, produces the
   descriptor-layout-prefixed blob above. Called only for `MgfxProfile.Vulkan`, right before the
   bytes are handed to `MgfxWriter.WriteShaders`'s existing generic bytecode-length+bytes field
   (i.e. the wrapping happens in `CompiledShaderBlob.Bytes` construction upstream of the writer,
   not inside the writer itself).
4. Add `-fvk-t-shift 32 all` / `-fvk-s-shift 32 all` to `DxcFlagBuilder`'s Vulkan VS/PS cases, plus
   `-fvk-bind-globals 0 <space>` (space matching each stage's auto-binding-space) to pin the
   implicit `$Globals` cbuffer's raw binding — otherwise DXC auto-numbers it wherever, and
   MonoGame's native Vulkan pipeline hardcodes an expectation that it's bound at 0.
5. Resolve the two-pass-compile question empirically (does shipping `-fspv-reflect`-decorated
   SPIR-V actually break MonoGame's Vulkan loader, or is stripping only precautionary).
6. Enforce (fail loudly, new `SD00xx`) that a Vulkan compile never has more than one constant
   buffer per shader stage — matches real mgfxc's own error rather than silently mis-emitting.
7. Extend `SpirvReflectionParser`/`SpirvReflector` to surface the raw `DescriptorSet`+`Binding`
   decorations (already collected internally, see the reflection-wiring plan) so the wrapper
   helper in (3) can compute the real texture/sampler slot (after undoing the `SlotOffset` shift)
   and the uniform/texture/sampler bitmasks + `VkDescriptorSetLayoutBinding` list.
