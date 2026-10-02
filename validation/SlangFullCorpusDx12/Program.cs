// SlangFullCorpusDx12 = issue #230's DirectX_12 arm of the real-slangc Slang render gate.
// The 21-shader corpus through ShadowDusk.Slang.SlangCompiler, loaded into a REAL MonoGame
// 3.8.5 WindowsDX12 Effect and pixel-diffed against the real mgfxc 3.8.5 /Profile:DirectX_12
// build of the same assembled .fx, on the same device. See validation/SharedSlang/SlangMonoGameGate.cs.
//
//   dotnet run --project validation/SlangFullCorpusDx12 -c Release
//   SHADOWDUSK_SLANG_CONTROL=1 ... -> the positive controls replace the gated rows (expect exit 1)

using ShadowDusk.Core;
using ShadowDusk.Validation.Dx;
using ShadowDusk.Validation.Slang;

DxHeadlessRasterizer.PinIfRequested();

// Tolerance 4/255, compare_dx12.py's default: the reference's DXC (MonoGame 3.8.5's bundled
// dxcoob 1.8) is newer than ShadowDusk's pinned 1.7 build (see docs/validation-matrix.md
// section 7), so a 1-ULP difference is a known toolchain artifact, not a defect. The driver
// prints the measured max delta either way.
return await SlangMonoGameGate.RunAsync(PlatformTarget.DirectX12, "DirectX_12", "dx12", tolerance: 4);
