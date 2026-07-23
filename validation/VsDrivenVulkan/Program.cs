// Issue #145 — VS-DRIVEN Vulkan render gate (rung 4, with a reference-compiler oracle).
//
// WHY THIS EXISTS. The Vulkan proof before this (validation/CandidateVulkan) ran ten
// PS-only, matrix-free shaders, so a ShadowDusk-produced VERTEX shader had never rendered
// on a real DesktopVK device and no Vulkan render had ever involved a matrix parameter.
// Both gaps are exactly what hid issue #145's bug 1: -Zpr made DXC pack matrices row-major
// while MonoGame uploads them for HLSL's column-major default, so every VS-driven Vulkan
// effect read its transform TRANSPOSED, threw its geometry out of clip space, and rendered
// nothing — loading fine and drawing without error the whole time.
//
// WHAT IT PROVES. Both arms render the same VS-driven fixture through the identical custom
// vertex-buffer path (VsEffectImageRenderer, which uploads a NON-IDENTITY ASYMMETRIC matrix
// — the issue-#70 input discipline, since an identity matrix is transpose-invariant and
// cannot detect this class of bug at all):
//
//   baseline  = the real dotnet-mgfxc 3.8.5 /Profile:Vulkan golden
//   candidate = ShadowDusk's own in-memory compile of the same .fx
//
// and diffs them pixel-for-pixel, same-backend Vulkan<->Vulkan. This is a genuine
// reference-compiler oracle on Vulkan, which Phase 32 could not achieve: mgfxc 3.8.5
// computes a texture slot as (rawBinding - 32) and AUTO-NUMBERED resources underflow to
// 224/225, making its own container unloadable. The fixture's Vulkan branch therefore uses
// matching EXPLICIT registers, which both keeps mgfxc's arithmetic in range and is the
// combined-descriptor shape MonoGame's native draw path actually handles.
//
// Non-vacuity: a fully transparent/black candidate render is REJECTED even if the baseline
// happens to agree, because "renders nothing" was the reported symptom.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

// ONE PHASE PER PROCESS. DesktopVK does not survive a second GraphicsDevice in the same
// process (creating a second Game after the first is disposed access-violates in the native
// Vulkan teardown/re-init), so each phase is its own run:
//   dotnet run --project validation/VsDrivenVulkan            -> phase 1 (the simple VS rig)
//   dotnet run --project validation/VsDrivenVulkan -- apos     -> phase 2 (the #145 reproducer)
string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "vs";
if (mode is not ("vs" or "apos"))
{
    Console.Error.WriteLine($"unknown mode '{mode}' — expected 'vs' or 'apos'");
    return 2;
}

const string Fixture = "VsTransformColorTexture";

string repoRoot   = ShaderInputs.FindRepoRoot();
string fxPath     = Path.Combine(repoRoot, "tests", "fixtures", "shaders", Fixture + ".fx");
string goldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", "Vulkan", Fixture + ".mgfx");
string catPath    = ShaderInputs.CatPath(repoRoot);
string outDir     = Path.Combine(repoRoot, "validation", "output", "vsdriven-vulkan");

Console.WriteLine($"[vs-vulkan] fixture: {fxPath}");
Console.WriteLine($"[vs-vulkan] golden:  {goldenPath}");
Console.WriteLine($"[vs-vulkan] out:     {outDir}\n");

if (mode == "vs")
{
    return await RunVsPhase();
}

return await RunAposPhase();

// ---------------------------------------------------------------------------------------
// Phase 1 — the simple VS rig (POSITION/COLOR/TEXCOORD + a float4x4), vs the mgfxc golden.
// ---------------------------------------------------------------------------------------
async Task<int> RunVsPhase()
{
// ---- Candidate: ShadowDusk's in-memory Vulkan compile. ----
byte[]? candidateBytes = null;
string? candidateErr   = null;
{
    var result = await new EffectCompiler().CompileAsync(
        await File.ReadAllTextAsync(fxPath),
        new CompilerOptions
        {
            Target          = PlatformTarget.Vulkan,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = fxPath,
        });

    if (result.IsFailure)
        candidateErr = string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));
    else
    {
        candidateBytes = result.Value.Data;
        Directory.CreateDirectory(outDir);
        await File.WriteAllBytesAsync(Path.Combine(outDir, Fixture + ".candidate.mgfx"), candidateBytes);
    }
}

// ---- Baseline: the checked-in mgfxc 3.8.5 Vulkan golden. ----
byte[]? baselineBytes = File.Exists(goldenPath) ? await File.ReadAllBytesAsync(goldenPath) : null;
string? baselineErr   = baselineBytes is null ? $"golden not found: {goldenPath}" : null;

Console.WriteLine($"[vs-vulkan] candidate: {(candidateBytes is null ? "COMPILE FAIL: " + candidateErr : candidateBytes.Length + " bytes")}");
Console.WriteLine($"[vs-vulkan] baseline:  {(baselineBytes is null ? baselineErr : baselineBytes.Length + " bytes")}\n");

// Both jobs go through ONE renderer instance: a single device, a single draw path, so any
// pixel difference is attributable solely to the compiler that produced the bytes.
var jobs = new List<ShaderJob>
{
    new("baseline-mgfxc", baselineBytes,  baselineErr),
    new("candidate-sd",   candidateBytes, candidateErr),
};

using var game = new VsEffectImageRenderer(catPath, outDir, jobs);
game.Run();

Console.WriteLine("[vs-vulkan] load + render results:");
foreach (var o in game.Outcomes)
    Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-16} {o.Error ?? o.PngPath}");

var captures = game.Captures.ToDictionary(c => c.Name, c => c);

bool haveBoth = captures.ContainsKey("baseline-mgfxc") && captures.ContainsKey("candidate-sd");
int maxd = haveBoth
    ? MaxDelta(captures["baseline-mgfxc"], captures["candidate-sd"])
    : int.MaxValue;

// Non-vacuity: the candidate must actually DRAW something. "Renders nothing" (a fully
// transparent or fully black frame) is precisely the issue-#145 symptom, so a blank frame
// fails even if both arms are blank in the same way.
bool candidateDrew = captures.TryGetValue("candidate-sd", out var cand) && HasVisibleContent(cand);

Console.WriteLine();
Console.WriteLine($"[vs-vulkan] baseline-vs-candidate maxd: {(maxd == int.MaxValue ? "n/a" : maxd)}");
Console.WriteLine($"[vs-vulkan] candidate drew visible content: {candidateDrew}");

bool phase1 = haveBoth && maxd <= 1 && candidateDrew;
Console.WriteLine($"[vs-vulkan] phase 1 (VsTransformColorTexture): {(phase1 ? "PASS" : "FAIL")}");
return phase1 ? 0 : 1;
}

// ---------------------------------------------------------------------------------------
// Phase 2 — the ISSUE #145 REPRODUCER ITSELF: upstream Apos.Shapes at its current revision,
// the shader the reporter compiled. Same two arms, same device, its own 13-element vertex
// layout (see AposShapesRenderer).
// ---------------------------------------------------------------------------------------
async Task<int> RunAposPhase()
{
const string AposFixture = "apos-shapes-sm6";

string aposFx     = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "third-party", "Apos.Shapes", AposFixture + ".fx");
string aposGolden = Path.Combine(repoRoot, "tests", "fixtures", "golden", "Vulkan", AposFixture + ".mgfx");
string aposOut    = Path.Combine(repoRoot, "validation", "output", "vsdriven-vulkan-apos");

Console.WriteLine();
Console.WriteLine($"[apos-vulkan] fixture: {aposFx}");
Console.WriteLine($"[apos-vulkan] golden:  {aposGolden}");

byte[]? aposCandidate = null;
string? aposCandErr   = null;
{
    var result = await new EffectCompiler().CompileAsync(
        await File.ReadAllTextAsync(aposFx),
        new CompilerOptions
        {
            Target          = PlatformTarget.Vulkan,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = aposFx,
        });

    if (result.IsFailure)
        aposCandErr = string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));
    else
    {
        aposCandidate = result.Value.Data;
        Directory.CreateDirectory(aposOut);
        await File.WriteAllBytesAsync(Path.Combine(aposOut, AposFixture + ".candidate.mgfx"), aposCandidate);
    }
}

byte[]? aposBaseline = File.Exists(aposGolden) ? await File.ReadAllBytesAsync(aposGolden) : null;
string? aposBaseErr  = aposBaseline is null ? $"golden not found: {aposGolden}" : null;

Console.WriteLine($"[apos-vulkan] candidate: {(aposCandidate is null ? "COMPILE FAIL: " + aposCandErr : aposCandidate.Length + " bytes")}");
Console.WriteLine($"[apos-vulkan] baseline:  {(aposBaseline is null ? aposBaseErr : aposBaseline.Length + " bytes")}");

var aposJobs = new List<(string Name, byte[]? Bytes, string? Error)>
{
    ("baseline-mgfxc", aposBaseline,  aposBaseErr),
    ("candidate-sd",   aposCandidate, aposCandErr),
};

using var aposGame = new ShadowDusk.Validation.VsDrivenVulkan.AposShapesRenderer(aposOut, aposJobs);
aposGame.Run();

Console.WriteLine("[apos-vulkan] load + render results:");
foreach (var o in aposGame.Outcomes)
    Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-16} {o.Error ?? "rendered"}");

var aposCaps = aposGame.Captures.ToDictionary(c => c.Name, c => c);
bool aposBoth = aposCaps.ContainsKey("baseline-mgfxc") && aposCaps.ContainsKey("candidate-sd");
int aposMaxd  = aposBoth ? MaxDelta(aposCaps["baseline-mgfxc"], aposCaps["candidate-sd"]) : int.MaxValue;
bool aposDrew = aposCaps.TryGetValue("candidate-sd", out var aposCand) && HasVisibleContent(aposCand);

Console.WriteLine($"[apos-vulkan] baseline-vs-candidate maxd: {(aposMaxd == int.MaxValue ? "n/a" : aposMaxd)}");
Console.WriteLine($"[apos-vulkan] candidate drew visible content: {aposDrew}");

bool phase2 = aposBoth && aposMaxd <= 1 && aposDrew;
Console.WriteLine($"[apos-vulkan] phase 2 (issue #145 reproducer): {(phase2 ? "PASS" : "FAIL")}");

return phase2 ? 0 : 1;
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

// "Visible" = at least 1% of pixels are non-transparent AND not pure black. The fixture
// draws a half-size, offset copy of the cat, so a correct render covers roughly a quarter
// of the target; 1% is far below that and far above sampling noise.
static bool HasVisibleContent((string Name, Color[] Pixels, int Width, int Height) c)
{
    int visible = c.Pixels.Count(p => p.A > 8 && (p.R > 8 || p.G > 8 || p.B > 8));
    return visible > c.Pixels.Length / 100;
}
