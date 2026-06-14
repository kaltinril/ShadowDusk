// Issue #70 follow-up - VS-DRIVEN render proof in REAL KNI (v4.2.9001), not MonoGame.
//
// squarebananas reported incorrect vertex positions on KNI/OpenGL. The fix is in the default
// v10 GL bytes (same bytes MonoGame and KNI both load via MojoShader), and validation/VsDriven
// proves it in MonoGame. This renders the SAME fixtures in real KNI and compares ShadowDusk's
// output to the mgfxc OpenGL golden pixel-for-pixel (GL<->GL, in-process) - the rung-4 bar on
// the KNI runtime. Covers both the asymmetric-matrix transform and the legacy ': POSITION' form.
//
// Run: dotnet run --project validation/KniVsDriven   (needs a real GL desktop driver)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

// ---- Runtime-integrity guard: must be KNI (nkast 'Xna.Framework.*'), not MonoGame ----------
AssemblyName xna = typeof(Game).Assembly.GetName();
Console.WriteLine($"[kni-vs] XNA implementation: {xna.Name} {xna.Version}");
bool isKni = xna.Name is not null
    && xna.Name.StartsWith("Xna.Framework", StringComparison.OrdinalIgnoreCase)
    && !xna.Name.StartsWith("MonoGame", StringComparison.OrdinalIgnoreCase);
if (!isKni)
{
    Console.WriteLine($"[kni-vs] verdict: FAIL - expected the KNI runtime, got '{xna.Name}'. Aborting.");
    return 2;
}

string repoRoot = ShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL");
string catPath = ShaderInputs.CatPath(repoRoot);
string outBase = Path.Combine(repoRoot, "validation", "output-vs-kni");

const string fixture = "VsTransformColorTexture";
const string legacyFixture = "VsTransformColorTextureLegacyPos";

Console.WriteLine($"[kni-vs] fixture: {fixture} (+ legacy ': POSITION' variant)\n");

async Task<(byte[]? Bytes, string? Err)> CompileGl(string name)
{
    var compiler = new EffectCompiler();
    string path = Path.Combine(shaderDir, name + ".fx");
    var result = await compiler.CompileAsync(await File.ReadAllTextAsync(path), new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = path,
    });
    return result.IsFailure
        ? (null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")))
        : (result.Value.Data, null);
}

var (candidateBytes, candidateErr) = await CompileGl(fixture);
var (legacyBytes, legacyErr) = await CompileGl(legacyFixture);

// Baseline: the mgfxc OpenGL golden bytes (the reference compiler).
string goldenPath = Path.Combine(goldenDir, fixture + ".mgfx");
byte[]? baselineBytes = File.Exists(goldenPath) ? await File.ReadAllBytesAsync(goldenPath) : null;
string? baselineErr = baselineBytes is null ? $"golden not found: {goldenPath}" : null;

Console.WriteLine($"[kni-vs] candidate (ShadowDusk): {(candidateBytes is null ? "FAIL: " + candidateErr : candidateBytes.Length + " bytes")}");
Console.WriteLine($"[kni-vs] baseline  (mgfxc golden): {(baselineBytes is null ? baselineErr : baselineBytes.Length + " bytes")}");
Console.WriteLine($"[kni-vs] legacy ': POSITION':     {(legacyBytes is null ? "FAIL: " + legacyErr : legacyBytes.Length + " bytes")}\n");

(int Ok, (string Name, Color[] Pixels, int W, int H)? Capture)
    Render(string label, byte[]? bytes, string? err, bool backbuffer)
{
    var jobs = new List<ShaderJob> { new(fixture, bytes, err) };
    using var game = new VsEffectImageRenderer(catPath, Path.Combine(outBase, label), jobs, renderToBackbuffer: backbuffer);
    game.Run();
    int ok = 0;
    foreach (var o in game.Outcomes)
    {
        string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
        if (status == "OK  ") ok++;
        Console.WriteLine($"  [{label}] [{status}] {o.Name,-26} {(o.Error ?? o.PngPath)}");
    }
    return (ok, game.Captures.Count > 0 ? game.Captures[0] : null);
}

int MaxDelta((string, Color[], int, int)? a, (string, Color[], int, int)? b)
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

// Render-target mode (posFixup.y = -1) and backbuffer mode (posFixup.y = +1, the real game case).
var (bRt, bRtCap) = Render("baseline",  baselineBytes,  baselineErr,  backbuffer: false);
var (cRt, cRtCap) = Render("candidate", candidateBytes, candidateErr, backbuffer: false);
var (bBb, bBbCap) = Render("baseline-backbuffer",  baselineBytes,  baselineErr,  backbuffer: true);
var (cBb, cBbCap) = Render("candidate-backbuffer", candidateBytes, candidateErr, backbuffer: true);
var (lRt, lRtCap) = Render("candidate-legacypos",  legacyBytes,    legacyErr,    backbuffer: false);

int rtMaxd = MaxDelta(bRtCap, cRtCap);
int bbMaxd = MaxDelta(bBbCap, cBbCap);
int legacyMaxd = MaxDelta(cRtCap, lRtCap);

Console.WriteLine($"\n[kni-vs] render-target: baseline {bRt}/1, candidate {cRt}/1, ShadowDusk-vs-mgfxc maxd {(rtMaxd == int.MaxValue ? "n/a" : rtMaxd)}");
Console.WriteLine($"[kni-vs] backbuffer:    baseline {bBb}/1, candidate {cBb}/1, ShadowDusk-vs-mgfxc maxd {(bbMaxd == int.MaxValue ? "n/a" : bbMaxd)}");
Console.WriteLine($"[kni-vs] legacy-pos:    legacy {lRt}/1, legacy-vs-true-SV maxd {(legacyMaxd == int.MaxValue ? "n/a" : legacyMaxd)}");

// Pass = every render loads + draws AND ShadowDusk matches the mgfxc golden (<= 1/255, the rung-4
// bar) in BOTH modes, AND the legacy ': POSITION' form matches the true-SV candidate - all in KNI.
bool pass = bRt == 1 && cRt == 1 && bBb == 1 && cBb == 1 && rtMaxd <= 1 && bbMaxd <= 1
            && lRt == 1 && legacyMaxd <= 1;
Console.WriteLine($"\n[kni-vs] verdict: {(pass ? "PASS - issue #70 fix render-proven in real KNI v" + xna.Version : "FAIL")}");
return pass ? 0 : 1;
