// Phase 28 DX confirmation for the VS-driven effect.
//
// Compiles the VS-driven fixture with ShadowDusk for DirectX (candidate) and loads
// the mgfxc DirectX_11 golden (baseline), renders BOTH through the identical custom
// vertex-buffer draw path in the real MonoGame.Framework.WindowsDX (DX11) runtime,
// and pixel-compares each ShadowDusk arm against the golden IN PROCESS (same-backend
// DX↔DX, tolerance 4/255). Exit 0 iff every arm loads + renders AND matches the golden.
//
// The candidate is compiled with BOTH DXBC backends: the d3dcompiler_47 oracle
// (default) and the cross-platform vkd3d-shader backend (the shipping reach backend),
// each rendered to its own folder so both are proven loadable + correct.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation.Dx;

string repoRoot = FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_11");
string catPath = Path.Combine(repoRoot, "samples", "ShaderViewer", "Content", "cat.jpg");
string outBase = Path.Combine(repoRoot, "validation", "output-vs-dx");

const string fixture = "VsTransformColorTexture";
string fxPath = Path.Combine(shaderDir, fixture + ".fx");
string src = await File.ReadAllTextAsync(fxPath);

async Task<(byte[]? Bytes, string? Err)> CompileDx(DxbcBackend backend)
{
    var compiler = new EffectCompiler();
    var r = await compiler.CompileAsync(src, new CompilerOptions
    {
        Target = PlatformTarget.DirectX,
        DxbcBackend = backend,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fxPath,
    });
    return r.IsFailure
        ? (null, string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}")))
        : (r.Value.Data, null);
}

var (oracleBytes, oracleErr) = await CompileDx(DxbcBackend.D3DCompiler);
var (vkd3dBytes, vkd3dErr) = await CompileDx(DxbcBackend.Vkd3d);

string goldenPath = Path.Combine(goldenDir, fixture + ".mgfx");
byte[]? baselineBytes = File.Exists(goldenPath) ? await File.ReadAllBytesAsync(goldenPath) : null;
string? baselineErr = baselineBytes is null ? $"golden not found: {goldenPath}" : null;

Console.WriteLine($"[vs-dx] baseline:  {(baselineBytes is null ? baselineErr : baselineBytes.Length + " bytes")}");
Console.WriteLine($"[vs-dx] oracle:    {(oracleBytes is null ? "FAIL: " + oracleErr : oracleBytes.Length + " bytes")}");
Console.WriteLine($"[vs-dx] vkd3d:     {(vkd3dBytes is null ? "FAIL: " + vkd3dErr : vkd3dBytes.Length + " bytes")}\n");

(int Ok, Color[]? Pixels) Render(string label, byte[]? bytes, string? err)
{
    var jobs = new List<ShaderJob> { new(fixture, bytes, err) };
    using var game = new VsDxEffectImageRenderer(catPath, Path.Combine(outBase, label), jobs);
    game.Run();
    int ok = 0;
    foreach (var o in game.Outcomes)
    {
        string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
        if (status == "OK  ") ok++;
        Console.WriteLine($"  [{label}] [{status}] {o.Name,-24} {(o.Error ?? o.PngPath)}");
    }
    game.RenderedPixels.TryGetValue(fixture, out var px);
    return (ok, px);
}

const int tolerance = 4; // Phase 18 DX bar (per-channel)
var (b, basePixels) = Render("baseline", baselineBytes, baselineErr);
var (o, oraclePixels) = Render("candidate-oracle", oracleBytes, oracleErr);
var (v, vkPixels) = Render("candidate-vkd3d", vkd3dBytes, vkd3dErr);

Console.WriteLine($"\n[vs-dx] baseline {b}/1, oracle {o}/1, vkd3d {v}/1.");

// In-process pixel comparison vs the mgfxc golden (same backend, DX↔DX): each ShadowDusk
// arm must MATCH the golden within tolerance — proving the VS-driven effect renders
// equivalently, not merely that it loads. A missing arm is a failure, never a skip-as-pass.
int CompareToBaseline(string label, Color[]? candidate)
{
    if (basePixels is null || candidate is null)
    {
        Console.WriteLine($"  [compare] {label} vs baseline: FAIL (missing render — baseline or candidate did not produce pixels)");
        return 1;
    }
    int maxDelta = 0, diff = 0;
    for (int i = 0; i < basePixels.Length; i++)
    {
        int d = Math.Max(
            Math.Max(Math.Abs(basePixels[i].R - candidate[i].R), Math.Abs(basePixels[i].G - candidate[i].G)),
            Math.Max(Math.Abs(basePixels[i].B - candidate[i].B), Math.Abs(basePixels[i].A - candidate[i].A)));
        if (d > 0) diff++;
        if (d > maxDelta) maxDelta = d;
    }
    bool pass = maxDelta <= tolerance;
    Console.WriteLine($"  [compare] {label} vs baseline: maxDelta={maxDelta} diffPixels={diff} -> {(pass ? "PASS" : "FAIL")} (tol {tolerance})");
    return pass ? 0 : 1;
}

int cmpOracle = CompareToBaseline("candidate-oracle", oraclePixels);
int cmpVkd3d = CompareToBaseline("candidate-vkd3d", vkPixels);

bool allLoaded = b == 1 && o == 1 && v == 1;
bool allMatch = cmpOracle == 0 && cmpVkd3d == 0;
Console.WriteLine($"\n[vs-dx] {(allLoaded && allMatch ? "PASS" : "FAIL")} — load+render {(allLoaded ? "3/3" : "<3")}, pixel-match vs golden {(allMatch ? "OK" : "DIVERGED")}.");
return (allLoaded && allMatch) ? 0 : 1;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate repo root (ShadowDusk.slnx).");
}
