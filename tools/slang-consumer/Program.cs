// ShadowDusk.Slang scratch consumer (issue #225): exactly what a brand-new user does.
//   dotnet new console
//   dotnet add package ShadowDusk.Slang
//   new SlangCompiler().CompileAsync(slangSource, options)
// No native installs, no flags, no chmod: the slangc native must ride inside the package,
// resolve from the consumer's own output, and run.
//
// Asserts, not just prints:
//   * slangc resolves from INSIDE this app's output directory (never a repo tools/ walk-up:
//     this project is copied outside the repo, so a hit there would be a packaging bug);
//   * genuine Slang (a generic over an interface) and a VS+PS shader compile on OpenGL and
//     DirectX, producing an MGFX container.
// Exit code 0 only when every case passed.
using ShadowDusk.Core;
using ShadowDusk.Slang;

int failed = 0;

string? slangc = SlangToolPath.Resolve();
string appDir = Path.GetFullPath(AppContext.BaseDirectory);
Console.WriteLine($"host           : {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
Console.WriteLine($"app base dir   : {appDir}");
Console.WriteLine($"slangc resolved: {slangc ?? "<null>"}");
if (slangc is null)
{
    Console.Error.WriteLine("FAIL  slangc did not resolve from the installed package");
    return 1;
}
if (!Path.GetFullPath(slangc).StartsWith(appDir, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"FAIL  slangc resolved outside the app's own output ({slangc})");
    failed++;
}
if (!OperatingSystem.IsWindows())
    Console.WriteLine($"slangc mode    : {File.GetUnixFileMode(slangc)}");

// win-arm64 (issue #286) has no vkd3d-shader build, so its DirectX column is DirectX 12 (DXC)
// plus Vulkan, and DirectX 11 is asserted to be the registered SD0211 below, never a crash.
bool winArm64 = OperatingSystem.IsWindows()
    && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
PlatformTarget dx = winArm64 ? PlatformTarget.DirectX12 : PlatformTarget.DirectX;
var cases = new List<(string File, PlatformTarget Target)>
{
    ("GenericsProbe.slang", PlatformTarget.OpenGL),   // generic over an interface: real Slang only
    ("GenericsProbe.slang", dx),
    ("WaveVertex.slang",    PlatformTarget.OpenGL),   // VS+PS: two slangc runs + merge
    ("WaveVertex.slang",    dx),
};
if (winArm64)
    cases.Add(("WaveVertex.slang", PlatformTarget.Vulkan));

var compiler = new SlangCompiler();
foreach (var (file, target) in cases)
{
    string label = $"{file} -> {target}";
    var result = await compiler.CompileAsync(
        await File.ReadAllTextAsync(Path.Combine(appDir, file)),
        new CompilerOptions { Target = target, SourceFileName = file });

    if (result.IsFailure)
    {
        Console.Error.WriteLine($"FAIL  {label}:");
        foreach (var e in result.Error)
            Console.Error.WriteLine($"      {e.File}({e.Line},{e.Column}): {e.Code}: {e.Message}");
        failed++;
        continue;
    }

    byte[] data = result.Value.Data;
    if (data.Length <= 4 || data[0] != (byte)'M' || data[1] != (byte)'G' || data[2] != (byte)'F' || data[3] != (byte)'X')
    {
        Console.Error.WriteLine($"FAIL  {label}: not an MGFX container ({data.Length} bytes)");
        failed++;
        continue;
    }
    Console.WriteLine($"OK    {label}: {data.Length} bytes, MGFX magic verified");
}

if (winArm64)
{
    var dx11 = await compiler.CompileAsync(
        await File.ReadAllTextAsync(Path.Combine(appDir, "WaveVertex.slang")),
        new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = "WaveVertex.slang" });
    if (dx11.IsSuccess || !dx11.Error.Any(e => e.Code == "SD0211"))
    {
        Console.Error.WriteLine("FAIL  WaveVertex.slang -> DirectX on win-arm64: expected the registered SD0211 (no vkd3d-shader build)");
        failed++;
    }
    else
    {
        Console.WriteLine("OK    WaveVertex.slang -> DirectX on win-arm64: SD0211, as registered");
    }
}

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} check(s) FAILED");
    return 1;
}
Console.WriteLine($"All {cases.Count} Slang consumer compiles succeeded; ShadowDusk.Slang is consumable on this host");
return 0;
