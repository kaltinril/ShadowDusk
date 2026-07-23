// Phase 28 rung-4 validation for a VS-DRIVEN effect.
//
// mode "vs" (default): compiles the VS-driven fixture with ShadowDusk (candidate) AND loads
// the mgfxc OpenGL golden (baseline) for the SAME .fx, renders BOTH through the identical
// custom vertex-buffer draw path (VsEffectImageRenderer), and reports each side's load+render
// result. A separate compare step (validation/compare.py) diffs the two PNGs pixel-for-pixel —
// same-backend GL<->GL, the rung-4 bar.
//
// mode "apos": the Phase 51 A3 GL slice — Apos.Shapes (Gum's SDF shape renderer), the same
// apos-shapes-sm6.fx fixture the DX (validation/VsDrivenDx -- apos) and Vulkan
// (validation/VsDrivenVulkan -- apos) gates use. OpenGL's macro set is {MGFX, GLSL, OPENGL}
// (PlatformMacros.For(OpenGL); no SM4/SM6/__KNIFX__), so the fixture's `#elif OPENGL` branch
// applies — cosmetically vs_3_0/ps_3_0, but the sampler declarations are gated on a separate
// `#if SM6` that OPENGL never takes, so GL lands on the same legacy sampler/tex2D branch DX
// does. See AposShapesRenderer for the bespoke vertex-buffer harness.
//
// dotnet run --project validation/VsDriven            -> the VS rig
// dotnet run --project validation/VsDriven -- apos     -> the Apos.Shapes GL render-proof

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "vs";
if (mode is not ("vs" or "apos"))
{
    Console.Error.WriteLine($"unknown mode '{mode}' — expected 'vs' or 'apos'");
    return 2;
}

string repoRoot = ShaderInputs.FindRepoRoot();

if (mode == "apos")
    return await RunAposPhase();

return await RunVsPhase();

// ---------------------------------------------------------------------------------------
// Phase 28 — the simple VS rig (POSITION/COLOR/TEXCOORD + a float4x4), vs the mgfxc golden.
// ---------------------------------------------------------------------------------------
async Task<int> RunVsPhase()
{
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

// ---- Issue #70 follow-up: the LEGACY ': POSITION' vertex-output variant. Same contract,
// but the VS position output uses the D3D9 POSITION semantic (the stock MonoGame GL template
// form). ShadowDusk's DXC frontend makes `: POSITION` a user varying; the rewriter must remap
// it to gl_Position or the geometry is silently broken. Rendered in real MonoGame and compared
// to the (golden-proven) true-SV_Position candidate below. ----
const string legacyFixture = "VsTransformColorTextureLegacyPos";
byte[]? legacyBytes = null;
string? legacyErr = null;
{
    var compiler = new EffectCompiler();
    string legacyPath = Path.Combine(shaderDir, legacyFixture + ".fx");
    var result = await compiler.CompileAsync(await File.ReadAllTextAsync(legacyPath), new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = legacyPath,
    });
    if (result.IsFailure)
        legacyErr = string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));
    else
        legacyBytes = result.Value.Data;
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

// ---- Legacy ': POSITION' variant: render in real MonoGame, compare to the true-SV_Position
// candidate (which is itself proven == the mgfxc golden above). Equal pixels prove the legacy
// form both LOADS and renders correctly through the POSITION->gl_Position mapping. ----
var (lRt, lRtCap) = Render("candidate-legacypos", legacyBytes, legacyErr, backbuffer: false);
int legacyMaxd = MaxDelta(cRtCap, lRtCap);
Console.WriteLine($"[vs] legacy ': POSITION' bytes: {(legacyBytes is null ? "COMPILE FAIL: " + legacyErr : legacyBytes.Length + " bytes")}");

Console.WriteLine($"\n[vs] render-target: baseline {bRt}/1, candidate {cRt}/1, baseline-vs-candidate maxd {(rtMaxd == int.MaxValue ? "n/a" : rtMaxd)}");
Console.WriteLine($"[vs] backbuffer:    baseline {bBb}/1, candidate {cBb}/1, baseline-vs-candidate maxd {(bbMaxd == int.MaxValue ? "n/a" : bbMaxd)}");
Console.WriteLine($"[vs] legacy-pos:    legacy {lRt}/1, legacy-vs-true-SV maxd {(legacyMaxd == int.MaxValue ? "n/a" : legacyMaxd)}");

// Pass = all four render AND the candidate matches the mgfxc baseline pixel-for-pixel
// (tolerance 1/255, the established rung-4 bar) in BOTH modes — same-backend GL<->GL — AND the
// legacy ': POSITION' variant loads, renders, and matches the true-SV_Position candidate.
bool pass = bRt == 1 && cRt == 1 && bBb == 1 && cBb == 1 && rtMaxd <= 1 && bbMaxd <= 1
            && lRt == 1 && legacyMaxd <= 1;
Console.WriteLine($"[vs] verdict: {(pass ? "PASS" : "FAIL")}");
return pass ? 0 : 1;
}

// ---------------------------------------------------------------------------------------
// Phase 51 A3 — Apos.Shapes OpenGL render-proof: the GL analogue of VsDrivenDx's and
// VsDrivenVulkan's "apos" phase, but NOT the same fixture. `apos-shapes-sm6.fx` (the DX/Vulkan
// fixture) compiles fine on GL, but the real mgfxc OpenGL golden's compiled output diverges
// completely (maxd 255, solid black) for a confirmed reason unrelated to ShadowDusk: MojoShader's
// GL translation of that revision's fxc-optimized shape dispatch hinges on a
// `-0.0 >= 0.0` comparison that this GPU/driver evaluates false, permanently selecting a
// hard-zeroed color branch — a genuine mgfxc/MojoShader bug (see AposShapesRenderer's remarks
// for the full trace). `apos-shapes.fx` (the Phase 49 pin, upstream commit 3fb73b8d — the
// older, non-fxc-SM3-optimizer-mangled revision) sidesteps it: plain sequential shape dispatch,
// Cantor-pair color packing instead of the sm6 revision's base-2048 quantization. Same
// non-identity matrix discipline as DX/Vulkan; the vertex layout and packing differ because
// this is upstream's earlier VertexInput shape (10 elements, no clip-distance split).
// ---------------------------------------------------------------------------------------
async Task<int> RunAposPhase()
{
const string AposFixture = "apos-shapes";

string aposFx = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "third-party", "Apos.Shapes", AposFixture + ".fx");
string aposGoldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL");
string aposGolden = Path.Combine(aposGoldenDir, AposFixture + ".mgfx");
string aposOutBase = Path.Combine(repoRoot, "validation", "output-apos-gl");

Console.WriteLine($"[apos-gl] fixture: {aposFx}");
Console.WriteLine($"[apos-gl] golden:  {aposGolden}\n");

string aposSrc = await File.ReadAllTextAsync(aposFx);

byte[]? aposCandidate = null;
string? aposCandErr = null;
{
    var compiler = new EffectCompiler();
    var result = await compiler.CompileAsync(aposSrc, new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = aposFx,
    });
    if (result.IsFailure)
        aposCandErr = string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));
    else
    {
        aposCandidate = result.Value.Data;
        Directory.CreateDirectory(aposOutBase);
        await File.WriteAllBytesAsync(Path.Combine(aposOutBase, AposFixture + ".candidate.mgfx"), aposCandidate);
    }
}

byte[]? aposBaseline = File.Exists(aposGolden) ? await File.ReadAllBytesAsync(aposGolden) : null;
string? aposBaseErr = aposBaseline is null ? $"golden not found: {aposGolden}" : null;

Console.WriteLine($"[apos-gl] baseline:  {(aposBaseline is null ? aposBaseErr : aposBaseline.Length + " bytes")}");
Console.WriteLine($"[apos-gl] candidate: {(aposCandidate is null ? "FAIL: " + aposCandErr : aposCandidate.Length + " bytes")}\n");

var jobs = new List<(string Name, byte[]? Bytes, string? Error)>
{
    ("baseline-mgfxc", aposBaseline,  aposBaseErr),
    ("candidate-sd",   aposCandidate, aposCandErr),
};

using var game = new ShadowDusk.Validation.VsDriven.AposShapesRenderer(aposOutBase, jobs);
game.Run();

Console.WriteLine("[apos-gl] load + render results:");
foreach (var o in game.Outcomes)
    Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-16} {o.Error ?? "rendered"}");

var caps = game.Captures.ToDictionary(c => c.Name, c => c);
bool haveBoth = caps.ContainsKey("baseline-mgfxc") && caps.ContainsKey("candidate-sd");

int aposMaxd = haveBoth ? AposMaxDelta(caps["baseline-mgfxc"], caps["candidate-sd"]) : int.MaxValue;
bool aposDrew = caps.TryGetValue("candidate-sd", out var aposCand) && AposHasVisibleContent(aposCand);

Console.WriteLine();
Console.WriteLine($"[apos-gl] baseline-vs-candidate maxd: {(aposMaxd == int.MaxValue ? "n/a" : aposMaxd)}");
Console.WriteLine($"[apos-gl] candidate drew visible content: {aposDrew}");

// Tolerance 2/255, NOT the maxd-0 bar the DX/Vulkan Apos.Shapes gates hit: this fixture's
// SpritePixelShader always round-trips fill/border colors through RgbToOklab/OkLabToRgb
// (cube roots + fractional pow()), and measured drift here is maxd 2, only 216/16384 pixels
// (1.3%), concentrated at 1-2/255 — the documented GLSL-dialect precision drift between
// ShadowDusk's SPIRV-Cross output and mgfxc's MojoShader GLSL on transcendental math (see
// plan/DONE/PHASE-17-monogame-runtime-validation.md), not a structural mismatch. This is
// a real, explained drift, not a silently-widened bar.
const int AposTolerance = 2;

bool allLoaded = game.Outcomes.All(o => o is { Loaded: true, Rendered: true });
bool pass2 = haveBoth && aposMaxd <= AposTolerance && aposDrew;
Console.WriteLine($"\n[apos-gl] {(allLoaded && pass2 ? "PASS" : "FAIL")} — load+render {(allLoaded ? "2/2" : "<2")}, pixel-match vs golden {(pass2 ? $"OK (maxd {aposMaxd} <= {AposTolerance})" : "DIVERGED")}.");
return (allLoaded && pass2) ? 0 : 1;
}

static int AposMaxDelta(
    (string Name, Microsoft.Xna.Framework.Color[] Pixels, int Width, int Height) a,
    (string Name, Microsoft.Xna.Framework.Color[] Pixels, int Width, int Height) b)
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
// non-vacuity bar the DX and Vulkan Apos.Shapes phases use.
static bool AposHasVisibleContent((string Name, Microsoft.Xna.Framework.Color[] Pixels, int Width, int Height) c)
{
    int visible = c.Pixels.Count(p => p.A > 8 && (p.R > 8 || p.G > 8 || p.B > 8));
    return visible > c.Pixels.Length / 100;
}
