// Phase 35 Area B - MGFX v11 render validation against MonoGame 3.8.5 (v11-capable).
//
// Compiles the SM3 PS-only corpus with ShadowDusk's EffectCompiler at MgfxVersion 11 (or 10
// via the `v10` arg) and renders those bytes in real MonoGame.Framework.DesktopGL
// 3.8.5-preview.6 (the develop line that shipped MGFX v11). A malformed v11 file throws on
// `new Effect(...)`, so 10/10 load + render is the proof the v11 byte stream is correct.
// Render to output/mgfx-v11 (v11) or output/mgfx-v10-385 (v10) so compare_mgfxv11.py can
// pixel-diff them (both rendered in the same 3.8.5 runtime).
//
// Run: dotnet run --project validation/MonoGameV11           # v11
//      dotnet run --project validation/MonoGameV11 -- v10    # v10 reference (same runtime)

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

// ---- Runtime-integrity guard: must be MonoGame 3.8.5+ (v11-capable), not KNI/3.8.2 --------
AssemblyName xna = typeof(Game).Assembly.GetName();
Console.WriteLine($"[mgfx-v11] XNA implementation: {xna.Name} {xna.Version}");
bool isMonoGame = xna.Name is not null && xna.Name.StartsWith("MonoGame", StringComparison.OrdinalIgnoreCase);
bool is385Plus = xna.Version is not null && (xna.Version.Major > 3
    || (xna.Version.Major == 3 && xna.Version.Minor > 8)
    || (xna.Version.Major == 3 && xna.Version.Minor == 8 && xna.Version.Build >= 5));
if (!isMonoGame || !is385Plus)
{
    Console.WriteLine($"[mgfx-v11] verdict: FAIL - need MonoGame >= 3.8.5 (v11-capable); got '{xna.Name} {xna.Version}'. " +
                      "MGFX v11 (per-shader SourceFile/Entrypoint) only loads on a runtime whose Effect reader accepts version 11.");
    return 2;
}

bool v10Mode = args.Any(a => a.Equals("v10", StringComparison.OrdinalIgnoreCase));
int mgfxVersion = v10Mode ? 10 : 11;
string outLeaf = v10Mode ? "mgfx-v10-385" : "mgfx-v11";

string repoRoot = ShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string catPath = ShaderInputs.CatPath(repoRoot);
string outDir = Path.Combine(repoRoot, "validation", "output", outLeaf);

Console.WriteLine($"[mgfx-v11] container: MGFX v{mgfxVersion}  (rendered in MonoGame {xna.Version})");
Console.WriteLine($"[mgfx-v11] out: {outDir}\n");

var compiler = new EffectCompiler();
var jobs = new System.Collections.Generic.List<ShaderJob>();

foreach (var name in ShaderInputs.ShaderNames)
{
    string fx = Path.Combine(shaderDir, name + ".fx");
    if (!File.Exists(fx))
    {
        jobs.Add(new ShaderJob(name, null, $".fx not found: {fx}"));
        continue;
    }

    string src = await File.ReadAllTextAsync(fx);
    var result = await compiler.CompileAsync(src, new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        MgfxVersion = mgfxVersion,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fx,
    });

    jobs.Add(result.IsFailure
        ? new ShaderJob(name, null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")))
        : new ShaderJob(name, result.Value.Data, null));
}

Console.WriteLine("[mgfx-v11] compile results:");
foreach (var j in jobs)
    Console.WriteLine($"  [{(j.Bytes is null ? "FAIL" : "OK  ")}] {j.Name,-12} {(j.CompileError ?? $"{j.Bytes!.Length} bytes")}");
Console.WriteLine();

using var game = new EffectImageRenderer(catPath, outDir, jobs, ShaderInputs.SetParams);
game.Run();

int ok = 0;
Console.WriteLine($"[mgfx-v11] load + render results (real MonoGame {xna.Version}):");
foreach (var o in game.Outcomes)
{
    string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
    if (status == "OK  ") ok++;
    Console.WriteLine($"  [{status}] {o.Name,-12} {(o.Error ?? o.PngPath)}");
}
Console.WriteLine($"\n[mgfx-v11] {ok}/{game.Outcomes.Count} loaded + rendered as MGFX v{mgfxVersion} in real MonoGame {xna.Version}.");
Console.WriteLine("[mgfx-v11] next: run with `-- v10`, then python validation/compare_mgfxv11.py");
return ok == game.Outcomes.Count ? 0 : 1;
