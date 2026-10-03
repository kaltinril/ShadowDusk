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
//   dotnet run --project validation/VsDrivenVulkan -- wave     -> issue #229: EXPECT-DIAGNOSTIC.
//                                                                 VsWaveQuadIntrinsics.fx must be
//                                                                 rejected with SD0218; no device.
//   dotnet run --project validation/VsDrivenVulkan -- texarr   -> issue #324: EXPECT-DIAGNOSTIC.
//                                                                 texture-arrays/TextureArray2.fx
//                                                                 must be rejected with SD0221,
//                                                                 and the committed mgfxc 3.8.5
//                                                                 golden must have no table at
//                                                                 all; no device.
//   dotnet run --project validation/VsDrivenVulkan -- texarr-reference
//                                                              -> issue #324, evidence arm: load
//                                                                 mgfxc's own TextureArray2 golden
//                                                                 into real DesktopVK and draw it,
//                                                                 reporting what the engine does
//                                                                 with an effect that has no
//                                                                 texture parameter and no
//                                                                 descriptor binding (informational,
//                                                                 not a gate: it is the reference
//                                                                 compiler's output under test).
string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "vs";
if (mode is not ("vs" or "apos" or "wave" or "texarr" or "texarr-reference"))
{
    Console.Error.WriteLine($"unknown mode '{mode}' — expected 'vs', 'apos', 'wave', 'texarr' or 'texarr-reference'");
    return 2;
}

const string Fixture = "VsTransformColorTexture";

// The wave fixture (phase 1's VS and registers, plus a self-checking wave/quad PS). Measured
// 2026-10-01 under the Khronos validation layer: it rendered correctly on lavapipe but its
// SPIR-V 1.3 / subgroup module is out of spec on MonoGame's Vulkan 1.0 instance, so ShadowDusk
// now rejects wave/quad intrinsics on Vulkan with SD0218. This mode pins that rejection.
string candidateFixture = mode == "wave" ? "VsWaveQuadIntrinsics" : Fixture;

string repoRoot   = ShaderInputs.FindRepoRoot();
string fxPath     = Path.Combine(repoRoot, "tests", "fixtures", "shaders", candidateFixture + ".fx");
string goldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", "Vulkan", Fixture + ".mgfx");
string catPath    = ShaderInputs.CatPath(repoRoot);
string outDir     = Path.Combine(repoRoot, "validation", "output", mode == "wave" ? "vsdriven-vulkan-wave" : "vsdriven-vulkan");

Console.WriteLine($"[vs-vulkan] fixture: {fxPath}");
Console.WriteLine($"[vs-vulkan] golden:  {goldenPath}");
Console.WriteLine($"[vs-vulkan] out:     {outDir}\n");

if (mode is "texarr" or "texarr-reference")
    return await RunTextureArrayPhase(mode == "texarr-reference");

if (mode == "wave")
{
    var waveResult = await new EffectCompiler().CompileAsync(
        await File.ReadAllTextAsync(fxPath),
        new CompilerOptions
        {
            Target          = PlatformTarget.Vulkan,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = fxPath,
        });

    if (waveResult.IsSuccess)
    {
        Console.WriteLine("[vs-vulkan] wave: FAIL, the wave/quad fixture COMPILED for Vulkan; it must be rejected with SD0218.");
        return 1;
    }

    var sd0218 = waveResult.Error.FirstOrDefault(e => e.Code == "SD0218");
    foreach (var e in waveResult.Error)
        Console.WriteLine($"[vs-vulkan] wave: {e.Code} {Path.GetFileName(e.File)}({e.Line},{e.Column}): {e.Message}");
    bool ok = sd0218 is not null && sd0218.Message.Contains("'QuadReadAcrossX'", StringComparison.Ordinal);
    Console.WriteLine(ok
        ? "[vs-vulkan] wave: PASS, rejected loudly with SD0218 naming the intrinsic (expected)."
        : "[vs-vulkan] wave: FAIL, rejected but not with an SD0218 naming QuadReadAcrossX.");
    return ok ? 0 : 1;
}

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
// Positive controls (CI only; see validation-render.yml's vulkan job). Each one plants a
// known defect in the CANDIDATE and the lane must turn red on it, which is the evidence the
// lane can fail at all:
//   wrong-shader      the candidate PS swaps its colour channels: a pixel divergence the
//                     mgfxc diff must catch.
//   spirv-1.3-header  every SPIR-V module's version word is set to 1.3 (the shape a
//                     vulkan1.1 target env emits) while MonoGame creates a Vulkan 1.0
//                     instance: an out-of-spec module the Khronos validation layer must flag.
string control = Environment.GetEnvironmentVariable("SHADOWDUSK_VK_CONTROL")?.Trim() ?? "";
if (control is not ("" or "wrong-shader" or "spirv-1.3-header"))
{
    Console.Error.WriteLine($"unknown SHADOWDUSK_VK_CONTROL '{control}'");
    return 2;
}
if (control != "")
    Console.WriteLine($"[vs-vulkan] POSITIVE CONTROL ACTIVE: {control} (this run is expected to FAIL)");

string candidateSource = await File.ReadAllTextAsync(fxPath);
if (control == "wrong-shader")
{
    const string Original = "return SpriteTexture.Sample(SpriteTextureSampler, input.TexCoord) * input.Color;";
    if (!candidateSource.Contains(Original, StringComparison.Ordinal))
        throw new InvalidOperationException("wrong-shader control: fixture PS line not found; update the control.");
    candidateSource = candidateSource.Replace(Original,
        "return (SpriteTexture.Sample(SpriteTextureSampler, input.TexCoord) * input.Color).bgra;", StringComparison.Ordinal);
}

// ---- Candidate: ShadowDusk's in-memory Vulkan compile. ----
byte[]? candidateBytes = null;
string? candidateErr   = null;
{
    var result = await new EffectCompiler().CompileAsync(
        candidateSource,
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
        if (control == "spirv-1.3-header")
        {
            int patched = SetSpirvVersion(candidateBytes, 0x00010300);
            Console.WriteLine($"[vs-vulkan] spirv-1.3-header control: patched {patched} SPIR-V module header(s) to 1.3");
            if (patched == 0)
                throw new InvalidOperationException("spirv-1.3-header control: no SPIR-V module found in the container.");
        }
        Console.WriteLine($"[vs-vulkan] candidate SPIR-V module versions: {string.Join(", ", SpirvVersions(candidateBytes))}");
        Directory.CreateDirectory(outDir);
        await File.WriteAllBytesAsync(Path.Combine(outDir, candidateFixture + ".candidate.mgfx"), candidateBytes);
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
Console.WriteLine($"[vs-vulkan] phase 1 ({candidateFixture} vs {Fixture} golden): {(phase1 ? "PASS" : "FAIL")}");
// The wrong-shader control must be caught by the PIXEL DIFF specifically (both arms loaded
// and drew), not by some unrelated crash; the CI step matches this marker.
if (control == "wrong-shader" && haveBoth && maxd > 1)
    Console.WriteLine($"[vs-vulkan] CONTROL-DETECTED: pixel divergence maxd={maxd}");
return phase1 ? 0 : 1;
}

// ---------------------------------------------------------------------------------------
// Issue #324 — an ARRAY of textures (`Texture2D Tex[2]`) on Vulkan.
//
// mode "texarr" (the gate, no device): real mgfxc 3.8.5 /Profile:Vulkan reflects the shape as
// NO parameter, NO sampler record and NO descriptor binding (the committed golden,
// tests/fixtures/golden/Vulkan/TextureArray2.mgfx, is decoded here to pin that), so the texture
// can never be set and the effect cannot draw. ShadowDusk used to compile it to a different
// broken table (the sampler only); it must now refuse it with SD0221 naming `Tex`, located at
// the declaration. Red before the fix (the compile succeeded), green after.
//
// mode "texarr-reference" (evidence, informational): the reference compiler's own golden is
// loaded into a real DesktopVK Effect and drawn through SpriteBatch, and whatever happens
// (loads? draws? throws? draws black?) is reported, so the claim "mgfxc's output is unusable"
// rests on a measurement, not on reading its table. Never part of the gate: a native fault
// inside MonoGame's Vulkan path would take the process with it.
// ---------------------------------------------------------------------------------------
async Task<int> RunTextureArrayPhase(bool reference)
{
const string TexArrFixture = "TextureArray2";
string texArrFx     = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "texture-arrays", TexArrFixture + ".fx");
string texArrGolden = Path.Combine(repoRoot, "tests", "fixtures", "golden", "Vulkan", TexArrFixture + ".mgfx");
string texArrOut    = Path.Combine(repoRoot, "validation", "output", reference ? "texarr-vulkan-reference" : "texarr-vulkan");
Console.WriteLine($"[texarr-vulkan] fixture: {texArrFx}");
Console.WriteLine($"[texarr-vulkan] golden:  {texArrGolden}");

if (!File.Exists(texArrGolden))
{
    Console.WriteLine("[texarr-vulkan] FAIL, the mgfxc golden is missing.");
    return 1;
}
byte[] goldenBytes = await File.ReadAllBytesAsync(texArrGolden);
var golden = ShadowDusk.Integration.Tests.MgfxBlobReader.Parse(goldenBytes);
var layout = ShadowDusk.Integration.Tests.VulkanShaderCodeReader.Parse(golden.Shaders.Single().Bytecode);
Console.WriteLine($"[texarr-vulkan] mgfxc golden: profile={golden.ProfileId} parameters={golden.Parameters.Count} " +
                  $"samplerRecords={golden.Samplers.Count} descriptorBindings={layout.Bindings.Count} " +
                  $"textureSlots=0x{layout.TextureSlots:x} samplerSlots=0x{layout.SamplerSlots:x} spirvOk={layout.SpirvMagicOk}");

if (reference)
{
    // The evidence arm: what the real engine does with mgfxc's effect.
    var refJobs = new List<ShaderJob> { new("mgfxc-golden", goldenBytes, null) };
    using var refGame = new EffectImageRenderer(catPath, texArrOut, refJobs, (effect, cat) =>
    {
        Console.WriteLine($"  [texarr-vulkan] Parameters[\"Tex\"] {(effect.Parameters["Tex"] is null ? "MISSING (nothing a game could set)" : "present")}; " +
                          $"parameter count {effect.Parameters.Count}");
        effect.Parameters["Tex"]?.SetValue(cat);
    });
    refGame.Run();
    foreach (var o in refGame.Outcomes)
        Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-16} {o.Error ?? o.PngPath}");
    if (refGame.Captures.FirstOrDefault(c => c.Name == "mgfxc-golden") is { } cap)
    {
        bool drew = HasVisibleContent(cap);
        int opaque = cap.Pixels.Count(p => p.A > 8);
        Console.WriteLine($"  [texarr-vulkan] mgfxc golden drew visible content: {drew} (opaque pixels {opaque}/{cap.Pixels.Length})");
    }
    Console.WriteLine("[texarr-vulkan] reference arm reported (informational, no verdict).");
    return 0;
}

bool goldenEmpty = golden.Parameters.Count == 0 && golden.Samplers.Count == 0 && layout.Bindings.Count == 0
                   && layout.TextureSlots == 0 && layout.SamplerSlots == 0 && layout.SpirvMagicOk;
Console.WriteLine(goldenEmpty
    ? "[texarr-vulkan] golden has no table at all (the measured mgfxc behavior this rejection rests on)."
    : "[texarr-vulkan] FAIL, the mgfxc golden now carries a table; re-measure the reference and revisit SD0221.");

var texArrResult = await new EffectCompiler().CompileAsync(
    await File.ReadAllTextAsync(texArrFx),
    new CompilerOptions
    {
        Target          = PlatformTarget.Vulkan,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName  = texArrFx,
    });

if (texArrResult.IsSuccess)
{
    var compiled = ShadowDusk.Integration.Tests.MgfxBlobReader.Parse(texArrResult.Value.Data);
    Console.WriteLine("[texarr-vulkan] FAIL, the texture-array fixture COMPILED for Vulkan; it must be rejected with SD0221. " +
                      $"Table: parameters=[{string.Join(", ", compiled.ParameterNames)}] samplerRecords={compiled.Samplers.Count}");
    return 1;
}

foreach (var e in texArrResult.Error)
    Console.WriteLine($"[texarr-vulkan] {e.Code} {Path.GetFileName(e.File)}({e.Line},{e.Column}): {e.Message}");
var sd0221 = texArrResult.Error.FirstOrDefault(e => e.Code == "SD0221");
bool rejected = sd0221 is not null
                && sd0221.Message.Contains("'Tex'", StringComparison.Ordinal)
                && sd0221.Line > 0
                && Path.GetFileName(sd0221.File) == TexArrFixture + ".fx";
Console.WriteLine(rejected
    ? "[texarr-vulkan] rejected loudly with SD0221 naming Tex at its declaration (expected)."
    : "[texarr-vulkan] FAIL, rejected but not with an SD0221 naming Tex at the declaration.");

bool texArrPass = goldenEmpty && rejected;
Console.WriteLine($"[texarr-vulkan] {(texArrPass ? "PASS" : "FAIL")}");
return texArrPass ? 0 : 1;
}

// The SPIR-V version ("1.0", "1.3", ...) of every module embedded in an .mgfx container.
static IEnumerable<string> SpirvVersions(byte[] container)
{
    for (int i = 0; i + 8 <= container.Length; i++)
    {
        if (BitConverter.ToUInt32(container, i) != 0x07230203u)
            continue;
        uint v = BitConverter.ToUInt32(container, i + 4);
        yield return $"{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}";
        i += 7;
    }
}

// Rewrites the version word of every SPIR-V module embedded in an .mgfx container
// (the word after each 0x07230203 magic). Returns how many modules were patched.
static int SetSpirvVersion(byte[] container, uint version)
{
    int count = 0;
    for (int i = 0; i + 8 <= container.Length; i++)
    {
        if (BitConverter.ToUInt32(container, i) != 0x07230203u)
            continue;
        uint current = BitConverter.ToUInt32(container, i + 4);
        if ((current & 0xFFFF00FFu) != 0x00010000u) // not a SPIR-V 1.x version word
            continue;
        BitConverter.GetBytes(version).CopyTo(container, i + 4);
        count++;
        i += 7;
    }
    return count;
}

// ---------------------------------------------------------------------------------------
// Phase 55 - Apos.Shapes FULL SHAPE-GALLERY render-proof: the Vulkan analogue of
// VsDrivenDx's/VsDrivenDx12's "apos" mode. Renders every ShapeBatch public
// Draw*/Fill*/Border* shape method through the REAL Apos.Shapes NuGet package. The golden
// arm is ShapeBatch's own embedded, precompiled effect; the candidate is ShadowDusk's
// Vulkan compile of the SAME upstream shader revision (apos-shapes-sm6.fx, the issue #145
// reproducer).
// ---------------------------------------------------------------------------------------
async Task<int> RunAposPhase()
{
const string AposFixture = "apos-shapes-sm6";

string aposFx  = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "third-party", "Apos.Shapes", AposFixture + ".fx");
string aposOut = Path.Combine(repoRoot, "validation", "output", "vsdriven-vulkan-apos");

Console.WriteLine();
Console.WriteLine($"[apos-vulkan] fixture: {aposFx}");

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

Console.WriteLine($"[apos-vulkan] candidate: {(aposCandidate is null ? "COMPILE FAIL: " + aposCandErr : aposCandidate.Length + " bytes")}\n");

var arms = new List<ShadowDusk.Validation.AposGallery.AposGalleryRenderer.Arm>
{
    new("baseline-embedded", UseEmbeddedGolden: true, EffectBytes: null, CompileError: null),
    new("candidate", UseEmbeddedGolden: false, aposCandidate, aposCandErr),
};

using var aposGame = new ShadowDusk.Validation.AposGallery.AposGalleryRenderer(aposOut, arms);
aposGame.Run();

Console.WriteLine("[apos-vulkan] load + render results:");
foreach (var o in aposGame.Outcomes)
    Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-16} {o.Error ?? "rendered"}");

var aposCaps = aposGame.Captures.ToDictionary(c => c.Name, c => c);
bool aposBoth = aposCaps.ContainsKey("baseline-embedded") && aposCaps.ContainsKey("candidate");
int aposMaxd  = aposBoth ? MaxDelta(aposCaps["baseline-embedded"], aposCaps["candidate"]) : int.MaxValue;
bool aposDrew = aposCaps.TryGetValue("candidate", out var aposCand) && HasVisibleContent(aposCand);

Console.WriteLine($"[apos-vulkan] baseline-vs-candidate maxd: {(aposMaxd == int.MaxValue ? "n/a" : aposMaxd)}");
Console.WriteLine($"[apos-vulkan] candidate drew visible content: {aposDrew}");
if (aposBoth && aposMaxd > 0)
    foreach (var (name, cellMaxd) in ShadowDusk.Validation.AposGallery.AposGalleryRenderer.CellDeltas(
        aposCaps["baseline-embedded"].Pixels, aposCaps["candidate"].Pixels, aposCaps["baseline-embedded"].Width))
        if (cellMaxd > 0)
            Console.WriteLine($"    [cell] {name,-28} maxd={cellMaxd}");

// Tolerance 1/255, consistent with this gate's pre-existing bar (the VsTransformColorTexture
// phase above already used <= 1): measured 2026-07-23 against the package's own embedded
// golden, same class of finding as DX11/DX12 (two independently-built DXC/toolchain
// artifacts, transcendental math reassociated at the 1-ULP level).
bool phase2 = aposBoth && aposMaxd <= 1 && aposDrew;
Console.WriteLine($"[apos-vulkan] phase 2 (Apos.Shapes full gallery): {(phase2 ? "PASS" : "FAIL")}");

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
