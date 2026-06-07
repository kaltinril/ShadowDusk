# Vulkan (future)

> **Status: future.** A first-class Vulkan target is not yet a validated shipping backend.

Vulkan consumes **SPIR-V** directly, which is convenient because the [faithful pipeline](../architecture/the-faithful-pipeline.md) already produces SPIR-V as its intermediate:

```text
HLSL → DXC → SPIR-V   (consumed directly by Vulkan / SM6 paths)
```

## Current state

The CLI and `PlatformTarget` accept a **`Vulkan`** profile, and DXC's `ps_6_0`/`vs_6_0` (SM6) output is what feeds the Vulkan / DX12-KNI path. However, end-to-end Vulkan validation in a real MonoGame/KNI runtime is **not** part of the proven corpus (which covers OpenGL, DirectX DX11, and KNI WebGL). Treat Vulkan as a forward-looking target rather than a guaranteed-equivalent one.

## Additive by policy

Like all new backends, Vulkan support will be **additive opt-in** and will not change the existing OpenGL/DX11/v10 output.
