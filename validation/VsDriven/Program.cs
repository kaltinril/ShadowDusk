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

(int Ok, (string Name, Microsoft.Xna.Framework.Color[] Pixels, int W, int H)? Capture)
    Render(string label, byte[]? bytes, string? err, bool backbuffer)
{
    var jobs = new List<ShaderJob> { new(fixture, bytes, err) };
    string outDir = Path.Combine(outBase, label);
    using var game = new VsEffectImageRenderer(catPath, outDir, jobs, renderToBackbuffer: backbuffer);
    game.Run();
    int ok = 0;
    foreach (var o in game.Outcomes)
    {
        string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
        if (status == "OK  ") ok++;
        Console.WriteLine($"  [{label}] [{status}] {o.Name,-24} {(o.Error ?? o.PngPath)}");
    }
    return (ok, game.Captures.Count > 0 ? game.Captures[0] : null);
}

int MaxDelta((string, Microsoft.Xna.Framework.Color[], int, int)? a,
             (string, Microsoft.Xna.Framework.Color[], int, int)? b)
{
    if (a is null || b is null) return int.MaxValue;
    var (_, pa, wa, ha) = a.Value;
    var (_, pb, wb, hb) = b.Value;
    if (wa != wb || ha != hb) return int.MaxValue;
    int maxd = 0;
    for (int i = 0; i < pa.Length; i++)
    {
        maxd = Math.Max(maxd, Math.Abs(pa[i].R - pb[i].R));
        maxd = Math.Max(maxd, Math.Abs(pa[i].G - pb[i].G));
        maxd = Math.Max(maxd, Math.Abs(pa[i].B - pb[i].B));
        maxd = Math.Max(maxd, Math.Abs(pa[i].A - pb[i].A));
    }
    return maxd;
}

// ---- Render-target mode (the original Phase 28 path; MonoGame sets posFixup.y = -1). ----
var (bRt, bRtCap) = Render("baseline",  baselineBytes,  baselineErr,  backbuffer: false);
var (cRt, cRtCap) = Render("candidate", candidateBytes, candidateErr, backbuffer: false);

// ---- BACKBUFFER mode (Phase 43 F3 — the case the static Y-flip got wrong;
// MonoGame sets posFixup.y = +1 and reads back via GetBackBufferData). ----
var (bBb, bBbCap) = Render("baseline-backbuffer",  baselineBytes,  baselineErr,  backbuffer: true);
var (cBb, cBbCap) = Render("candidate-backbuffer", candidateBytes, candidateErr, backbuffer: true);

int rtMaxd = MaxDelta(bRtCap, cRtCap);
int bbMaxd = MaxDelta(bBbCap, cBbCap);

Console.WriteLine($"\n[vs] render-target: baseline {bRt}/1, candidate {cRt}/1, baseline-vs-candidate maxd {(rtMaxd == int.MaxValue ? "n/a" : rtMaxd)}");
Console.WriteLine($"[vs] backbuffer:    baseline {bBb}/1, candidate {cBb}/1, baseline-vs-candidate maxd {(bbMaxd == int.MaxValue ? "n/a" : bbMaxd)}");

// Pass = all four render AND the candidate matches the mgfxc baseline pixel-for-pixel
// (tolerance 1/255, the established rung-4 bar) in BOTH modes — same-backend GL<->GL.
bool pass = bRt == 1 && cRt == 1 && bBb == 1 && cBb == 1 && rtMaxd <= 1 && bbMaxd <= 1;
Console.WriteLine($"[vs] verdict: {(pass ? "PASS" : "FAIL")}");
return pass ? 0 : 1;
