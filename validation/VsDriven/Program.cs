// Phase 28 rung-4 validation for a VS-DRIVEN effect.
//
// Compiles the VS-driven fixture with ShadowDusk (candidate) AND loads the mgfxc
// OpenGL golden (baseline) for the SAME .fx, renders BOTH through the identical
// custom vertex-buffer draw path (VsEffectImageRenderer), and reports each side's
// load+render result. A separate compare step (validation/compare.py) diffs the two
// PNGs pixel-for-pixel — same-backend GL↔GL, the rung-4 bar.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

string repoRoot = ShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL");
string catPath = ShaderInputs.CatPath(repoRoot);
string outBase = Path.Combine(repoRoot, "validation", "output-vs");

// The single VS-driven fixture this phase adds.
const string fixture = "VsTransformColorTexture";

Console.WriteLine($"[vs] cat: {catPath}");
Console.WriteLine($"[vs] fixture: {fixture}\n");

// ---- Candidate: compile the .fx with ShadowDusk (OpenGL) in memory. ----
string fxPath = Path.Combine(shaderDir, fixture + ".fx");
byte[]? candidateBytes = null;
string? candidateErr = null;
{
    var compiler = new EffectCompiler();
    string src = await File.ReadAllTextAsync(fxPath);
    var result = await compiler.CompileAsync(src, new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fxPath,
    });
    if (result.IsFailure)
        candidateErr = string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));
    else
    {
        candidateBytes = result.Value.Data;
        string mgfxDir = Path.Combine(outBase, "candidate-mgfx");
        Directory.CreateDirectory(mgfxDir);
        await File.WriteAllBytesAsync(Path.Combine(mgfxDir, fixture + ".mgfx"), candidateBytes);
    }
}

// ---- Baseline: the mgfxc OpenGL golden bytes. ----
string goldenPath = Path.Combine(goldenDir, fixture + ".mgfx");
byte[]? baselineBytes = File.Exists(goldenPath) ? await File.ReadAllBytesAsync(goldenPath) : null;
string? baselineErr = baselineBytes is null ? $"golden not found: {goldenPath}" : null;

Console.WriteLine($"[vs] candidate: {(candidateBytes is null ? "COMPILE FAIL: " + candidateErr : candidateBytes.Length + " bytes")}");
Console.WriteLine($"[vs] baseline:  {(baselineBytes is null ? baselineErr : baselineBytes.Length + " bytes")}\n");

int Render(string label, byte[]? bytes, string? err)
{
    var jobs = new List<ShaderJob> { new(fixture, bytes, err) };
    string outDir = Path.Combine(outBase, label);
    using var game = new VsEffectImageRenderer(catPath, outDir, jobs);
    game.Run();
    int ok = 0;
    foreach (var o in game.Outcomes)
    {
        string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
        if (status == "OK  ") ok++;
        Console.WriteLine($"  [{label}] [{status}] {o.Name,-24} {(o.Error ?? o.PngPath)}");
    }
    return ok;
}

int b = Render("baseline", baselineBytes, baselineErr);
int c = Render("candidate", candidateBytes, candidateErr);

Console.WriteLine($"\n[vs] baseline rendered {b}/1, candidate rendered {c}/1.");
return (b == 1 && c == 1) ? 0 : 1;
