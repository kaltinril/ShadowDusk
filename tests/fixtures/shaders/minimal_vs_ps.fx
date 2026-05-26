float4 VSMain(float4 pos : POSITION) : SV_Position { return pos; }
float4 PSMain() : SV_Target { return float4(1,0,0,1); }
