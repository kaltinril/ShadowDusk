// Phase 58 Area D / D1 - HAND conversion of cpt-max's `compute_write_to_texture` compute
// shader into an ordinary render-target pixel shader that stock MonoGame CAN run.
//
// THIS IS A RESEARCH PROBE, NOT A PRODUCT FEATURE. ShadowDusk ships no converter. The
// question D1 answers is only "can a human do this convincingly for this shape?", because
// Phase 58 refuses to write a transpiler on the strength of the idea alone.
//
// ------------------------------------------------------------------------------------
// The original kernel (one thread per PAIR of pixels), verbatim in shape:
//
//     [numthreads(8,8,1)]
//     void CS(..., uint3 globalID : SV_DispatchThreadID)
//     {
//         uint2 idL = uint2(globalID.x * 2 + OffsetX, globalID.y);
//         uint2 idR = uint2(idL.x + 1, idL.y);
//         float4 colL = Input[idL], colR = Input[idR];
//         bool exceedBorder = idR.x >= (uint)Width;
//         bool swap = HueFromRGB(colL.rgb) > HueFromRGB(colR.rgb) && !exceedBorder;
//         Output[idL] = swap ? colR : colL;      // <-- writes TWO pixels
//         Output[idR] = swap ? colL : colR;      // <-- from ONE invocation
//     }
//
// THE ONE STRUCTURAL OBSTACLE, and the whole finding of this probe: a compute shader
// SCATTERS (it writes wherever it likes, here two locations per invocation), while a pixel
// shader GATHERS (it writes exactly one location, its own fragment, and cannot choose it).
// A direct statement-by-statement port is therefore impossible - `Output[idL] = ...` has no
// pixel-shader spelling.
//
// It converts anyway, because the kernel's write set is a deterministic function of the
// output coordinate: given the pixel I am, I can work out which pair I belong to, read BOTH
// members myself, run the SAME comparison, and emit only my own half of the result. The
// comparison is recomputed redundantly (twice per pair, once by each member) instead of
// once - the standard cost of turning a scatter into a gather.
//
// That is the generalizable rule, and it is what D2 is written from: a compute kernel is
// convertible when each output element is a PURE FUNCTION OF ITS OWN COORDINATE. It is not
// convertible when outputs depend on where other threads decided to write (append buffers,
// atomics, scan/reduction across the dispatch, or any data-dependent output count).
//
// ------------------------------------------------------------------------------------
// Host-side recipe this shader requires (a converter could never write this for the user -
// see Phase 58 section 6.5.2; it is exactly why any Area D output is "a shader PLUS a
// documented recipe"):
//   * `Dispatch(w/2/8, h/8, 1)`             becomes  SetRenderTarget + a full-screen draw.
//   * `RWTexture2D<float4> Output`          becomes  the bound RenderTarget2D.
//   * `Texture2D<float4> Input`             becomes  the sampled texture, POINT-filtered.
//   * The ping-pong over OffsetX = 0/1      stays a host loop either way - unchanged.
//   * Read and write MUST be different textures (they already were: Input vs Output), so
//     the host ping-pongs two render targets.
//
// Integer texel addressing is done in FLOAT here on purpose: `Input[uint2]` is an SM5 typed
// load with no SM3/GLSL-1.10 equivalent, and integer `%` would trip SD0403 on the OpenGL
// profile (a GLSL 1.30+ operator in versionless GLSL). Parity comes from frac(t * 0.5)
// instead, which is exact for the small integers involved.

#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;

sampler2D SpriteTextureSampler = sampler_state
{
	Texture = <SpriteTexture>;
};

// Carried over unchanged from the original shader - this half needed no conversion at all,
// which is itself part of the finding: it is the I/O model that resists porting, not the math.
float HueFromRGB(float3 rgb)
{
	float minimum = min(rgb.r, min(rgb.g, rgb.b));
	float maximum = max(rgb.r, max(rgb.g, rgb.b));
	float delta = maximum - minimum;

	float hue = delta == 0 ? 0 :
		(rgb.r == maximum) ?     (rgb.g - rgb.b) / delta :
		(rgb.g == maximum) ? 2 + (rgb.b - rgb.r) / delta :
		                     4 + (rgb.r - rgb.g) / delta;

	hue *= 60;
	return hue >= 0 ? hue : hue + 360;
}

float Width;      // was `int Width`   - float to keep the GL profile off integer ops
float OffsetX;    // was `int OffsetX` - the host's ping-pong phase, 0 or 1
float TexelWidth; // 1.0 / Width; the host supplies it so the shader does no divide

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color : COLOR0;
	float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
	// Which column am I? floor() of the un-normalized u lands on the texel index because
	// the host point-samples and draws the render target 1:1 with the source.
	float x = floor(input.TextureCoordinates.x * Width);

	// Which pair do I belong to, and which half of it am I? Pairs start at OffsetX and
	// step by 2, so t = x - OffsetX is even for a left member and odd for a right one.
	float t = x - OffsetX;

	// t < 0 happens only for column 0 when OffsetX == 1: that pixel is in no pair this
	// phase, so it passes through untouched. The original kernel expressed the same thing
	// by simply never dispatching a thread that would write it.
	if (t < 0)
		return tex2D(SpriteTextureSampler, input.TextureCoordinates);

	// frac(t * 0.5) is exactly 0.0 for even t and exactly 0.5 for odd t.
	float isRight = frac(t * 0.5) > 0.25 ? 1.0 : 0.0;

	float xL = x - isRight;
	float xR = xL + 1.0;

	// Sample both members of the pair at their texel CENTRES.
	float4 colL = tex2D(SpriteTextureSampler, float2((xL + 0.5) * TexelWidth, input.TextureCoordinates.y));
	float4 colR = tex2D(SpriteTextureSampler, float2((xR + 0.5) * TexelWidth, input.TextureCoordinates.y));

	// The original comparison, unchanged. Recomputed by BOTH members of the pair - the
	// redundancy that buys us the gather formulation.
	bool exceedBorder = xR >= Width;
	bool swap = HueFromRGB(colL.rgb) > HueFromRGB(colR.rgb) && !exceedBorder;

	// Emit only MY half. `Output[idL] = swap ? colR : colL` for the left member;
	// `Output[idR] = swap ? colL : colR` for the right one.
	float4 mine = (isRight > 0.5) ? (swap ? colL : colR)
	                              : (swap ? colR : colL);

	return mine;
}

technique Tech0
{
	pass Pass0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
