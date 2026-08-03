//-----------------------------------------------------------------------------
// SamplerRegisterSparse.fx — GitHub issue #189, the SPARSE/OFFSET register half.
//
// Sibling of SamplerRegisterOrder.fx, and deliberately isolating a DIFFERENT
// variable. That fixture samples out of declaration order to pin the ORDER samplers
// are allocated in. This one samples strictly IN declaration order, so ordering
// cannot be what it measures: the only thing under test is the ABSOLUTE register
// VALUE.
//
// The two samplers sit at s2 and s3 and NOTHING is declared at s0 or s1. Compacting
// them to units 0/1 is order-preserving and self-consistent, and still wrong,
// because texture unit 0 is not the effect's to allocate: SpriteBatcher.FlushVertexArray
// assigns `_device.Textures[0] = texture` right AFTER EffectPass.Apply(), so whatever
// the effect put on unit 0 gets overwritten with the sprite.
//
// Driven with a BLUE sprite (which lands on unit 0 no matter what), a RED MaskA and a
// GREEN MaskB, output = (a.r, b.g, 0, 1):
//
//   registers honoured (mgfxc)   -> A on unit 2, B on unit 3, sprite alone on unit 0
//                                   -> (255, 255,   0)  yellow
//   compacted to 0/1 (the bug)   -> A on unit 0, overwritten by the BLUE sprite
//                                   -> (  0, 255,   0)  green
//
// Only the RED channel moves, which is the point: it is exactly the sampler that got
// pushed onto SpriteBatch's unit that goes wrong, and the untouched green channel is
// the control proving the harness bound anything at all.
//
// WHY THE LEGACY FORM SPECIFICALLY (measured 2026-08-02, do not "modernize" this):
// mgfxc honours the register annotation HERE and nowhere else. Compiled at ps_3_0 a
// legacy `sampler` IS the combined sampler, so `: register(sN)` pins its SM3 sampler
// register directly. The modern spelling behaves differently and was measured: for
// `Texture2D T : register(t3); SamplerState S : register(s2);` mgfxc's OpenGL output
// puts the pair on slot 0 anyway, allocating by texture declaration order and ignoring
// both annotations. So rewriting this fixture in modern syntax would silently stop
// testing anything.
//-----------------------------------------------------------------------------

#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler MaskA : register(s2);
sampler MaskB : register(s3);

float4 PS(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	// Declaration order, on purpose - see the header.
	float4 a = tex2D(MaskA, texCoord);
	float4 b = tex2D(MaskB, texCoord);
	return float4(a.r, b.g, 0, 1);
}

technique SamplerRegisterSparse
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL PS();
	}
}
