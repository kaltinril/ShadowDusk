// =============================================================================
// PreprocessorInclude.fxh
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : MonoGame (MonoGame/MonoGame)
//   Repo       : https://github.com/MonoGame/MonoGame
//   Tag        : v3.8.5
//   Upstream   : Tests/Assets/Effects/PreprocessorInclude.fxh
//   License    : Ms-PL - Copyright (C) MonoGame Foundation, Inc (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Include target for PreprocessorTest.fx.
// =============================================================================
// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

#if SM4

#define PS_PROFILE ps_4_0_level_9_1
#define VS_PROFILE vs_4_0_level_9_1

#else

#define PS_PROFILE ps_2_0
#define VS_PROFILE vs_2_0

#endif
int a; // Something on final line