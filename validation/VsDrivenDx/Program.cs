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
// mode "texarr" (issue #339): a TEXTURE ARRAY (`Texture2D Tex[2]`, tests/fixtures/shaders/
// texture-arrays/TextureArray2.fx) on real MonoGame WindowsDX, ShadowDusk's DirectX_11 table and
// render against the real mgfxc 3.8.4.1 /Profile:DirectX_11 golden. See RunTextureArrayPhase.
//
// mode "samparr" (issue #340): a SAMPLER ARRAY (`SamplerState Samplers[2]`, texture-arrays/
// SamplerArray2.fx) must be REFUSED with SD0223 on DirectX 11 and DirectX 12, as mgfxc refuses it
// in its own parser. An optional .mgfx path loads a prebuilt effect (a pre-fix build's output) into
// the real engine as evidence. See RunSamplerArrayPhase.
//
// dotnet run --project validation/VsDrivenDx                     -> the VS rig
// dotnet run --project validation/VsDrivenDx -- apos              -> the Apos.Shapes DX render-proof
// dotnet run --project validation/VsDrivenDx -- texarr [cand.mgfx] -> the texture-array row
// dotnet run --project validation/VsDrivenDx -- samparr [evid.mgfx] -> the sampler-array refusal

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation.Dx;

string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "vs";
if (mode is not ("vs" or "apos" or "texarr" or "samparr"))
{
    Console.Error.WriteLine($"unknown mode '{mode}' — expected 'vs', 'apos', 'texarr' or 'samparr'");
    return 2;
}

string repoRoot = FindRepoRoot();
string? extraPath = args.Length > 1 ? args[1] : null;

if (mode == "vs")
    return await RunVsPhase();
if (mode == "texarr")
    return await RunTextureArrayPhase(extraPath);
if (mode == "samparr")
    return await RunSamplerArrayPhase(extraPath);

return await RunAposPhase();

// ---------------------------------------------------------------------------------------
// mode "texarr" - issue #339: an ARRAY of textures (`Texture2D Tex[2] : register(t0)`, one
// SamplerState) on real MonoGame WindowsDX (DX11), ShadowDusk vs the real mgfxc 3.8.4.1
// /Profile:DirectX_11 golden, tests/fixtures/golden/DirectX_11/TextureArray2.mgfx.
//
// What mgfxc puts in the table (3.8.4.1 and 3.8.5, measured 2026-10-02, the same for 1, 2
// and 4 elements with or without a register): ONE Object parameter `Tex`, ONE sampler record
// t0/s0 pointing at it. Root cause of the old divergence: mgfxc compiles at the author's
// ps_4_0, where fxc reflects the array as one binding `Tex` with BindCount N; ShadowDusk
// compiles DirectX 11 at ps_5_0, where fxc (and vkd3d) store one record per element,
// `Tex[0]` t0 and `Tex[1]` t1, which became two parameters no author names. The DXBC
// extractor now folds them back (ResourceArrayBindings). The arms:
//   baseline-mgfxc   = the committed golden
//   candidate-vkd3d  = ShadowDusk's in-process DirectX compile, vkd3d-shader (the shipping backend)
//   candidate-oracle = the same through d3dcompiler_47 (the Windows oracle backend)
//   candidate-file   = instead of the two above, a prebuilt .mgfx given on the command line
//                      (evidence / bisecting: a pre-fix build's output turns this row RED)
// Verdict: (1) each candidate's parameter table and sampler records equal the golden's record
// for record (name, class, type, shape; shader, type, slots, parameter, record name, baked
// state; DX11 record names are empty on both sides); (2) every arm loads and draws with
// `Parameters["Tex"]` = cat and `GraphicsDevice.Textures[1]` = flat green (a MISSING `Tex`
// parameter is the consumer-visible symptom of the old table); (3) each candidate matches the
// baseline within 1/255. Reported, not asserted: (4) the candidate against the CPU expectation
// (cat + green) / 2, i.e. whether element [1] bound through GraphicsDevice.Textures[1] is read
// on WindowsDX (on WindowsDX12 it is not, see VsDrivenDx12 -- texarr).
// ---------------------------------------------------------------------------------------
async Task<int> RunTextureArrayPhase(string? candidateFile)
{
const string TexArrFixture = "TextureArray2";
string fxPath     = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "texture-arrays", TexArrFixture + ".fx");
string goldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_11", TexArrFixture + ".mgfx");
string catPath    = Path.Combine(repoRoot, "samples", "ShaderViewer", "Content", "cat.jpg");
string outDir     = Path.Combine(repoRoot, "validation", "output", "texarr-dx11");

Console.WriteLine($"[texarr-dx11] fixture: {fxPath}");
Console.WriteLine($"[texarr-dx11] golden:  {goldenPath}");

string src = await File.ReadAllTextAsync(fxPath);
async Task<(byte[]? Bytes, string? Err)> Compile(DxbcBackend backend)
{
    var r = await new EffectCompiler().CompileAsync(src, new CompilerOptions
    {
        Target = PlatformTarget.DirectX,
        DxbcBackend = backend,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fxPath,
    });
    return r.IsFailure ? (null, string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}"))) : (r.Value.Data, null);
}

var candidates = new List<(string Label, byte[]? Bytes, string? Err)>();
if (candidateFile is not null)
{
    Console.WriteLine($"[texarr-dx11] candidate-file: {candidateFile} (prebuilt effect, NOT compiled here)");
    candidates.Add(("candidate-file", File.Exists(candidateFile) ? await File.ReadAllBytesAsync(candidateFile) : null,
                    File.Exists(candidateFile) ? null : $"not found: {candidateFile}"));
}
else
{
    var (vk, vkErr) = await Compile(DxbcBackend.Vkd3d);
    candidates.Add(("candidate-vkd3d", vk, vkErr));
    var (or, orErr) = await Compile(DxbcBackend.D3DCompiler);
    candidates.Add(("candidate-oracle", or, orErr));
}

byte[]? baselineBytes = File.Exists(goldenPath) ? await File.ReadAllBytesAsync(goldenPath) : null;
string? baselineErr = baselineBytes is null ? $"golden not found: {goldenPath}" : null;
Console.WriteLine($"[texarr-dx11] baseline-mgfxc: {(baselineBytes is null ? baselineErr : baselineBytes.Length + " bytes")}");
foreach (var c in candidates)
    Console.WriteLine($"[texarr-dx11] {c.Label}: {(c.Bytes is null ? "COMPILE FAIL: " + c.Err : c.Bytes.Length + " bytes")}");
Console.WriteLine();

Directory.CreateDirectory(outDir);
foreach (var c in candidates)
    if (c.Bytes is not null)
        await File.WriteAllBytesAsync(Path.Combine(outDir, $"{TexArrFixture}.{c.Label}.mgfx"), c.Bytes);

// (1) The table, record for record, against the reference compiler's.
bool tablesOk = baselineBytes is not null && candidates.All(c => c.Bytes is not null);
if (baselineBytes is not null)
{
    var golden = ShadowDusk.Integration.Tests.MgfxBlobReader.Parse(baselineBytes);
    Console.WriteLine($"[texarr-dx11] golden           params: {Params(golden)} | records: {Records(golden)}");
    foreach (var c in candidates)
    {
        if (c.Bytes is null) continue;
        var subject = ShadowDusk.Integration.Tests.MgfxBlobReader.Parse(c.Bytes);
        bool ok = Params(golden) == Params(subject) && Records(golden) == Records(subject);
        tablesOk &= ok;
        Console.WriteLine($"[texarr-dx11] {c.Label,-16} params: {Params(subject)} | records: {Records(subject)} -> table equals mgfxc's: {(ok ? "YES" : "NO")}");
    }
}
Console.WriteLine();

// (2) Every arm in the real engine, identical draw path.
Texture2D? green = null;
Color[]? catPixels = null;
void SetParams(Effect effect, Texture2D cat)
{
    if (catPixels is null)
    {
        catPixels = new Color[cat.Width * cat.Height];
        cat.GetData(catPixels);
    }
    var tex = effect.Parameters["Tex"];
    Console.WriteLine($"  [texarr-dx11] Parameters[\"Tex\"] {(tex is null ? "MISSING" : $"present, Elements.Count={tex.Elements.Count}")}; " +
                      $"all parameters: {string.Join(", ", Enumerable.Range(0, effect.Parameters.Count).Select(i => effect.Parameters[i].Name))}");
    tex?.SetValue(cat);
    green ??= Flat(effect.GraphicsDevice, new Color(0, 255, 0, 255));
    // Element [1] is reachable only through the device's texture slots (mgfxc's table has no
    // parameter for it); the shader averages [0] and [1].
    effect.GraphicsDevice.Textures[1] = green;
}

var jobs = new List<ShaderJob> { new("baseline-mgfxc", baselineBytes, baselineErr) };
foreach (var c in candidates)
    jobs.Add(new ShaderJob(c.Label, c.Bytes, c.Err));
using var game = new DxEffectImageRenderer(catPath, outDir, jobs, SetParams);
game.Run();

Console.WriteLine("[texarr-dx11] load + render results:");
foreach (var o in game.Outcomes)
    Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-16} {o.Error ?? o.PngPath}");

var caps = game.Captures.ToDictionary(c => c.Name, c => c);
bool allRendered = caps.ContainsKey("baseline-mgfxc") && candidates.All(c => caps.ContainsKey(c.Label));

// (3) Each candidate vs the baseline; (4) each candidate vs the CPU expectation (informational).
bool pixelsOk = allRendered;
Console.WriteLine();
foreach (var c in candidates)
{
    if (!caps.TryGetValue(c.Label, out var cand) || !caps.TryGetValue("baseline-mgfxc", out var baseCap))
    {
        Console.WriteLine($"  [compare] {c.Label} vs baseline-mgfxc: FAIL (missing render)");
        pixelsOk = false;
        continue;
    }
    int maxd = MaxDelta(baseCap, cand);
    pixelsOk &= maxd <= 1;
    int cpuMaxd = int.MaxValue;
    if (catPixels is { } cat && cat.Length == cand.Pixels.Length)
    {
        cpuMaxd = 0;
        for (int i = 0; i < cand.Pixels.Length; i++)
        {
            var e = new Color((cat[i].R + 0) / 2, (cat[i].G + 255) / 2, (cat[i].B + 0) / 2, 255);
            cpuMaxd = Math.Max(cpuMaxd, Math.Max(Math.Max(Math.Abs(e.R - cand.Pixels[i].R), Math.Abs(e.G - cand.Pixels[i].G)),
                                                 Math.Max(Math.Abs(e.B - cand.Pixels[i].B), Math.Abs(e.A - cand.Pixels[i].A))));
        }
    }
    Console.WriteLine($"  [compare] {c.Label} vs baseline-mgfxc: maxd={maxd} -> {(maxd <= 1 ? "PASS" : "FAIL")} (tol 1); " +
                      $"vs CPU (cat + green) / 2: maxd={(cpuMaxd == int.MaxValue ? "n/a" : cpuMaxd)} " +
                      $"(informational: element [1] through GraphicsDevice.Textures[1] is {(cpuMaxd <= 2 ? "READ" : "NOT read")} on WindowsDX)");
}

bool pass = tablesOk && allRendered && pixelsOk;
Console.WriteLine($"\n[texarr-dx11] {(pass ? "PASS" : "FAIL")}: tables {(tablesOk ? "equal" : "DIFFER")}, load+render {(allRendered ? $"{jobs.Count}/{jobs.Count}" : $"<{jobs.Count}")}, " +
                  $"pixel-match vs golden {(pixelsOk ? "OK" : "DIVERGED")}.");
return pass ? 0 : 1;
}

// ---------------------------------------------------------------------------------------
// mode "samparr" - issue #340: an ARRAY of samplers (`SamplerState Samplers[2]`, one per
// texture; tests/fixtures/shaders/texture-arrays/SamplerArray2.fx). Real mgfxc 3.8.4.1 and
// 3.8.5 refuse the shape on EVERY profile in their own parser before any shader compiles
// ("SamplerArray2.fx(31,22) : Unexpected token '[' found. Expected Semicolon, Comma, or
// CloseParenthesis."), so there is no reference effect to render against and the gate is the
// refusal itself: ShadowDusk's DirectX and DirectX12 compiles must FAIL with SD0223 naming
// `Samplers` at the declaration (line 31, column 14). Red before the fix (both compiled).
//
// Optional evidence arm: a prebuilt .mgfx on the command line (a pre-fix build's DirectX_11
// output) is loaded into the real WindowsDX engine and drawn with TexA = cat, TexB = flat
// green, so what the refused shape used to do in the engine is on record. Informational,
// never part of the verdict.
// ---------------------------------------------------------------------------------------
async Task<int> RunSamplerArrayPhase(string? evidenceFile)
{
const string SampArrFixture = "SamplerArray2";
string fxPath  = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "texture-arrays", SampArrFixture + ".fx");
string catPath = Path.Combine(repoRoot, "samples", "ShaderViewer", "Content", "cat.jpg");
string outDir  = Path.Combine(repoRoot, "validation", "output", "samparr-dx11");
Console.WriteLine($"[samparr-dx11] fixture: {fxPath}");

string src = await File.ReadAllTextAsync(fxPath);
bool pass = true;
foreach (PlatformTarget target in new[] { PlatformTarget.DirectX, PlatformTarget.DirectX12 })
{
    var r = await new EffectCompiler().CompileAsync(src, new CompilerOptions
    {
        Target = target,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fxPath,
    });
    if (r.IsSuccess)
    {
        Console.WriteLine($"[samparr-dx11] {target}: COMPILED ({r.Value.Data.Length} bytes) -> FAIL: mgfxc refuses this shape, so must ShadowDusk");
        pass = false;
        continue;
    }
    ShaderError? e = r.Error.FirstOrDefault(x => x.Code == "SD0223");
    bool ok = e is not null && Path.GetFileName(e.File) == SampArrFixture + ".fx" && e.Line == 31 && e.Column == 14 && e.Message.Contains("'Samplers'", StringComparison.Ordinal);
    pass &= ok;
    Console.WriteLine($"[samparr-dx11] {target}: refused with {string.Join(", ", r.Error.Select(x => $"{x.Code} {Path.GetFileName(x.File)}({x.Line},{x.Column})"))} -> {(ok ? "PASS" : "FAIL")} (expected SD0223 at SamplerArray2.fx(31,14) naming 'Samplers')");
    if (e is not null)
        Console.WriteLine($"  {e.Message}");
}

if (evidenceFile is not null)
{
    Console.WriteLine($"\n[samparr-dx11] evidence: loading prebuilt {evidenceFile} into real WindowsDX (informational)");
    byte[]? bytes = File.Exists(evidenceFile) ? await File.ReadAllBytesAsync(evidenceFile) : null;
    if (bytes is not null)
    {
        var reader = ShadowDusk.Integration.Tests.MgfxBlobReader.Parse(bytes);
        Console.WriteLine($"  params: {Params(reader)} | records: {Records(reader)}");
    }
    Texture2D? green = null;
    Color[]? catPixels = null;
    void SetParams(Effect effect, Texture2D cat)
    {
        if (catPixels is null) { catPixels = new Color[cat.Width * cat.Height]; cat.GetData(catPixels); }
        Console.WriteLine($"  parameters: {string.Join(", ", Enumerable.Range(0, effect.Parameters.Count).Select(i => effect.Parameters[i].Name))}");
        effect.Parameters["TexA"]?.SetValue(cat);
        green ??= Flat(effect.GraphicsDevice, new Color(0, 255, 0, 255));
        effect.Parameters["TexB"]?.SetValue(green);
    }
    using var game = new DxEffectImageRenderer(catPath, outDir, new List<ShaderJob> { new("evidence-prebuilt", bytes, bytes is null ? $"not found: {evidenceFile}" : null) }, SetParams);
    game.Run();
    foreach (var o in game.Outcomes)
        Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-16} {o.Error ?? o.PngPath}");
    var cap = game.Captures.FirstOrDefault();
    if (cap.Pixels is not null && catPixels is { } cat && cat.Length == cap.Pixels.Length)
    {
        int cpuMaxd = 0;
        for (int i = 0; i < cap.Pixels.Length; i++)
        {
            var e = new Color((cat[i].R + 0) / 2, (cat[i].G + 255) / 2, (cat[i].B + 0) / 2, 255);
            cpuMaxd = Math.Max(cpuMaxd, Math.Max(Math.Max(Math.Abs(e.R - cap.Pixels[i].R), Math.Abs(e.G - cap.Pixels[i].G)),
                                                 Math.Max(Math.Abs(e.B - cap.Pixels[i].B), Math.Abs(e.A - cap.Pixels[i].A))));
        }
        Console.WriteLine($"  evidence vs CPU (cat + green) / 2: maxd={cpuMaxd} (both textures {(cpuMaxd <= 2 ? "sampled" : "NOT both sampled")} through the two records)");
    }
}

Console.WriteLine($"\n[samparr-dx11] {(pass ? "PASS" : "FAIL")}: SD0223 refusal on DirectX 11 and DirectX 12 {(pass ? "as mgfxc's own parser refuses the shape" : "MISSING")}.");
return pass ? 0 : 1;
}

static Texture2D Flat(Microsoft.Xna.Framework.Graphics.GraphicsDevice device, Color color)
{
    var t = new Texture2D(device, 1, 1);
    t.SetData(new[] { color });
    return t;
}

// The parameter table and the sampler records of a decoded .mgfx, one line each, so two effects
// compare record for record (texarr, samparr).
static string Params(ShadowDusk.Integration.Tests.MgfxBlobReader e) => string.Join("; ",
    e.Parameters.Select(p => $"{p.Name} class={p.Class} type={p.Type} {p.Rows}x{p.Columns} elems={p.ElementCount} members={p.MemberCount}"));
static string Records(ShadowDusk.Integration.Tests.MgfxBlobReader e) => string.Join("; ",
    e.Samplers.Select(s => $"sh{s.ShaderIndex} type={s.Type} t{s.TextureSlot} s{s.SamplerSlot} name='{s.Name}' ->param {s.Parameter} state={(s.State is null ? "none" : "baked")}"));

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
// mgfxc golden (same evidence bar Phase 51 A3 used).
//
// ROOT-CAUSED 2026-09-09: the residual 1/255 is a sub-ULP fast-math scheduling artifact,
// not a ShadowDusk defect — the DX12 gate's own precedent (see VsDrivenDx12/Program.cs,
// root-caused 2026-07-31), just via d3dcompiler_47 flags here instead of a DXC version pin.
// Decompiling mgfxc.dll (MonoGame.Framework.Content.Pipeline's ShaderCompiler.CompileHLSL)
// showed its real DirectX_11 fxc invocation sets EnableBackwardsCompatibility and
// OptimizationLevel3, which D3DCompilerShaderCompiler was NOT setting — both sides load the
// SAME system d3dcompiler_47.dll (confirmed: neither Vortice.D3DCompiler nor mgfxc's
// SharpDX.D3DCompiler ships a private copy), so this was a genuine flag mismatch, now fixed.
// Fixing the flags changed the oracle's DXBC (confirmed via a byte-diff of the compiled
// blob) but did not close the gap: the same 18 cells still land at maxd 1, one sub-ULP fast-
// math rewrite choice apart from mgfxc's own optimizer pass on an identical-flags, identical-
// DLL compile — the same class of dithering-boundary sensitivity the DX12 investigation
// documented, just not eliminable here since fxc's optimizer (unlike DXC) exposes no version
// pin to align on. The shipping path (candidate-vkd3d, below) is unaffected and stays at
// maxd 0. See `docs/validation-matrix.md` §7.
int cmpOracle = CompareTo("baseline-real-mgfxc", "candidate-oracle", tolerance: 1);
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
