Fixtures for the global-initializer default tests (`InitializerDefaultsTests`) and the real-runtime
render drivers (`validation/InitializerDefaults*`).

`A_Tint` ... `F_NoInit` are the probe shapes: float4, scalar, float2 plus float2x2, int plus bool,
float4[2], and a control with no initializer. `G_NonSquare` is a float2x3 plus a float3x2 with
every element distinct (issue #423, off GL). `H_RowMajorMatrix` and `I_PackMatrixPragma` are
`MatrixDefault` declared `row_major` and under `#pragma pack_matrix(row_major)`, and `J_RowMajor2x2`
a `row_major float2x2` (off GL): their default blob is the rows in order (issue #423 follow-up). `mgfxc-3.8.4.1-DirectX_11/` holds what the real
`mgfxc` 3.8.4.1 `/Profile:DirectX_11` emits for each (its default blobs are the reference the
scalar, vector, int and bool blobs are compared to byte for byte; its float2x2 and array blobs are
the known-lossy ones ShadowDusk deliberately does not copy). mgfxc's OpenGL build writes zeros for
all of them, so there is no OpenGL golden to compare.
