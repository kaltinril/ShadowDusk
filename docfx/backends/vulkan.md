# Vulkan

Vulkan consumes **SPIR-V** directly, which is convenient because the [faithful pipeline](../architecture/the-faithful-pipeline.md) already produces SPIR-V as its intermediate:

```text
HLSL → DXC → SPIR-V   (consumed directly by Vulkan)
```

## Current state

The CLI and `PlatformTarget` accept a **`Vulkan`** profile. Compiling targets MonoGame's `DesktopVK` platform: the `.mgfx` uses the real Vulkan container (profile byte `80`), with each shader's SPIR-V wrapped in the descriptor-layout header MonoGame's native Vulkan pipeline creation reads directly. ShadowDusk's own Vulkan output is validated end-to-end — it loads and renders correctly in a real MonoGame DesktopVK `Effect`, across the standard post-process corpus.

One caveat, now narrower than it used to be: a pixel comparison against MonoGame's own `mgfxc /Profile:Vulkan` output is possible **only for effects that declare explicit registers**. `mgfxc`'s compiled Vulkan output crashes on load in real DesktopVK for shaders using auto-numbered (non-explicit-register) resources — a MonoGame-side bug, independent of ShadowDusk, unrelated to anything this library emits. ShadowDusk's own output sidesteps it by construction. Where the comparison *can* run, it matches exactly (max Δ 0, including the real-world `Apos.Shapes` effect); where it can't, ShadowDusk's output is verified by rendering it in the real engine. Track MonoGame's `DesktopVK` runtime maturity if you rely on this target in production.

KNI does not ship a Vulkan platform, so this target is MonoGame-only.

A Vulkan `.mgfx` requires **at most one constant buffer per shader stage** — the same limit `mgfxc`'s own Vulkan writer enforces. ShadowDusk fails loudly (rather than mis-emitting) if a shader declares more than one (`SD0026`).

### Texture and sampler registers are assigned for you

Vulkan binds a texture and the sampler it is used with as **one combined descriptor**, so the two halves of a pair must sit on the same binding. A pair left to automatic numbering does not get that, and the mismatch crashes MonoGame's native Vulkan draw path rather than failing at compile time. On the Vulkan target only, ShadowDusk therefore assigns matching `register(tN)`/`register(sN)` to each texture/sampler pair it sees used together.

Registers you write yourself are kept wherever they can be. They are re-assigned only when honouring them would break the rules above — splitting a pair across two bindings, or putting two textures on one binding. If you read `effect.Parameters` by name this changes nothing; the runtime binds by slot index, never by the register number in your source. The rewrite applies to the Vulkan output alone: DirectX, OpenGL, and FNA bytes are untouched.

One shape to avoid: a **single sampler shared by two textures in the same code path**. Vulkan's combined-image-sampler model needs a distinct descriptor per texture, and ShadowDusk does not yet split that shape, so the second texture ends up unbound. Declare one sampler per texture.

## Additive by policy

Like all backends, targeting Vulkan is **opt-in per compile** (`PlatformTarget.Vulkan` / `/Profile:Vulkan`) and does not change OpenGL/DX11/v10 output for consumers who don't ask for it.
