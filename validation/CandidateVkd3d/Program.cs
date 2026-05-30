// CandidateVkd3d = OUR compiler, vkd3d-shader DXBC backend (the CROSS-PLATFORM one).
// Compiles each .fx with ShadowDusk's EffectCompiler in-memory (DirectX target,
// DxbcBackend.Vkd3d), loads the resulting .mgfx bytes into the SAME real
// MonoGame.Framework.WindowsDX (DX11) Effect path as the baseline and the
// d3dcompiler oracle candidate, applies each to the cat, saves PNGs under
// validation/output-dx/candidate-vkd3d. This is the proof that vkd3d delivers the
// SAME fidelity as the proven oracle.

using System;
using System.IO;
using System.Linq;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation.Dx;

string repoRoot = DxShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string catPath = DxShaderInputs.CatPath(repoRoot);
string outDir = Path.Combine(repoRoot, "validation", "output-dx", "candidate-vkd3d");

Console.WriteLine($"[candidate-vkd3d] cat: {catPath}");
Console.WriteLine($"[candidate-vkd3d] shaders: {shaderDir}");
Console.WriteLine($"[candidate-vkd3d] out: {outDir}\n");

var compiler = new EffectCompiler();
var jobs = new System.Collections.Generic.List<ShaderJob>();

foreach (var name in DxShaderInputs.ShaderNames)
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
        Target = PlatformTarget.DirectX,
        DxbcBackend = DxbcBackend.Vkd3d,   // <-- the only difference vs CandidateDx
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fx,
    });

    if (result.IsFailure)
    {
        jobs.Add(new ShaderJob(name, null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))));
    }
    else
    {
        string mgfxDir = Path.Combine(outDir, "..", "candidate-vkd3d-mgfx");
        Directory.CreateDirectory(mgfxDir);
        await File.WriteAllBytesAsync(Path.Combine(mgfxDir, name + ".mgfx"), result.Value.Data);
        jobs.Add(new ShaderJob(name, result.Value.Data, null));
    }
}

Console.WriteLine("[candidate-vkd3d] compile results:");
foreach (var j in jobs)
    Console.WriteLine($"  [{(j.Bytes is null ? "FAIL" : "OK  ")}] {j.Name,-12} {(j.CompileError ?? $"{j.Bytes!.Length} bytes")}");
Console.WriteLine();

using var game = new DxEffectImageRenderer(catPath, outDir, jobs, DxShaderInputs.SetParams);
game.Run();

int ok = 0;
Console.WriteLine("[candidate-vkd3d] load + render results:");
foreach (var o in game.Outcomes)
{
    string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
    if (status == "OK  ") ok++;
    Console.WriteLine($"  [{status}] {o.Name,-12} {(o.Error ?? o.PngPath)}");
}
Console.WriteLine($"\n[candidate-vkd3d] {ok}/{game.Outcomes.Count} rendered.");
return ok == game.Outcomes.Count ? 0 : 1;
