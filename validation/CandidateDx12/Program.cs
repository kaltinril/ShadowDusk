// Candidate = OUR compiler. Compiles each .fx with ShadowDusk's EffectCompiler
// in-memory (DirectX12 target), loads the resulting .mgfx bytes into a real
// MonoGame WindowsDX12 Effect path, applies each to the cat, saves PNGs.
// Phase 54 - own-output render proof (no mgfxc DirectX_12 golden exists yet in
// this repo's corpus - see the phase appendix's open root-signature question).

using System;
using System.IO;
using System.Linq;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

string repoRoot = ShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string catPath = ShaderInputs.CatPath(repoRoot);
string outDir = Path.Combine(repoRoot, "validation", "output", "candidate-dx12");

Console.WriteLine($"[candidate-dx12] cat: {catPath}");
Console.WriteLine($"[candidate-dx12] shaders: {shaderDir}");
Console.WriteLine($"[candidate-dx12] out: {outDir}\n");

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
        Target = PlatformTarget.DirectX12,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fx,
    });

    if (result.IsFailure)
    {
        jobs.Add(new ShaderJob(name, null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))));
    }
    else
    {
        string mgfxDir = Path.Combine(outDir, "..", "candidate-dx12-mgfx");
        Directory.CreateDirectory(mgfxDir);
        await File.WriteAllBytesAsync(Path.Combine(mgfxDir, name + ".mgfx"), result.Value.Data);
        jobs.Add(new ShaderJob(name, result.Value.Data, null));
    }
}

Console.WriteLine("[candidate-dx12] compile results:");
foreach (var j in jobs)
    Console.WriteLine($"  [{(j.Bytes is null ? "FAIL" : "OK  ")}] {j.Name,-12} {(j.CompileError ?? $"{j.Bytes!.Length} bytes")}");
Console.WriteLine();

using var game = new EffectImageRenderer(catPath, outDir, jobs, ShaderInputs.SetParams);
game.Run();

int ok = 0;
Console.WriteLine("[candidate-dx12] load + render results:");
foreach (var o in game.Outcomes)
{
    string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
    if (status == "OK  ") ok++;
    Console.WriteLine($"  [{status}] {o.Name,-12} {(o.Error ?? o.PngPath)}");
}
Console.WriteLine($"\n[candidate-dx12] {ok}/{game.Outcomes.Count} rendered.");
return ok == game.Outcomes.Count ? 0 : 1;
