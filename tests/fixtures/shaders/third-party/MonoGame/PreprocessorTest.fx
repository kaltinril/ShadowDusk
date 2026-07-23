// =============================================================================
// PreprocessorTest.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : MonoGame (MonoGame/MonoGame)
//   Repo       : https://github.com/MonoGame/MonoGame
//   Tag        : v3.8.5
//   Upstream   : Tests/Assets/Effects/PreprocessorTest.fx
//   License    : Ms-PL - Copyright (C) MonoGame Foundation, Inc (see ./LICENSE, ./NOTICE.md)
//   Exercises  : The reference compiler's own preprocessor test: C comments, #if/#elif/#else, defined(), unknown-function warning. Requires -DTEST=<n>.
// =============================================================================
// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

#include "PreprocessorInclude.fxh"

#define TEST 1

/*
This is a C style comment.
*/

#if foo(TEST) // TODO: This should trigger a warning at position 13:4, but it's currently reported at 9:4.

#endif

#if TEST == 0
int Foo;
#elif TEST == 1
int Bar;
#else
int Baz;
#endif

#if defined(TEST2)
FOO
#elif defined(TEST3)
BAR
#endif

float4 VS_Main(float4 position : POSITION0) : SV_Position0
{
	return float4(1, 2, 3, 4);
}

float4 PS_Main(float4 position : SV_Position) : SV_TARGET0
{
	return 1;
}

technique
{
	pass
	{
		VertexShader = compile VS_PROFILE VS_Main();
		PixelShader = compile PS_PROFILE PS_Main();
	}
}
