// Phase 28 DX confirmation for the VS-driven effect, plus (Phase 51 A3) the Apos.Shapes
// DirectX render-proof.
//
// mode "vs" (default): compiles the VS-driven fixture with ShadowDusk for DirectX
// (candidate) and loads the mgfxc DirectX_11 golden (baseline), renders BOTH through the
// identical custom vertex-buffer draw path in the real MonoGame.Framework.WindowsDX (DX11)
// runtime, and pixel-compares each ShadowDusk arm against the golden IN PROCESS (same-backend
// DX<->DX, tolerance 4/255). The candidate is compiled with BOTH DXBC backends: the
// d3dcompiler_47 oracle (default) and the cross-platform vkd3d-shader backend (the shipping
// reach backend), each rendered to its own folder so both are proven loadable + correct.
//
// mode "apos": the Phase 51 A3 DX slice — Apos.Shapes (Gum's SDF shape renderer), the same
// apos-shapes-sm6.fx fixture the Vulkan gate (validation/VsDrivenVulkan -- apos) uses. Its
// `#else` shader-model branch (not __KNIFX__, not OPENGL, not SM6) is vs_4_0/ps_4_0 with the
// legacy sampler/tex2D syntax, which is exactly the branch a DirectX_11-profile compile takes
// (PlatformMacros.For(DirectX) = {MGFX, HLSL, SM4}, no OPENGL/SM6/__KNIFX__), so DX needs no
// separate fixture variant — since Phase 55 rendered through the real Apos.Shapes package's
// ShapeBatch gallery (AposGalleryRenderer; the RunAposPhase remarks below cover the two baselines).
//
// dotnet run --project validation/VsDrivenDx            -> the VS rig
// dotnet run --project validation/VsDrivenDx -- apos     -> the Apos.Shapes DX render-proof

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation.Dx;

string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "vs";
if (mode is not ("vs" or "apos"))
{
    Console.Error.WriteLine($"unknown mode '{mode}' — expected 'vs' or 'apos'");
    return 2;
}

string repoRoot = FindRepoRoot();

if (mode == "vs")
    return await RunVsPhase();

return await RunAposPhase();

// ---------------------------------------------------------------------------------------
// Phase 28 — the simple VS rig (POSITION/COLOR/TEXCOORD + a float4x4), vs the mgfxc golden.
// ---------------------------------------------------------------------------------------
async Task<int> RunVsPhase()
{
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
}

// ---------------------------------------------------------------------------------------
// Phase 55 — Apos.Shapes FULL SHAPE-GALLERY render-proof: the DX analogue of
// VsDrivenVulkan's/VsDrivenDx12's "apos" phase. Renders every ShapeBatch public
// Draw*/Fill*/Border* shape method through the REAL Apos.Shapes NuGet package (not a
// hand-rolled vertex harness). Candidates are ShadowDusk's compile of the SAME upstream
// shader revision (apos-shapes-sm6.fx, confirmed byte-identical to the NuGet's pinned
// commit modulo one comment — see NOTICE.md), through BOTH DXBC backends (d3dcompiler_47
// oracle, vkd3d-shader).
//
// TWO DIFFERENT baselines are used, deliberately, not one:
//  - "baseline-embedded" is Apos.Shapes' own embedded, precompiled DX11 effect (loaded via
//    ShapeBatch(GraphicsDevice), no local compile needed). Disassembling it (2026-07-23)
//    found its header literally says "Generated by vkd3d-shader 1.17" — it is NOT an
//    mgfxc/fxc/d3dcompiler_47 artifact. So it is the correct baseline for the vkd3d-shader
//    candidate (same toolchain family — maxd 0 is a meaningful match) but NOT a valid oracle
//    for the d3dcompiler_47 candidate (comparing two independent compiler implementations).
//  - "baseline-real-mgfxc" is the genuine, locally-generated `mgfxc /Profile:DirectX_11`
//    golden already checked in at tests/fixtures/golden/DirectX_11/apos-shapes-sm6.mgfx
//    (the one Phase 51 A3's single-shape proof used, at maxd 0). This is the correct oracle
//    reference for the d3dcompiler_47 candidate.
// ---------------------------------------------------------------------------------------
async Task<int> RunAposPhase()
{
const string AposFixture = "apos-shapes-sm6";

string aposFx = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "third-party", "Apos.Shapes", AposFixture + ".fx");
string realGoldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_11", AposFixture + ".mgfx");
string aposOutBase = Path.Combine(repoRoot, "validation", "output-apos-dx");

Console.WriteLine($"[apos-dx] fixture: {aposFx}\n");

string aposSrc = await File.ReadAllTextAsync(aposFx);

async Task<(byte[]? Bytes, string? Err)> CompileAposDx(DxbcBackend backend)
{
    var compiler = new EffectCompiler();
    var r = await compiler.CompileAsync(aposSrc, new CompilerOptions
    {
        Target = PlatformTarget.DirectX,
        DxbcBackend = backend,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = aposFx,
    });
    return r.IsFailure
        ? (null, string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}")))
        : (r.Value.Data, null);
}

var (oracleBytes, oracleErr) = await CompileAposDx(DxbcBackend.D3DCompiler);
var (vkd3dBytes, vkd3dErr) = await CompileAposDx(DxbcBackend.Vkd3d);

Console.WriteLine($"[apos-dx] candidate-oracle: {(oracleBytes is null ? "FAIL: " + oracleErr : oracleBytes.Length + " bytes")}");
Console.WriteLine($"[apos-dx] candidate-vkd3d:  {(vkd3dBytes is null ? "FAIL: " + vkd3dErr : vkd3dBytes.Length + " bytes")}\n");

byte[]? realGoldenBytes = File.Exists(realGoldenPath) ? await File.ReadAllBytesAsync(realGoldenPath) : null;
string? realGoldenErr = realGoldenBytes is null ? $"golden not found: {realGoldenPath}" : null;

var arms = new List<ShadowDusk.Validation.AposGallery.AposGalleryRenderer.Arm>
{
    new("baseline-embedded", UseEmbeddedGolden: true, EffectBytes: null, CompileError: null),
    new("baseline-real-mgfxc", UseEmbeddedGolden: false, realGoldenBytes, realGoldenErr),
    new("candidate-oracle", UseEmbeddedGolden: false, oracleBytes, oracleErr),
    new("candidate-vkd3d", UseEmbeddedGolden: false, vkd3dBytes, vkd3dErr),
};

using var game = new ShadowDusk.Validation.AposGallery.AposGalleryRenderer(aposOutBase, arms);
game.Run();

Console.WriteLine("[apos-dx] load + render results:");
foreach (var o in game.Outcomes)
    Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-16} {o.Error ?? "rendered"}");

var caps = game.Captures.ToDictionary(c => c.Name, c => c);

int CompareTo(string baselineLabel, string label, int tolerance)
{
    if (!caps.TryGetValue(baselineLabel, out var baseline) || !caps.TryGetValue(label, out var candidate))
    {
        Console.WriteLine($"  [compare] {label} vs {baselineLabel}: FAIL (missing render)");
        return 1;
    }
    int maxd = MaxDelta(baseline, candidate);
    bool drew = HasVisibleContent(candidate);
    bool pass = maxd <= tolerance && drew;
    Console.WriteLine($"  [compare] {label} vs {baselineLabel}: maxd={maxd} visibleContent={drew} -> {(pass ? "PASS" : "FAIL")} (tol {tolerance})");
    if (maxd > 0)
        foreach (var (name, cellMaxd) in ShadowDusk.Validation.AposGallery.AposGalleryRenderer.CellDeltas(baseline.Pixels, candidate.Pixels, baseline.Width))
            if (cellMaxd > 0)
                Console.WriteLine($"    [cell] {name,-28} maxd={cellMaxd}");
    return pass ? 0 : 1;
}

// The real oracle comparison: d3dcompiler_47 candidate vs the genuine locally-generated
// mgfxc golden (same evidence bar Phase 51 A3 used) — tolerance 0.
int cmpOracle = CompareTo("baseline-real-mgfxc", "candidate-oracle", tolerance: 0);
// The vkd3d-shader candidate vs Apos.Shapes' own embedded (also vkd3d-shader-compiled)
// effect — a legitimate same-toolchain comparison — tolerance 0.
int cmpVkd3d = CompareTo("baseline-embedded", "candidate-vkd3d", tolerance: 0);

bool allLoaded = game.Outcomes.All(o => o is { Loaded: true, Rendered: true });
bool allMatch = cmpOracle == 0 && cmpVkd3d == 0;
Console.WriteLine($"\n[apos-dx] {(allLoaded && allMatch ? "PASS" : "FAIL")} — load+render {(allLoaded ? "4/4" : "<4")}, pixel-match vs golden {(allMatch ? "OK" : "DIVERGED")}.");
return (allLoaded && allMatch) ? 0 : 1;
}

static int MaxDelta(
    (string Name, Color[] Pixels, int Width, int Height) a,
    (string Name, Color[] Pixels, int Width, int Height) b)
{
    if (a.Width != b.Width || a.Height != b.Height)
        return int.MaxValue;

    int maxd = 0;
    for (int i = 0; i < a.Pixels.Length; i++)
    {
        maxd = Math.Max(maxd, Math.Abs(a.Pixels[i].R - b.Pixels[i].R));
        maxd = Math.Max(maxd, Math.Abs(a.Pixels[i].G - b.Pixels[i].G));
        maxd = Math.Max(maxd, Math.Abs(a.Pixels[i].B - b.Pixels[i].B));
        maxd = Math.Max(maxd, Math.Abs(a.Pixels[i].A - b.Pixels[i].A));
    }
    return maxd;
}

// "Visible" = at least 1% of pixels are non-transparent AND not pure black — the same
// non-vacuity bar VsDrivenVulkan's Apos.Shapes phase uses.
static bool HasVisibleContent((string Name, Color[] Pixels, int Width, int Height) c)
{
    int visible = c.Pixels.Count(p => p.A > 8 && (p.R > 8 || p.G > 8 || p.B > 8));
    return visible > c.Pixels.Length / 100;
}

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
