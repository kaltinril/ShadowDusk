// Phase 44 D - KNI DESKTOP (SDL2.GL) render validation.
//
// Compiles the 10-shader SM3 PS-only corpus with ShadowDusk's UNCHANGED EffectCompiler
// (default opts -> v10 GL .mgfx), then loads those exact bytes into a REAL KNI (nkast)
// Effect on the SDL2.GL desktop backend and renders the standard cat-corpus scene to
// validation/output/kni/*.png. Those bytes are byte-identical to validation/Candidate's
// (ShadowDusk is deterministic), so comparing output/kni against output/candidate
// (ShadowDusk -> MonoGame DesktopGL) and output/baseline (mgfxc goldens) - via
// validation/compare_kni.py - proves ShadowDusk's v10 output renders correctly on a
// CURRENT KNI v4.2.9001 runtime, same backend (GL), same scene.
//
// Run: dotnet run --project validation/KniDesktopGL   (needs a real GL desktop driver)

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

// ---- Runtime-integrity guard ------------------------------------------------------
// Prove we are actually running on KNI, not MonoGame. The nkast.* NuGet package ships
// its Game type in an assembly literally named "Xna.Framework.Game" (KNI's assembly
// naming), whereas MonoGame's is "MonoGame.Framework". If a stray MonoGame assembly
// ever shadowed KNI's, this harness would silently validate the wrong runtime; fail
// loudly instead. KNI v4.02 also stamps assembly version 4.2.9001.x, which we surface.
AssemblyName xna = typeof(Game).Assembly.GetName();
Console.WriteLine($"[kni-desktop] XNA implementation: {xna.Name} {xna.Version}");
bool isKni = xna.Name is not null
    && xna.Name.StartsWith("Xna.Framework", StringComparison.OrdinalIgnoreCase)
    && !xna.Name.StartsWith("MonoGame", StringComparison.OrdinalIgnoreCase);
if (!isKni)
{
    Console.WriteLine($"[kni-desktop] verdict: FAIL - expected the KNI (nkast 'Xna.Framework.*') runtime, " +
                      $"got '{xna.Name}'. This harness must render on KNI; aborting so we cannot mislabel " +
                      "a MonoGame render as KNI.");
    return 2;
}

// Container mode: default MGFX v10, or `knifx` arg for KNI's additive KNIFX v11 container.
// The KNIFX run renders into output/kni-knifx so compare_kni.py can pixel-diff it against
// the v10 render (output/kni) in real KNI — the render proof that KNIFX loads + runs.
bool knifxMode = args.Any(a => a.Equals("knifx", StringComparison.OrdinalIgnoreCase));
var container = knifxMode ? EffectContainer.Knifx : EffectContainer.Mgfx;

// ---- Reserved-word (`float noise;`) B10 render proof in real KNI ------------------------
// Phase 45 B10 fixed the GL binding of a free uniform whose name collides with a GLSL
// reserved word; the fix lives in the SHARED GL container path, so it flows into both the
// v10 MGFX and the KNIFX containers KNI loads. validation/ReservedWordGl proved it for v10 in
// MonoGame; this arm re-proves the SAME non-vacuous bar (set `noise` by name to 0.25/0.75,
// render, assert grey 64/191 and the ~127 differentiation) in the real KNI v4.02 runtime
// above, for MGFX v10 (default) or KNIFX (`knifx` arg). It does NOT touch the shared golden
// corpus - it compiles only the focused noise fixture.
//   Run: dotnet run --project validation/KniDesktopGL -- reservedword         # KNI MGFX v10
//        dotnet run --project validation/KniDesktopGL -- reservedword knifx   # KNI KNIFX v11
if (args.Any(a => a.Equals("reservedword", StringComparison.OrdinalIgnoreCase)))
    return await ShadowDusk.Validation.ReservedWordProbe.RunAsync(
        label: knifxMode ? "KNIFX v11" : "MGFX v10",
        outLeaf: knifxMode ? "kni-knifx" : "kni-v10",
        container: container);
string containerLabel = knifxMode ? "KNIFX v11" : "MGFX v10";
string outLeaf = knifxMode ? "kni-knifx" : "kni";

string repoRoot = ShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string catPath = ShaderInputs.CatPath(repoRoot);
string outDir = Path.Combine(repoRoot, "validation", "output", outLeaf);

Console.WriteLine($"[kni-desktop] container: {containerLabel}");
Console.WriteLine($"[kni-desktop] cat: {catPath}");
Console.WriteLine($"[kni-desktop] shaders: {shaderDir}");
Console.WriteLine($"[kni-desktop] out: {outDir}\n");

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
        Container = container,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fx,
    });

    if (result.IsFailure)
    {
        jobs.Add(new ShaderJob(name, null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))));
    }
    else
    {
        // Persist the raw container bytes so they can be diffed/inspected offline.
        string byteDir = Path.Combine(outDir, "..", knifxMode ? "kni-knifx-bytes" : "kni-mgfx");
        Directory.CreateDirectory(byteDir);
        string ext = knifxMode ? ".knifx" : ".mgfx";
        await File.WriteAllBytesAsync(Path.Combine(byteDir, name + ext), result.Value.Data);
        jobs.Add(new ShaderJob(name, result.Value.Data, null));
    }
}

Console.WriteLine("[kni-desktop] compile results:");
foreach (var j in jobs)
    Console.WriteLine($"  [{(j.Bytes is null ? "FAIL" : "OK  ")}] {j.Name,-12} {(j.CompileError ?? $"{j.Bytes!.Length} bytes")}");
Console.WriteLine();

using var game = new EffectImageRenderer(catPath, outDir, jobs, ShaderInputs.SetParams);
game.Run();

int ok = 0;
Console.WriteLine("[kni-desktop] load + render results (real KNI SDL2.GL):");
foreach (var o in game.Outcomes)
{
    string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
    if (status == "OK  ") ok++;
    Console.WriteLine($"  [{status}] {o.Name,-12} {(o.Error ?? o.PngPath)}");
}
Console.WriteLine($"\n[kni-desktop] {ok}/{game.Outcomes.Count} loaded + rendered in real KNI v{xna.Version}.");
Console.WriteLine("[kni-desktop] next: python validation/compare_kni.py  (pixel-compares output/kni vs baseline + candidate)");
return ok == game.Outcomes.Count ? 0 : 1;
