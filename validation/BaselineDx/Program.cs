// BaselineDx = the OFFICIAL MonoGame DirectX output. Loads the checked-in mgfxc
// goldens (tests/fixtures/golden/DirectX_11/*.mgfx — these ARE MonoGame's
// standard-pipeline DX output) into a real MonoGame.Framework.WindowsDX (DX11)
// Effect, applies each to the cat, saves PNGs under validation/output-dx/baseline.

using System;
using System.IO;
using System.Linq;
using ShadowDusk.Validation.Dx;

string repoRoot = DxShaderInputs.FindRepoRoot();
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_11");
string catPath = DxShaderInputs.CatPath(repoRoot);
string outDir = Path.Combine(repoRoot, "validation", "output-dx", "baseline");

var jobs = DxShaderInputs.ShaderNames.Select(name =>
{
    string mgfx = Path.Combine(goldenDir, name + ".mgfx");
    return File.Exists(mgfx)
        ? new ShaderJob(name, File.ReadAllBytes(mgfx), null)
        : new ShaderJob(name, null, $"golden not found: {mgfx}");
}).ToList();

Console.WriteLine($"[baseline-dx] cat: {catPath}");
Console.WriteLine($"[baseline-dx] goldens: {goldenDir}");
Console.WriteLine($"[baseline-dx] out: {outDir}\n");

using var game = new DxEffectImageRenderer(catPath, outDir, jobs, DxShaderInputs.SetParams);
game.Run();

int ok = 0;
foreach (var o in game.Outcomes)
{
    string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
    if (status == "OK  ") ok++;
    Console.WriteLine($"  [{status}] {o.Name,-12} {(o.Error ?? o.PngPath)}");
}
Console.WriteLine($"\n[baseline-dx] {ok}/{game.Outcomes.Count} rendered.");
return ok == game.Outcomes.Count ? 0 : 1;
