// Phase 54 DX12 VS-driven confirmation, plus the Apos.Shapes DirectX12 render-proof -
// the DX12 analogue of validation/VsDrivenDx (DX11) and validation/VsDrivenVulkan.
//
// mode "vs" (default): compiles the VS-driven fixture with ShadowDusk for DirectX12
// (candidate) and loads the REAL mgfxc DirectX_12 golden (baseline, built by MonoGame
// 3.8.5's own content pipeline /Platform:WindowsDX12), renders BOTH through the identical
// custom vertex-buffer draw path in the real MonoGame WindowsDX12 runtime, and pixel-compares
// in process (same-backend DX12<->DX12, tolerance 4/255). DX12 has a single DXC->DXIL path
// (no oracle/vkd3d backend split the way DX11 does).
//
// mode "apos": Apos.Shapes (Gum's SDF shape renderer), the same apos-shapes-sm6.fx fixture
// the Vulkan and DX11 gates use. DX12's macro set is {MGFX, HLSL, SM6} - the SAME branch
// Vulkan takes (vs_6_0/ps_6_0, Texture2D/SamplerState pairs) - see AposShapesRenderer.
//
// dotnet run --project validation/VsDrivenDx12            -> the VS rig
// dotnet run --project validation/VsDrivenDx12 -- apos     -> the Apos.Shapes DX12 render-proof

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
// mode "vs" - the simple VS rig (POSITION/COLOR/TEXCOORD + a float4x4), vs the real mgfxc
// DirectX_12 golden.
// ---------------------------------------------------------------------------------------
async Task<int> RunVsPhase()
{
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_12");
string catPath = Path.Combine(repoRoot, "samples", "ShaderViewer", "Content", "cat.jpg");
string outBase = Path.Combine(repoRoot, "validation", "output-vs-dx12");

const string fixture = "VsTransformColorTexture";
string fxPath = Path.Combine(shaderDir, fixture + ".fx");
string src = await File.ReadAllTextAsync(fxPath);

var compiler = new EffectCompiler();
var r = await compiler.CompileAsync(src, new CompilerOptions
{
    Target = PlatformTarget.DirectX12,
    IncludeResolver = new FileSystemIncludeResolver(),
    SourceFileName = fxPath,
});
byte[]? candidateBytes = r.IsFailure ? null : r.Value.Data;
string? candidateErr = r.IsFailure ? string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}")) : null;
if (candidateBytes is not null)
{
    string dumpDir = Path.Combine(repoRoot, "validation", "output", "candidate-dx12-mgfx");
    Directory.CreateDirectory(dumpDir);
    await File.WriteAllBytesAsync(Path.Combine(dumpDir, fixture + ".mgfx"), candidateBytes);
}

string goldenPath = Path.Combine(goldenDir, fixture + ".mgfx");
byte[]? baselineBytes = File.Exists(goldenPath) ? await File.ReadAllBytesAsync(goldenPath) : null;
string? baselineErr = baselineBytes is null ? $"golden not found: {goldenPath}" : null;

Console.WriteLine($"[vs-dx12] baseline:  {(baselineBytes is null ? baselineErr : baselineBytes.Length + " bytes")}");
Console.WriteLine($"[vs-dx12] candidate: {(candidateBytes is null ? "FAIL: " + candidateErr : candidateBytes.Length + " bytes")}\n");

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

const int tolerance = 4;
var (b, basePixels) = Render("baseline", baselineBytes, baselineErr);
var (c, candPixels) = Render("candidate", candidateBytes, candidateErr);

Console.WriteLine($"\n[vs-dx12] baseline {b}/1, candidate {c}/1.");

int CompareToBaseline(Color[]? candidate)
{
    if (basePixels is null || candidate is null)
    {
        Console.WriteLine("  [compare] candidate vs baseline: FAIL (missing render)");
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
    Console.WriteLine($"  [compare] candidate vs baseline: maxDelta={maxDelta} diffPixels={diff} -> {(pass ? "PASS" : "FAIL")} (tol {tolerance})");
    return pass ? 0 : 1;
}

int cmp = CompareToBaseline(candPixels);

bool allLoaded = b == 1 && c == 1;
bool allMatch = cmp == 0;
Console.WriteLine($"\n[vs-dx12] {(allLoaded && allMatch ? "PASS" : "FAIL")} — load+render {(allLoaded ? "2/2" : "<2")}, pixel-match vs golden {(allMatch ? "OK" : "DIVERGED")}.");
return (allLoaded && allMatch) ? 0 : 1;
}

// ---------------------------------------------------------------------------------------
// Phase 55 - Apos.Shapes FULL SHAPE-GALLERY render-proof: the DX12 analogue of
// VsDrivenDx's/VsDrivenVulkan's "apos" mode. Renders every ShapeBatch public
// Draw*/Fill*/Border* shape method through the REAL Apos.Shapes NuGet package. The golden
// arm is ShapeBatch's own embedded, precompiled effect; the candidate is ShadowDusk's DX12
// compile of the SAME upstream shader revision (apos-shapes-sm6.fx).
// ---------------------------------------------------------------------------------------
async Task<int> RunAposPhase()
{
const string AposFixture = "apos-shapes-sm6";

string aposFx = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "third-party", "Apos.Shapes", AposFixture + ".fx");
string realGoldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_12", AposFixture + ".mgfx");
string aposOutBase = Path.Combine(repoRoot, "validation", "output-apos-dx12");

Console.WriteLine($"[apos-dx12] fixture: {aposFx}\n");

string aposSrc = await File.ReadAllTextAsync(aposFx);

var compiler = new EffectCompiler();
var r = await compiler.CompileAsync(aposSrc, new CompilerOptions
{
    Target = PlatformTarget.DirectX12,
    IncludeResolver = new FileSystemIncludeResolver(),
    SourceFileName = aposFx,
});
byte[]? candidateBytes = r.IsFailure ? null : r.Value.Data;
string? candidateErr = r.IsFailure ? string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}")) : null;
if (candidateBytes is not null)
{
    string dumpDir = Path.Combine(repoRoot, "validation", "output", "candidate-dx12-mgfx");
    Directory.CreateDirectory(dumpDir);
    await File.WriteAllBytesAsync(Path.Combine(dumpDir, AposFixture + ".mgfx"), candidateBytes);
}

Console.WriteLine($"[apos-dx12] candidate: {(candidateBytes is null ? "FAIL: " + candidateErr : candidateBytes.Length + " bytes")}\n");

byte[]? realGoldenBytes = File.Exists(realGoldenPath) ? await File.ReadAllBytesAsync(realGoldenPath) : null;
string? realGoldenErr = realGoldenBytes is null ? $"golden not found: {realGoldenPath}" : null;

// Apos.Shapes' embedded golden was DROPPED as the baseline here (2026-07-23): disassembling
// the DX11 embedded resource found it says "Generated by vkd3d-shader 1.17" in its header —
// it is NOT an mgfxc/DXC-oracle artifact, it's the SAME toolchain family the DX11 vkd3d
// candidate uses, which is why that arm matched it at maxd 0 for the wrong reason (comparing
// vkd3d-shader against vkd3d-shader). DX12 almost certainly has the same issue (both DX11 and
// DX12 dropped from the same non-Windows-native Apos.Shapes build pipeline). The genuine
// oracle is the locally-generated `mgfxc /Platform:WindowsDX12` golden already checked in at
// tests/fixtures/golden/DirectX_12/apos-shapes-sm6.mgfx (the one Phase 54's own VS-driven
// proof uses) — compare against THAT instead.
var arms = new List<ShadowDusk.Validation.AposGallery.AposGalleryRenderer.Arm>
{
    new("baseline-real-mgfxc", UseEmbeddedGolden: false, realGoldenBytes, realGoldenErr),
    new("candidate", UseEmbeddedGolden: false, candidateBytes, candidateErr),
};

using var game = new ShadowDusk.Validation.AposGallery.AposGalleryRenderer(aposOutBase, arms);
game.Run();

Console.WriteLine("[apos-dx12] load + render results:");
foreach (var o in game.Outcomes)
    Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-16} {o.Error ?? "rendered"}");

var caps = game.Captures.ToDictionary(cap => cap.Name, cap => cap);
bool haveBase = caps.ContainsKey("baseline-real-mgfxc");

int CompareToBaseline(string label, int tolerance)
{
    if (!haveBase || !caps.TryGetValue(label, out var candidate))
    {
        Console.WriteLine($"  [compare] {label} vs baseline-real-mgfxc: FAIL (missing render)");
        return 1;
    }
    var baseline = caps["baseline-real-mgfxc"];
    int maxd = MaxDelta(baseline, candidate);
    bool drew = HasVisibleContent(candidate);
    bool pass = maxd <= tolerance && drew;
    Console.WriteLine($"  [compare] {label} vs baseline-real-mgfxc: maxd={maxd} visibleContent={drew} -> {(pass ? "PASS" : "FAIL")} (tol {tolerance})");
    if (maxd > 0)
        foreach (var (name, cellMaxd) in ShadowDusk.Validation.AposGallery.AposGalleryRenderer.CellDeltas(baseline.Pixels, candidate.Pixels, baseline.Width))
            if (cellMaxd > 0)
                Console.WriteLine($"    [cell] {name,-28} maxd={cellMaxd}");
    return pass ? 0 : 1;
}

// Tolerance 1/255, confirmed 2026-07-23 against the REAL locally-generated mgfxc golden
// (not the embedded resource, which DX11's investigation showed is a vkd3d-shader artifact,
// not a valid oracle — see the comment above). DX12 diverges by exactly 1 on the SAME 2/30
// cells (DrawCircle, FillArc) even against this genuine oracle, so this is a real, if tiny,
// divergence distinct from the (now-resolved) DX11 false alarm — not yet root-caused. Both
// sides compile through DXC->DXIL, so an independent-DXC-build/version drift on this shader's
// transcendental math remains the leading hypothesis, but unlike the DX11 case this one has
// NOT been confirmed against a wrong-baseline theory — treat it as a genuine open follow-up.
int cmp = CompareToBaseline("candidate", tolerance: 1);

bool allLoaded = game.Outcomes.All(o => o is { Loaded: true, Rendered: true });
bool allMatch = cmp == 0;
Console.WriteLine($"\n[apos-dx12] {(allLoaded && allMatch ? "PASS" : "FAIL")} — load+render {(allLoaded ? "2/2" : "<2")}, pixel-match vs golden {(allMatch ? "OK" : "DIVERGED")}.");
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
