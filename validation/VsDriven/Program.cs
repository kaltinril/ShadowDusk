// Phase 28 rung-4 validation for a VS-DRIVEN effect.
//
// mode "vs" (default): compiles the VS-driven fixture with ShadowDusk (candidate) AND loads
// the mgfxc OpenGL golden (baseline) for the SAME .fx, renders BOTH through the identical
// custom vertex-buffer draw path (VsEffectImageRenderer), and reports each side's load+render
// result. A separate compare step (validation/compare.py) diffs the two PNGs pixel-for-pixel —
// same-backend GL<->GL, the rung-4 bar.
//
// mode "apos": the Phase 51 A3 GL slice — Apos.Shapes (Gum's SDF shape renderer). Compiles
// apos-shapes.fx for OpenGL and compares ShadowDusk's candidate to the mgfxc GL golden, same-
// backend GL<->GL, on two shapes: a CIRCLE (the original Phase 51 proof) and, since issue #160, a
// needle-thin ELLIPSE (the shape whose Newton/bisect SDF exercises the header-less-loop rewrite).
// The ellipse slice is supplementary — the bug it targets is undefined behavior whose pixel
// manifestation is driver-dependent (see RunAposPhase's caveat); the rewriter unit test is the
// authoritative guard. See AposShapesRenderer for the bespoke vertex-buffer harness.
//
// mode "apos-gallery": Phase 55 — renders Apos.Shapes' full ShapeBatch shape gallery through
// ShadowDusk's GL compile ONLY (no golden arm). GL gets no pixel-diff for this gallery: the
// package's own embedded GL effect drives the SAME shader revision DX/Vulkan use, and
// mgfxc's own GL compile of that revision is a confirmed MojoShader bug rendering solid
// black for every non-textured shape (Phase 51 A3, tests/fixtures/shaders/third-party/
// Apos.Shapes/NOTICE.md) — not a ShadowDusk defect, but no trustworthy GL oracle exists for
// this gallery. This mode instead asserts every gallery cell renders visible (non-black,
// non-transparent) content through ShadowDusk. It does NOT replace or touch the existing
// "apos" mode above, which stays the one pixel-diffed GL data point for Apos.Shapes.
//
// dotnet run --project validation/VsDriven                   -> the VS rig
// dotnet run --project validation/VsDriven -- apos            -> the Apos.Shapes GL render-proof (unchanged)
// dotnet run --project validation/VsDriven -- apos-gallery     -> the Phase 55 GL gallery visibility check

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "vs";
if (mode is not ("vs" or "apos" or "apos-gallery"))
{
    Console.Error.WriteLine($"unknown mode '{mode}' — expected 'vs', 'apos', or 'apos-gallery'");
    return 2;
}

string repoRoot = ShaderInputs.FindRepoRoot();

if (mode == "apos")
    return await RunAposPhase();
if (mode == "apos-gallery")
    return await RunAposGalleryPhase();

return await RunVsPhase();

// ---------------------------------------------------------------------------------------
// Phase 55 — Apos.Shapes full shape-gallery GL visibility check (candidate-only, no golden;
// see the mode "apos-gallery" remarks above for why GL has no trustworthy oracle here).
// ---------------------------------------------------------------------------------------
async Task<int> RunAposGalleryPhase()
{
const string AposFixture = "apos-shapes-sm6";

string aposFx = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "third-party", "Apos.Shapes", AposFixture + ".fx");
string aposOutBase = Path.Combine(repoRoot, "validation", "output-apos-gl-gallery");

Console.WriteLine($"[apos-gl-gallery] fixture: {aposFx}\n");

string aposSrc = await File.ReadAllTextAsync(aposFx);

byte[]? candidateBytes = null;
string? candidateErr = null;
{
    var compiler = new EffectCompiler();
    var result = await compiler.CompileAsync(aposSrc, new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = aposFx,
    });
    if (result.IsFailure)
        candidateErr = string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));
    else
        candidateBytes = result.Value.Data;
}

Console.WriteLine($"[apos-gl-gallery] candidate: {(candidateBytes is null ? "FAIL: " + candidateErr : candidateBytes.Length + " bytes")}\n");

var arms = new List<ShadowDusk.Validation.AposGallery.AposGalleryRenderer.Arm>
{
    new("candidate", UseEmbeddedGolden: false, candidateBytes, candidateErr),
};

using var game = new ShadowDusk.Validation.AposGallery.AposGalleryRenderer(aposOutBase, arms);
game.Run();

Console.WriteLine("[apos-gl-gallery] load + render results:");
foreach (var o in game.Outcomes)
    Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-16} {o.Error ?? "rendered"}");

var caps = game.Captures.ToDictionary(c => c.Name, c => c);
bool loaded = game.Outcomes.All(o => o is { Loaded: true, Rendered: true });

int failCount = 0;
if (caps.TryGetValue("candidate", out var candidate))
{
    Console.WriteLine("[apos-gl-gallery] per-shape visibility (non-black, non-transparent pixels present):");
    foreach (var (name, cell) in ShadowDusk.Validation.AposGallery.AposGalleryRenderer.Cells)
    {
        bool visible = CellHasVisibleContent(candidate.Pixels, candidate.Width, cell);
        if (!visible) failCount++;
        Console.WriteLine($"  [{(visible ? "OK  " : "FAIL")}] {name}");
    }
}
else
{
    failCount = ShadowDusk.Validation.AposGallery.AposGalleryRenderer.Cells.Count;
}

bool pass = loaded && failCount == 0;
Console.WriteLine($"\n[apos-gl-gallery] {(pass ? "PASS" : "FAIL")} — load+render {(loaded ? "OK" : "FAIL")}, {ShadowDusk.Validation.AposGallery.AposGalleryRenderer.Cells.Count - failCount}/{ShadowDusk.Validation.AposGallery.AposGalleryRenderer.Cells.Count} shapes visible.");
Console.WriteLine("[apos-gl-gallery] NOTE: no mgfxc golden comparison — see the mode remarks above for why GL has no trustworthy oracle for this gallery.");
return pass ? 0 : 1;
}

static bool CellHasVisibleContent(Microsoft.Xna.Framework.Color[] pixels, int width, Microsoft.Xna.Framework.Rectangle cell)
{
    int visible = 0, total = 0;
    for (int y = cell.Top; y < cell.Bottom; y++)
    for (int x = cell.Left; x < cell.Right; x++)
    {
        var p = pixels[y * width + x];
        total++;
        if (p.A > 8 && (p.R > 8 || p.G > 8 || p.B > 8))
            visible++;
    }
    // At least 1% of the cell must be non-black/non-transparent — the same non-vacuity
    // bar the other Apos.Shapes render-proofs use, applied per-cell instead of whole-image.
    return total > 0 && visible > total / 100;
}

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

// Issue #160: the NEEDLE-THIN ELLIPSE slice — SUPPLEMENTARY coverage, not the authoritative
// guard. Same same-backend GL<->GL comparison against the mgfxc golden, on the shape whose
// Newton/bisect SDF exercises the header-less-loop rewrite (the circle above converges in ~3
// iterations and never reaches the loop's finalizer else-branch).
//
// CAVEAT: the 0.14.0 bug was an UNINITIALIZED read (the rewrite dropped the else that assigns the
// solver's phi output), i.e. undefined behavior. On the reporter's Intel GL driver it surfaced as
// garbage (tips vanished, whole-image maxd ~200); on other drivers (this box included) the driver
// zero-inits the register and the stalled tip `t` is ~0, so buggy and fixed render identically and
// this gate stays green regardless. So it CANNOT be relied on to red on every machine. The
// authoritative, driver-independent regression guard is the rewriter unit test
// (MonoGameGlslRewriterTests.PixelStage_BoundedHeaderlessForLoop_ElseBranchStillReachableAtMaxTripCount_Issue160),
// which asserts the emitted loop keeps the else reachable. This render slice adds real value on
// drivers that DO expose the UB and against future ellipse regressions that are deterministic.
bool haveEllipse = caps.ContainsKey("baseline-mgfxc.ellipse") && caps.ContainsKey("candidate-sd.ellipse");
int ellipseMaxd = haveEllipse
    ? AposMaxDelta(caps["baseline-mgfxc.ellipse"], caps["candidate-sd.ellipse"])
    : int.MaxValue;
bool ellipseDrew = caps.TryGetValue("candidate-sd.ellipse", out var ellCand) && AposHasVisibleContent(ellCand);

Console.WriteLine();
Console.WriteLine($"[apos-gl] baseline-vs-candidate maxd: {(aposMaxd == int.MaxValue ? "n/a" : aposMaxd)}");
Console.WriteLine($"[apos-gl] candidate drew visible content: {aposDrew}");
Console.WriteLine($"[apos-gl] thin-ellipse baseline-vs-candidate maxd: {(ellipseMaxd == int.MaxValue ? "n/a" : ellipseMaxd)} (issue #160)");
Console.WriteLine($"[apos-gl] thin-ellipse candidate drew visible content: {ellipseDrew}");

// Tolerance 2/255, NOT the maxd-0 bar the DX/Vulkan Apos.Shapes gates hit: this fixture's
// SpritePixelShader always round-trips fill/border colors through RgbToOklab/OkLabToRgb
// (cube roots + fractional pow()), and measured drift here is maxd 2, only 216/16384 pixels
// (1.3%), concentrated at 1-2/255 — the documented GLSL-dialect precision drift between
// ShadowDusk's SPIRV-Cross output and mgfxc's MojoShader GLSL on transcendental math (see
// plan/DONE/PHASE-17-monogame-runtime-validation.md), not a structural mismatch. This is
// a real, explained drift, not a silently-widened bar.
const int AposTolerance = 2;

// The thin ellipse shares the fill/border color path (RgbToOklab/OkLabToRgb) with the circle, so
// its transcendental drift vs the golden is the same class. Measured maxd is 1 on this box; the
// tolerance leaves a little headroom for AA-edge drift on other GL drivers. On a driver that
// exposes the issue-#160 UB the divergence is whole-image maxd ~200, so a real regression there is
// far above this bar; on a driver that masks the UB the slice simply stays green (see the CAVEAT
// above) and the unit test carries the guard.
const int EllipseTolerance = 4;

bool allLoaded = game.Outcomes.All(o => o is { Loaded: true, Rendered: true });
bool pass2 = haveBoth && aposMaxd <= AposTolerance && aposDrew;
bool passEllipse = haveEllipse && ellipseMaxd <= EllipseTolerance && ellipseDrew;
Console.WriteLine($"\n[apos-gl] circle:      {(pass2 ? $"OK (maxd {aposMaxd} <= {AposTolerance})" : "DIVERGED")}");
Console.WriteLine($"[apos-gl] thin ellipse: {(passEllipse ? $"OK (maxd {ellipseMaxd} <= {EllipseTolerance})" : "DIVERGED")} (issue #160)");
Console.WriteLine($"[apos-gl] {(allLoaded && pass2 && passEllipse ? "PASS" : "FAIL")} — load+render {(allLoaded ? "OK" : "FAIL")}.");
return (allLoaded && pass2 && passEllipse) ? 0 : 1;
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
