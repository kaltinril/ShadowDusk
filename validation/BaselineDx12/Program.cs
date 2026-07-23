// BaselineDx12 = the OFFICIAL MonoGame DirectX12 output. Loads the checked-in real
// mgfxc goldens (tests/fixtures/golden/DirectX_12/*.mgfx - produced by real MonoGame
// 3.8.5 content build, /Platform:WindowsDX12) into a real MonoGame WindowsDX12
// Effect, applies each to the cat, saves PNGs under validation/output/baseline-dx12.

using System;
using System.IO;
using System.Linq;
using ShadowDusk.Validation;

string repoRoot = ShaderInputs.FindRepoRoot();
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_12");
string catPath = ShaderInputs.CatPath(repoRoot);
string outDir = Path.Combine(repoRoot, "validation", "output", "baseline-dx12");

var jobs = ShaderInputs.ShaderNames.Select(name =>
{
    string mgfx = Path.Combine(goldenDir, name + ".mgfx");
    return File.Exists(mgfx)
        ? new ShaderJob(name, File.ReadAllBytes(mgfx), null)
        : new ShaderJob(name, null, $"golden not found: {mgfx}");
}).ToList();

Console.WriteLine($"[baseline-dx12] cat: {catPath}");
Console.WriteLine($"[baseline-dx12] goldens: {goldenDir}");
Console.WriteLine($"[baseline-dx12] out: {outDir}\n");

using var game = new EffectImageRenderer(catPath, outDir, jobs, ShaderInputs.SetParams);
game.Run();

int ok = 0;
foreach (var o in game.Outcomes)
{
    string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
    if (status == "OK  ") ok++;
    Console.WriteLine($"  [{status}] {o.Name,-12} {(o.Error ?? o.PngPath)}");
}
Console.WriteLine($"\n[baseline-dx12] {ok}/{game.Outcomes.Count} rendered.");
return ok == game.Outcomes.Count ? 0 : 1;
