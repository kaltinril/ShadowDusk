Fixtures for the global-initializer default tests (`InitializerDefaultsTests`) and the real-runtime
render drivers (`validation/InitializerDefaults*`).

`A_Tint` ... `F_NoInit` are the probe shapes: float4, scalar, float2 plus float2x2, int plus bool,
float4[2], and a control with no initializer. `mgfxc-3.8.4.1-DirectX_11/` holds what the real
`mgfxc` 3.8.4.1 `/Profile:DirectX_11` emits for each (its default blobs are the reference the
scalar, vector, int and bool blobs are compared to byte for byte; its float2x2 and array blobs are
the known-lossy ones ShadowDusk deliberately does not copy). mgfxc's OpenGL build writes zeros for
all of them, so there is no OpenGL golden to compare.

`G_PackedFloat3` (issue #425) is a `float3` that HLSL packing puts behind a `float` in one
register, followed by a `float4x4`, the shape that used to fail the OpenGL compile with `SD0100`.
It has no `mgfxc` golden; its pixel tells a misplaced member apart.
