# Metal (future — not implemented)

> **Status: stub / not yet implemented.** Metal is **not** a shipping backend. Do not expect `.fx` → MSL compilation to work today.

The intended Metal path (macOS / iOS) would reuse the faithful pipeline through SPIRV-Cross's MSL emitter:

```text
HLSL → DXC → SPIR-V → SPIRV-Cross → MSL (Metal Shading Language)
```

This is the same shape as the OpenGL path, swapping SPIRV-Cross's GLSL target for its MSL target.

## Current state

`ShadowDusk.Metal` exists only as a **stub**: `MslEmitter` is an empty `public sealed class MslEmitter { }`. There is no working emitter, no reflection, and no MGFX integration for Metal. The project is **excluded from the API reference** for this reason.

## When implemented, it would be additive

Per the project's backward-compatibility policy, Metal will arrive as an **additive opt-in target** (`PlatformTarget.Metal`) — it will not change the existing OpenGL or DirectX output or the MGFX v10 format. Until then, MonoGame's Metal backend is out of ShadowDusk's reach; use the OpenGL path on macOS.
