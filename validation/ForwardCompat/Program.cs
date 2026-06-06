// Phase 35 Area A — FORWARD-COMPAT validation.
//
// Identical to validation/Candidate/Program.cs in every way EXCEPT the MonoGame
// runtime: this project references MonoGame.Framework.DesktopGL 3.8.4.1 (newer
// than the product's pinned 3.8.2.1105) via a project-local VersionOverride —
// the product pin is untouched.
//
// It compiles the SM3 PS-only corpus with the UNCHANGED ShadowDusk EffectCompiler
// (default options => v10 GL .mgfx), loads those exact bytes into a REAL newer
// DesktopGL Effect, renders the cat, and writes 10 PNGs to output/forwardcompat/.
//
// The goal: prove the v10 .mgfx the consumer already gets STILL loads + renders
// on a newer MonoGame, with zero consumer action. compare.py then checks these
// renders pixel-equivalent to the 3.8.2.1105 candidate renders (same bytes,
// different runtime) and to the mgfxc baseline goldens.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

string repoRoot = ShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string catPath = ShaderInputs.CatPath(repoRoot);
string outDir = Path.Combine(repoRoot, "validation", "output", "forwardcompat");

// Prove which MonoGame runtime is actually loaded — this is the whole point.
var mgAsm = typeof(Microsoft.Xna.Framework.Graphics.Effect).Assembly;
string mgVersion = mgAsm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? mgAsm.GetName().Version?.ToString() ?? "(unknown)";

Console.WriteLine($"[forwardcompat] MonoGame runtime: {mgAsm.GetName().Name} {mgVersion}");
Console.WriteLine($"[forwardcompat] (product pin stays 3.8.2.1105; this project uses VersionOverride)");
Console.WriteLine($"[forwardcompat] cat: {catPath}");
Console.WriteLine($"[forwardcompat] shaders: {shaderDir}");
Console.WriteLine($"[forwardcompat] out: {outDir}\n");

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
    // Default options => Target=OpenGL, MgfxVersion=10. The product is unchanged.
    var result = await compiler.CompileAsync(src, new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fx,
    });

    if (result.IsFailure)
    {
        jobs.Add(new ShaderJob(name, null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))));
    }
    else
    {
        // Persist the raw .mgfx so the format can be inspected/diffed offline.
        string mgfxDir = Path.Combine(outDir, "..", "forwardcompat-mgfx");
        Directory.CreateDirectory(mgfxDir);
        await File.WriteAllBytesAsync(Path.Combine(mgfxDir, name + ".mgfx"), result.Value.Data);
        jobs.Add(new ShaderJob(name, result.Value.Data, null));
    }
}

Console.WriteLine("[forwardcompat] compile results:");
foreach (var j in jobs)
    Console.WriteLine($"  [{(j.Bytes is null ? "FAIL" : "OK  ")}] {j.Name,-12} {(j.CompileError ?? $"{j.Bytes!.Length} bytes")}");
Console.WriteLine();

using var game = new EffectImageRenderer(catPath, outDir, jobs, ShaderInputs.SetParams);
game.Run();

int ok = 0;
Console.WriteLine("[forwardcompat] load + render results:");
foreach (var o in game.Outcomes)
{
    string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
    if (status == "OK  ") ok++;
    Console.WriteLine($"  [{status}] {o.Name,-12} {(o.Error ?? o.PngPath)}");
}
Console.WriteLine($"\n[forwardcompat] {ok}/{game.Outcomes.Count} rendered on MonoGame {mgVersion}.");
return ok == game.Outcomes.Count ? 0 : 1;
