// Dumps the EXACT HLSL text ShadowDusk's pipeline hands to DXC for a given .fx + PlatformTarget,
// plus the -D macro flags that ride alongside it — i.e. everything on DXC's side of the seam
// EXCEPT the DXC binary itself.
//
// Why it exists: when ShadowDusk's output diverges from the reference compiler's on a target that
// compiles through DXC (OpenGL, Vulkan, DirectX 12, and DX11's reflection blob), there are exactly
// three candidate causes — the source we feed DXC, the flags we pass it, or the DXC build we pin.
// Dumping the source lets the first two be eliminated by replaying the identical input through a
// different DXC and diffing the disassembly, which is how the DX12 Apos.Shapes 1/255 delta was
// root-caused to the pin alone (2026-07-31; see plan/DONE/PHASE-55-... §8).
//
//   dotnet run --project validation/DumpPreprocessedHlsl -- <fx-path> <target> <out.hlsl>
//
// then, with any other dxc.exe (a Windows SDK one is a different build from our pin):
//
//   dxc -E <entry> -T ps_6_0 -WX -D MGFX=1 -D HLSL=1 -D SM6=1 -Fo out.dxil out.hlsl
//   dxc -dumpbin out.dxil
//
// The non-macro half of the flag list lives in DxcFlagBuilder.Build and must be mirrored by hand
// for whichever target is being investigated; this tool prints the macro half.

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: DumpPreprocessedHlsl <fx-path> <target> <out.hlsl>");
    Console.Error.WriteLine("  target: DirectX | DirectX12 | OpenGL | Vulkan | Fna");
    return 2;
}

string fxPath = args[0];
if (!Enum.TryParse(args[1], ignoreCase: true, out PlatformTarget target) || !PlatformMacros.IsSupported(target))
{
    Console.Error.WriteLine($"unknown or unsupported target '{args[1]}'");
    return 2;
}
string outPath = args[2];

string src = await File.ReadAllTextAsync(fxPath);

// Stage 1 + stage 2 of CompilationPipeline.Run: the FX9 pre-parser (strips technique/pass/
// sampler_state blocks and rewrites legacy D3D9 constructs forward to SM4), then the preprocessor
// (prepends the platform macros and flattens #includes). What comes out is verbatim what the
// DxcCompileRequest.HlslSource field carries.
var parsed = FxPreParser.Parse(src, fxPath);
if (parsed.IsFailure)
{
    Console.Error.WriteLine($"pre-parse failed: {parsed.Error}");
    return 1;
}

MacroSet macros = PlatformMacros.For(target);
var flattened = new Preprocessor().Flatten(
    parsed.Value.StrippedHlsl, fxPath, macros, new FileSystemIncludeResolver(), Array.Empty<string>());
if (flattened.IsFailure)
{
    Console.Error.WriteLine($"flatten failed: {flattened.Error}");
    return 1;
}

await File.WriteAllTextAsync(outPath, flattened.Value.Text);
Console.WriteLine($"wrote {outPath} ({flattened.Value.Text.Length} chars) for target {target}");
Console.WriteLine("dxc macro flags: " + string.Join(" ", macros.ToDxcFlags()));
return 0;
