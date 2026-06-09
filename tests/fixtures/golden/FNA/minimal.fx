float4 PSMain() : COLOR { return float4(1,0,0,1); }
technique T { pass P { PixelShader = compile ps_2_0 PSMain(); } }
