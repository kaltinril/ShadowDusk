// SlangFullCorpusVulkan = issue #230's Vulkan arm of the real-slangc Slang render gate.
// The 21-shader corpus through ShadowDusk.Slang.SlangCompiler, loaded into a REAL MonoGame
// 3.8.5 DesktopVK Effect and pixel-diffed against the real mgfxc 3.8.5 /Profile:Vulkan build of
// the same assembled .fx, on the same device. See validation/SharedSlang/SlangMonoGameGate.cs.
//
//   dotnet run --project validation/SlangFullCorpusVulkan -c Release
//   SHADOWDUSK_SLANG_CONTROL=1 ... -> the positive controls replace the gated rows (expect exit 1)

using ShadowDusk.Core;
using ShadowDusk.Validation.Slang;

// Tolerance 4/255 for the same DXC-version reason as the DirectX_12 arm; the measured max delta
// is printed either way.
return await SlangMonoGameGate.RunAsync(PlatformTarget.Vulkan, "Vulkan", "vulkan", tolerance: 4);
