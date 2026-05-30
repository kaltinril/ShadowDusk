// Baseline = the OFFICIAL MonoGame output. Loads the checked-in mgfxc goldens
// (tests/fixtures/golden/OpenGL/*.mgfx — these ARE MonoGame's standard-pipeline
// output) into a real DesktopGL Effect, applies each to the cat, saves 10 PNGs.

using System;
using System.IO;
using System.Linq;
using ShadowDusk.Validation;

string repoRoot = ShaderInputs.FindRepoRoot();
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL");
string catPath = ShaderInputs.CatPath(repoRoot);
string outDir = Path.Combine(repoRoot, "validation", "output", "baseline");

var jobs = ShaderInputs.ShaderNames.Select(name =>
{
    string mgfx = Path.Combine(goldenDir, name + ".mgfx");
    return File.Exists(mgfx)
        ? new ShaderJob(name, File.ReadAllBytes(mgfx), null)
        : new ShaderJob(name, null, $"golden not found: {mgfx}");
}).ToList();

Console.WriteLine($"[baseline] cat: {catPath}");
Console.WriteLine($"[baseline] goldens: {goldenDir}");
Console.WriteLine($"[baseline] out: {outDir}\n");

using var game = new EffectImageRenderer(catPath, outDir, jobs, ShaderInputs.SetParams);
game.Run();

int ok = 0;
foreach (var o in game.Outcomes)
{
    string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
    if (status == "OK  ") ok++;
    Console.WriteLine($"  [{status}] {o.Name,-12} {(o.Error ?? o.PngPath)}");
}
Console.WriteLine($"\n[baseline] {ok}/{game.Outcomes.Count} rendered.");
return ok == game.Outcomes.Count ? 0 : 1;
