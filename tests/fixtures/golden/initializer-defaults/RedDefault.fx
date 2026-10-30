#if OPENGL
#define PS ps_3_0
#else
#define PS ps_4_0_level_9_1
#endif

float R = 1;

float4 main() : COLOR0 { return float4(R, 0, 0, 1); }

technique T { pass P { PixelShader = compile PS main(); } }
