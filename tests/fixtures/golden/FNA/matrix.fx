// Phase 40 fxc golden source: a square float4x4 parameter with a non-trivial
// default, used in PS arithmetic. The fxc-compiled matrix.fxb is the calibration
// artifact for the fx_2_0 matrix typedef encoding (dword5 = columns / dword6 =
// rows — the MojoShader read order) and the ground truth for the F2
// matrix-default question (fxc bakes the initializer; ShadowDusk deliberately
// bakes zeros until F2 is settled — the validation harness sets M explicitly so
// the rung-4 comparison stays fair).
//
// Compiled with the d3dcompiler_47 D3DCompile(pTarget: "fx_2_0") oracle — proven
// byte-identical to fxc.exe /T fx_2_0 (see README.md).

float4x4 M = { 1.0, 0.0, 0.0, 0.0,
               0.0, 1.0, 0.0, 0.0,
               0.0, 0.0, 1.0, 0.0,
               0.5, 0.25, 0.0, 1.0 };

float4 MainPS(float2 uv : TEXCOORD0) : COLOR0
{
    // Row-vector convention (mul(v, M)) so the matrix's translation row lands in
    // the output — a row/column-major mishandling visibly shifts the gradient.
    return mul(float4(uv, 0.0, 1.0), M);
}

technique T
{
    pass P
    {
        PixelShader = compile ps_2_0 MainPS();
    }
}
