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
//   dotnet run --project validation/VsDrivenVulkan -- regslots -> EXPLICIT REGISTER SLOTS: a lone
//                                                                 register(t1), a t0+t2 gap and a
//                                                                 Texture2DArray on t1 (slice 1),
//                                                                 each vs its mgfxc 3.8.5 golden
//                                                                 through SpriteBatch, plus a
//                                                                 known-colour check. Positive
//                                                                 control: SHADOWDUSK_VK_CONTROL=
//                                                                 ordinal-slots (expect red).
string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "vs";
if (mode is not ("vs" or "apos" or "wave" or "texarr" or "texarr-reference" or "regslots"))
{
    Console.Error.WriteLine($"unknown mode '{mode}' — expected 'vs', 'apos', 'wave', 'texarr', 'texarr-reference' or 'regslots'");
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

if (mode == "regslots")
    return await RunRegisterSlotsPhase();

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

// ---------------------------------------------------------------------------------------
// Explicit register slots (mode "regslots"). A Vulkan texture's slot is its SPIR-V binding
// minus 32: mgfxc 3.8.5 writes that, and MonoGame's DesktopVK runtime both binds a texture
// parameter to GraphicsDevice.Textures[slot] and reads a descriptor's image from
// textures[binding - 32]. ShadowDusk used to write the texture's ORDINAL instead, which is the
// same number only when the registers start at 0 with no gap. Each fixture below breaks that:
//
//   RegisterLoneT1            Texture2D on register(t1), nothing at t0. Tex1 = solid colour.
//   RegisterGapT0T2           t0 (SpriteBatch's sprite, the cat) + t2 (Overlay = solid white),
//                             each half the output, so an unbound t2 reads as a dark picture.
//   RegisterTexture2DArrayT1  Texture2DArray on register(t1), sampling slice 1 (slice 0 red,
//                             slice 1 blue), built with MonoGame 3.8.4.1+'s public
//                             Texture2D(..., arraySize) constructor and SetData(level, slice, ...).
//   RegisterSpace0            t1, s1 AND a cbuffer, each on an explicit "space0": a pixel shader
//                             in set 0, where DesktopVK never binds (mgfxc's own build draws
//                             (0,0,0,0)). Tex1 = solid colour, Tint = (1, 0.5, 1, 1). The golden
//                             is mgfxc on the file with the spaces removed.
//   RegisterDeadSampler       an unregistered SpriteTex beside a dead register(s0) sampler, drawn
//                             by SpriteBatch alone (parameter never set): SpriteTex must be slot 0,
//                             as on DirectX. The golden is mgfxc on the DirectX-equivalent form
//                             (SpriteTex : register(t0)).
//
// Each arm (mgfxc 3.8.5 golden, ShadowDusk) is drawn through SpriteBatch on one device and
// must (a) match the other within 1/255 and (b) show the colour the shader computes from the
// textures set, checked per pixel, so two arms that are wrong the same way cannot pass.
// Before the fix the ShadowDusk arm drew (0,0,0,0) for the lone t1 and the array.
//
//   RegisterLoadOnly          a texture on t1 only LOADED (no sampler) beside the sprite pair:
//                             mgfxc writes it its own record (slot 1, sampler slot 255) and its
//                             build draws it; ShadowDusk wrote none. A real mgfxc oracle.
//   RegisterHelperPair        pairs made through helper functions (texture + sampler passed in,
//                             or a texture passed in and a global sampler read). Golden: mgfxc on
//                             the explicit-register form.
//   RegisterPreprocessedPairs pairs only visible once preprocessed (a macro sampler name, a
//                             token-pasting macro, a dead #if 0 pairing). Golden: mgfxc on the
//                             expanded explicit-register form.
//   RegisterTwoSamplers       one texture (Tex1) sampled through two samplers: ShadowDusk aliases
//                             it onto a second descriptor bound to the same parameter. Golden:
//                             mgfxc on the two-texture form (Tex1b set to the same texture).
//   RegisterSamplerPerPass    one sprite texture read through a different sampler in each of two
//                             techniques, drawn through SpriteBatch ALONE (no parameter set), one row
//                             per technique: each must show the sprite (the second with its channels
//                             swapped). The second technique used to read an alias on slot 1, which
//                             only the parameter fills. Golden: mgfxc with one sampler for both.
//   RegisterSamplerPerStage   Tex1 read through one sampler in the VS and another in the PS. The PS's
//                             texture is SpriteBatch's (slot 0, the cat), the VS's the Tex1 parameter
//                             (a solid colour): cat * colour per pixel, where the old alias layout drew
//                             colour * colour. Golden: mgfxc with one sampler for both stages.
//
// Positive controls (SHADOWDUSK_VK_CONTROL), each reverting one fix in the candidate; the run
// must turn red with CONTROL-DETECTED, raised only when the row that fix guards fails:
//   ordinal-slots   records and header slot masks back to ordinal numbering (the first fix);
//                   any row may go red.
//   keep-space      RegisterSpace0's SPIR-V DescriptorSet decorations back to 0 (what the
//                   explicit space0 gave before it was dropped).
//   reserve-dead    RegisterDeadSampler compiled with SpriteTex on t1 (the layout the dead
//                   sampler's reserved s0 used to force).
//   whole-file-pairing  RegisterSamplerPerPass and RegisterSamplerPerStage compiled with the
//                   second sampler's texture moved onto its own t1 declaration (the alias layout the
//                   whole-file pairing produced; RegisterSamplerPerStage's copy is set to Tex1's
//                   texture, as the alias was bound to Tex1).
// ---------------------------------------------------------------------------------------
async Task<int> RunRegisterSlotsPhase()
{
string control = Environment.GetEnvironmentVariable("SHADOWDUSK_VK_CONTROL")?.Trim() ?? "";
if (control is not ("" or "ordinal-slots" or "keep-space" or "reserve-dead" or "whole-file-pairing"))
{
    Console.Error.WriteLine($"unknown SHADOWDUSK_VK_CONTROL '{control}' for regslots (expected 'ordinal-slots', 'keep-space', 'reserve-dead' or 'whole-file-pairing')");
    return 2;
}
if (control != "")
    Console.WriteLine($"[regslots-vulkan] POSITIVE CONTROL ACTIVE: {control} (this run is expected to FAIL)");

string[] fixtures = ["RegisterLoneT1", "RegisterGapT0T2", "RegisterTexture2DArrayT1", "RegisterSpace0", "RegisterDeadSampler",
                     "RegisterLoadOnly", "RegisterHelperPair", "RegisterPreprocessedPairs", "RegisterTwoSamplers",
                     "RegisterSamplerPerPass", "RegisterSamplerPerStage"];
// A render row: the fixture it draws and the technique (the first one when null).
var rows = fixtures.SelectMany(f => f == "RegisterSamplerPerPass"
        ? new[] { (Row: f + ".Point", Fixture: f, Technique: (string?)"Point"), (Row: f + ".Linear", Fixture: f, Technique: (string?)"Linear") }
        : new[] { (Row: f, Fixture: f, Technique: (string?)null) })
    .ToList();
// The rows each fix-specific control must turn red (one of them is enough).
string[] controlRows = control switch
{
    "keep-space" => ["RegisterSpace0"],
    "reserve-dead" => ["RegisterDeadSampler"],
    "whole-file-pairing" => ["RegisterSamplerPerPass.Linear", "RegisterSamplerPerStage"],
    _ => [],
};
string regOut = Path.Combine(repoRoot, "validation", "output", "regslots-vulkan");
Directory.CreateDirectory(regOut);

var regJobs = new List<ShaderJob>();
foreach (string name in fixtures)
{
    string fx = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "register-slots", name + ".fx");
    string golden = Path.Combine(repoRoot, "tests", "fixtures", "golden", "Vulkan", name + ".mgfx");

    byte[]? goldenBytes = File.Exists(golden) ? await File.ReadAllBytesAsync(golden) : null;
    foreach (var row in rows.Where(r => r.Fixture == name))
        regJobs.Add(new($"{row.Row}.mgfxc", goldenBytes, goldenBytes is null ? $"golden not found: {golden}" : null, row.Technique));

    string fxSource = await File.ReadAllTextAsync(fx);
    if (control == "reserve-dead" && name == "RegisterDeadSampler")
    {
        // The pre-fix layout: the dead sampler's s0 was reserved, so SpriteTex went to t1.
        const string Unregistered = "Texture2D SpriteTex;";
        if (!fxSource.Contains(Unregistered, StringComparison.Ordinal))
            throw new InvalidOperationException("reserve-dead control: SpriteTex declaration not found; update the control.");
        fxSource = fxSource.Replace(Unregistered, "Texture2D SpriteTex : register(t1);", StringComparison.Ordinal);
        Console.WriteLine("[regslots-vulkan] reserve-dead control: SpriteTex compiled on t1");
    }
    if (control == "whole-file-pairing" && name is "RegisterSamplerPerPass" or "RegisterSamplerPerStage")
    {
        // The pre-fix layout: the second sampler's pairing on an alias of the texture at t1.
        (string tex, string call) = name == "RegisterSamplerPerPass"
            ? ("SpriteTex", "SpriteTex.Sample(LinearSampler")
            : ("Tex1", "Tex1.Sample(PixelSampler");
        string declaration = $"Texture2D {tex} : register(t0);";
        if (!fxSource.Contains(call, StringComparison.Ordinal) || !fxSource.Contains(declaration, StringComparison.Ordinal))
            throw new InvalidOperationException($"whole-file-pairing control: {name}'s second pairing not found; update the control.");
        fxSource = fxSource
            .Replace(declaration, declaration + $" Texture2D {tex}Alias : register(t1);", StringComparison.Ordinal)
            .Replace(call, call.Replace(tex + ".", tex + "Alias.", StringComparison.Ordinal), StringComparison.Ordinal);
        Console.WriteLine($"[regslots-vulkan] whole-file-pairing control: {name}'s second sampler reads {tex}Alias on t1");
    }
    var result = await new EffectCompiler().CompileAsync(
        fxSource,
        new CompilerOptions { Target = PlatformTarget.Vulkan, IncludeResolver = new FileSystemIncludeResolver(), SourceFileName = fx });
    byte[]? cand = null;
    string? candErr = null;
    if (result.IsFailure)
        candErr = string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));
    else
    {
        cand = result.Value.Data;
        if (control == "ordinal-slots")
            Console.WriteLine($"[regslots-vulkan] ordinal-slots control: {name}: {ApplyOrdinalSlots(cand)}");
        if (control == "keep-space" && name == "RegisterSpace0")
            Console.WriteLine($"[regslots-vulkan] keep-space control: {SetDescriptorSets(cand, from: 1, to: 0)} DescriptorSet decoration(s) set back to 0");
        await File.WriteAllBytesAsync(Path.Combine(regOut, name + ".candidate.mgfx"), cand);
    }
    foreach (var row in rows.Where(r => r.Fixture == name))
        regJobs.Add(new($"{row.Row}.sd", cand, candErr, row.Technique));

    foreach ((string arm, byte[]? bytes) in new[] { ("mgfxc", goldenBytes), ("sd", cand) })
    {
        if (bytes is null)
            continue;
        var parsed = ShadowDusk.Integration.Tests.MgfxBlobReader.Parse(bytes);
        string headers = string.Join("; ", parsed.Shaders.Select(sh =>
        {
            var header = ShadowDusk.Integration.Tests.VulkanShaderCodeReader.Parse(sh.Bytecode);
            return $"{(sh.IsVertex ? "vs" : "ps")} textureSlots=0x{header.TextureSlots:x} bindings=[{string.Join(",", header.Bindings.Select(b => b.Binding))}]";
        }));
        Console.WriteLine($"[regslots-vulkan] {name} {arm,-5} records=[{string.Join(", ", parsed.Samplers.Select(s => $"s{s.ShaderIndex}:tex{s.TextureSlot}/samp{s.SamplerSlot}->param{s.Parameter}"))}] {headers}");
    }
}

// Test textures, made once per device (the renderer calls back per job).
var lone   = new Color(51, 204, 102, 255);
var slice0 = new Color(255, 0, 0, 255);
var slice1 = new Color(0, 128, 255, 255);
var tinted = new Color(51, 102, 102, 255); // lone * (1, 0.5, 1, 1)
Microsoft.Xna.Framework.Graphics.Texture2D? loneTex = null, whiteTex = null, arrayTex = null;
Color[]? catPixels = null;

using var regGame = new EffectImageRenderer(catPath, regOut, regJobs, (effect, cat) =>
{
    var gd = effect.GraphicsDevice;
    // Device texture slots outlive a job: without this the previous arm's texture still sits
    // in the slot this arm's descriptor reads, and a mis-slotted effect renders correctly by
    // accident (measured: the ordinal-slots control stayed green until this reset).
    for (int s = 0; s < 16; s++)
        gd.Textures[s] = null;
    for (int s = 0; s < 4; s++)
        gd.VertexTextures[s] = null;
    if (catPixels is null)
    {
        catPixels = new Color[cat.Width * cat.Height];
        cat.GetData(catPixels);
    }
    // RegisterSamplerPerStage's vertex shader: SpriteBatch's own projection (SpriteEffect's).
    effect.Parameters["WVP"]?.SetValue(Matrix.CreateOrthographicOffCenter(0, gd.Viewport.Width, gd.Viewport.Height, 0, 0, -1));
    if (effect.Parameters["Tex1Alias"] is { } t1Alias)
    {
        loneTex ??= Solid(gd, lone);
        t1Alias.SetValue(loneTex);
    }
    if (effect.Parameters["Tex1"] is { } t1)
    {
        loneTex ??= Solid(gd, lone);
        t1.SetValue(loneTex);
    }
    if (effect.Parameters["Tex1b"] is { } t1b)
    {
        loneTex ??= Solid(gd, lone);
        t1b.SetValue(loneTex);
    }
    if (effect.Parameters["Overlay"] is { } overlay)
    {
        whiteTex ??= Solid(gd, Color.White);
        overlay.SetValue(whiteTex);
    }
    if (effect.Parameters["Tint1"] is { } tint1)
    {
        whiteTex ??= Solid(gd, Color.White);
        tint1.SetValue(whiteTex);
    }
    effect.Parameters["Tint"]?.SetValue(new Vector4(1f, 0.5f, 1f, 1f));
    if (effect.Parameters["Arr"] is { } arr)
    {
        if (arrayTex is null)
        {
            arrayTex = new Microsoft.Xna.Framework.Graphics.Texture2D(gd, 4, 4, false, Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color, 2);
            arrayTex.SetData(0, 0, null, Enumerable.Repeat(slice0, 16).ToArray(), 0, 16);
            arrayTex.SetData(0, 1, null, Enumerable.Repeat(slice1, 16).ToArray(), 0, 16);
        }
        arr.SetValue(arrayTex);
    }
});
regGame.Run();

Console.WriteLine("[regslots-vulkan] load + render results:");
foreach (var o in regGame.Outcomes)
    Console.WriteLine($"  [{(o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL")}] {o.Name,-32} {o.Error ?? o.PngPath}");

var regCaps = regGame.Captures.ToDictionary(c => c.Name, c => c);
bool allPass = true;
bool controlDetected = false;
foreach (string name in rows.Select(r => r.Row))
{
    bool haveGolden = regCaps.TryGetValue($"{name}.mgfxc", out var g);
    bool haveCand   = regCaps.TryGetValue($"{name}.sd", out var c);
    int maxd = haveGolden && haveCand ? MaxDelta(g, c) : int.MaxValue;

    // What the shader computes from the textures set, per pixel.
    Func<(string Name, Color[] Pixels, int Width, int Height), (bool Ok, string Detail)> expect = name switch
    {
        "RegisterLoneT1"           => cap => AllPixels(cap, p => Near(p, lone), $"every pixel ({lone.R},{lone.G},{lone.B},{lone.A})"),
        "RegisterTexture2DArrayT1" => cap => AllPixels(cap, p => Near(p, slice1), $"every pixel slice 1 ({slice1.R},{slice1.G},{slice1.B},{slice1.A})"),
        "RegisterSpace0"           => cap => AllPixels(cap, p => Near(p, tinted), $"every pixel Tex1 * Tint ({tinted.R},{tinted.G},{tinted.B},{tinted.A})"),
        // SpriteBatch's own sprite, the cat: visible content (a slot nothing binds draws nothing).
        "RegisterDeadSampler"      => cap => (HasVisibleContent(cap), $"the sprite (visible content); centre {Centre(cap)}"),
        // (cat + Tex1 + Tex1) / 3: green at least 136 everywhere. An unbound alias slot gives (cat + Tex1) / 3.
        "RegisterTwoSamplers"      => cap => AllPixels(cap, p => p.G >= 135 && p.A >= 254, "every pixel green >= 135 ((cat + Tex1 + Tex1) / 3)"),
        // SpriteBatch's sprite on slot 0 in each technique; an unbound slot draws (0,0,0,0).
        "RegisterSamplerPerPass.Point"  => cap => PerPixel(cap, catPixels!, k => k, 1, "every pixel the cat"),
        "RegisterSamplerPerPass.Linear" => cap => PerPixel(cap, catPixels!, k => new Color(k.B, k.G, k.R, k.A), 1, "every pixel the cat, channels swapped (.bgra)"),
        // PS: the sprite (cat); VS: the Tex1 parameter (lone). The old alias layout gave lone * lone.
        "RegisterSamplerPerStage"  => cap => PerPixel(cap, catPixels!, k => new Color(k.R * lone.R / 255, k.G * lone.G / 255, k.B * lone.B / 255, k.A * lone.A / 255), 2,
                                              $"every pixel cat * ({lone.R},{lone.G},{lone.B},{lone.A})"),
        // (cat + Tex1) / 2: green at least 102 everywhere (Tex1's 204 / 2). An unbound Tex1 gives cat / 2.
        "RegisterLoadOnly"         => cap => AllPixels(cap, p => p.G >= 101 && p.A >= 254, "every pixel green >= 101 ((cat + loaded texel) / 2)"),
        // (cat + white + white) / 3: every channel at least 170. One unbound texture gives (cat + white) / 3.
        "RegisterHelperPair" or "RegisterPreprocessedPairs"
                                   => cap => AllPixels(cap, p => p.R >= 169 && p.G >= 169 && p.B >= 169 && p.A >= 254, "every channel >= 169 ((cat + white + white) / 3)"),
        // (cat + white) / 2: every channel at least 127. An unbound t2 gives cat / 2.
        _                          => cap => AllPixels(cap, p => p.R >= 127 && p.G >= 127 && p.B >= 127 && p.A >= 254, "every channel >= 127 ((cat + white) / 2)"),
    };
    var (goldenOk, goldenDetail) = haveGolden ? expect(g) : (false, "no golden render");
    var (candOk, candDetail)     = haveCand ? expect(c) : (false, "no candidate render");

    bool pass = maxd <= 1 && goldenOk && candOk;
    allPass &= pass;
    Console.WriteLine($"[regslots-vulkan] {name}: maxd {(maxd == int.MaxValue ? "n/a" : maxd)}; mgfxc {(goldenOk ? "OK" : "WRONG")} ({goldenDetail}); " +
                      $"candidate {(candOk ? "OK" : "WRONG")} ({candDetail}) -> {(pass ? "PASS" : "FAIL")}");
    bool rowRed = goldenOk && (!candOk || maxd > 1);
    if (rowRed && (control == "ordinal-slots" || controlRows.Contains(name)))
        controlDetected = true;
}

if (controlDetected)
    Console.WriteLine($"[regslots-vulkan] CONTROL-DETECTED: the {control} candidate diverged from mgfxc's render");
Console.WriteLine($"[regslots-vulkan] {(allPass ? "PASS" : "FAIL")}");
return allPass ? 0 : 1;
}

static Microsoft.Xna.Framework.Graphics.Texture2D Solid(Microsoft.Xna.Framework.Graphics.GraphicsDevice gd, Color color)
{
    var t = new Microsoft.Xna.Framework.Graphics.Texture2D(gd, 4, 4);
    t.SetData(Enumerable.Repeat(color, 16).ToArray());
    return t;
}

// Every pixel within 	olerance (per channel) of want(cat pixel) at the same position.
static (bool Ok, string Detail) PerPixel((string Name, Color[] Pixels, int Width, int Height) cap, Color[] cat, Func<Color, Color> want, int tolerance, string what)
{
    if (cat.Length != cap.Pixels.Length)
        return (false, $"{what}: capture is {cap.Pixels.Length} pixels, the cat {cat.Length}");
    for (int i = 0; i < cat.Length; i++)
    {
        Color got = cap.Pixels[i], expect = want(cat[i]);
        if (Math.Abs(got.R - expect.R) > tolerance || Math.Abs(got.G - expect.G) > tolerance
            || Math.Abs(got.B - expect.B) > tolerance || Math.Abs(got.A - expect.A) > tolerance)
            return (false, $"{what}: pixel {i} is ({got.R},{got.G},{got.B},{got.A}), want ({expect.R},{expect.G},{expect.B},{expect.A})");
    }
    return (true, what);
}

static string Centre((string Name, Color[] Pixels, int Width, int Height) cap)
{
    Color c = cap.Pixels[(cap.Height / 2) * cap.Width + cap.Width / 2];
    return $"({c.R},{c.G},{c.B},{c.A})";
}

static bool Near(Color a, Color b) =>
    Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1 && Math.Abs(a.A - b.A) <= 1;

static (bool Ok, string Detail) AllPixels((string Name, Color[] Pixels, int Width, int Height) cap, Func<Color, bool> ok, string want)
{
    int bad = cap.Pixels.Count(p => !ok(p));
    Color centre = cap.Pixels[(cap.Height / 2) * cap.Width + cap.Width / 2];
    return (bad == 0, $"want {want}; {bad}/{cap.Pixels.Length} off; centre ({centre.R},{centre.G},{centre.B},{centre.A})");
}

// The ordinal-slots positive control: renumbers every Vulkan descriptor header's texture and
// sampler slot masks (and its texture-type table) and every positional sampler record
// ("ps_s<N>") to ORDINAL slots, 0, 1, 2, ... in slot order, which is exactly what ShadowDusk
// wrote before the fix. The SPIR-V and its bindings are left alone, so the descriptors still
// read slot binding - 32. Measured equal to the real pre-fix compile of each fixture except
// for the 4-byte effect hash in the MGFX header (bytes 6..9), which covers the content.
// Returns a summary of what was patched.
static string ApplyOrdinalSlots(byte[] mgfx)
{
    var parsed = ShadowDusk.Integration.Tests.MgfxBlobReader.Parse(mgfx);
    var remap = new Dictionary<int, int>();
    int headers = 0;
    int recordsFrom = mgfx.Length;
    foreach (var shader in parsed.Shaders)
    {
        // The shader's ShaderCode field, located by its own bytes: the descriptor header is
        // its first 84 bytes (VulkanShaderCodeReader's layout).
        int i = mgfx.AsSpan().IndexOf(shader.Bytecode);
        if (i < 0)
            throw new InvalidOperationException("ordinal-slots control: shader code not found in its container.");
        recordsFrom = Math.Min(recordsFrom, i + shader.Bytecode.Length);
        uint tSlots = BitConverter.ToUInt32(mgfx, i + 8);
        uint sSlots = BitConverter.ToUInt32(mgfx, i + 12);
        if (tSlots == 0)
            continue;

        var slots = Enumerable.Range(0, 32).Where(b => (tSlots & (1u << b)) != 0).ToList();
        uint newT = 0, newS = 0;
        var types = new uint[16];
        for (int k = 0; k < slots.Count; k++)
        {
            int slot = slots[k];
            if (remap.TryGetValue(slot, out int prior) && prior != k)
                throw new InvalidOperationException("ordinal-slots control: shaders disagree on the ordinal of a slot; extend the control.");
            remap[slot] = k;
            newT |= 1u << k;
            if ((sSlots & (1u << slot)) != 0)
                newS |= 1u << k;
            if (slot < 16 && k < 16)
                types[k] = BitConverter.ToUInt32(mgfx, i + 16 + 4 * slot);
        }
        BitConverter.GetBytes(newT).CopyTo(mgfx, i + 8);
        BitConverter.GetBytes(newS).CopyTo(mgfx, i + 12);
        for (int k = 0; k < 16; k++)
            BitConverter.GetBytes(types[k]).CopyTo(mgfx, i + 16 + 4 * k);
        headers++;
    }

    // Records (after the first shader code): type, texSlot, samplerSlot, hasState (false),
    // name "ps_sN" (length 5), parameter.
    int records = 0;
    for (int j = Math.Max(4, recordsFrom); j + 6 <= mgfx.Length; j++)
    {
        if (mgfx[j] != 5 || mgfx[j + 1] != 'p' || mgfx[j + 2] != 's' || mgfx[j + 3] != '_' || mgfx[j + 4] != 's')
            continue;
        int digit = mgfx[j + 5] - '0';
        if (digit is < 0 or > 9 || mgfx[j - 1] != 0 || mgfx[j - 2] != digit || mgfx[j - 3] != digit || !remap.TryGetValue(digit, out int k))
            continue;
        mgfx[j - 3] = (byte)k;
        mgfx[j - 2] = (byte)k;
        mgfx[j + 5] = (byte)('0' + k);
        records++;
    }
    if (headers == 0 || records == 0)
        throw new InvalidOperationException($"ordinal-slots control: patched {headers} header(s) and {records} record(s); the container shape changed, update the control.");
    return $"{headers} header(s), {records} record(s), slots {string.Join(", ", remap.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}->{kv.Value}"))}";
}

// The keep-space control: rewrites every OpDecorate DescriptorSet <from> to <to> in every SPIR-V
// module of the container (located through the parsed shader code, the SPIR-V after the 84-byte
// descriptor header and its 24-byte bindings). Returns how many decorations changed.
static int SetDescriptorSets(byte[] mgfx, uint from, uint to)
{
    const uint OpDecorate = 71, DescriptorSet = 34;
    var parsed = ShadowDusk.Integration.Tests.MgfxBlobReader.Parse(mgfx);
    int changed = 0;
    foreach (var shader in parsed.Shaders)
    {
        int code = mgfx.AsSpan().IndexOf(shader.Bytecode);
        uint bindings = BitConverter.ToUInt32(mgfx, code + 80);
        int spirv = code + 84 + 24 * (int)bindings;
        int end = code + shader.Bytecode.Length;
        if (BitConverter.ToUInt32(mgfx, spirv) != 0x07230203u)
            throw new InvalidOperationException("keep-space control: no SPIR-V module where the header says; update the control.");
        for (int w = spirv + 20; w + 4 <= end;)
        {
            uint word = BitConverter.ToUInt32(mgfx, w);
            int count = (int)(word >> 16);
            if (count == 0)
                break;
            if ((word & 0xFFFF) == OpDecorate && count == 4
                && BitConverter.ToUInt32(mgfx, w + 8) == DescriptorSet && BitConverter.ToUInt32(mgfx, w + 12) == from)
            {
                BitConverter.GetBytes(to).CopyTo(mgfx, w + 12);
                changed++;
            }
            w += 4 * count;
        }
    }
    if (changed == 0)
        throw new InvalidOperationException("keep-space control: no DescriptorSet decoration changed; update the control.");
    return changed;
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
