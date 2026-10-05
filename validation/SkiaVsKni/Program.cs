// =============================================================================
// SkiaVsKni: issue #369 (Phase 62 Area D, D2), the Gum shader-parity render gate.
// -----------------------------------------------------------------------------
// Gum's authoring tool runs on KNI and its saved projects load in SkiaSharp, so ONE .fx file has
// to give the same image in both. This driver takes each texture-only XnaFiddle example and
// renders it twice, in the two REAL runtimes:
//
//     the same .fx --[EffectCompiler, OpenGL]--> .mgfx --> real KNI v4.2.9001 SDL2.GL Effect
//               \--[SkslConverter]-----------> SkSL --> real SkiaSharp SKRuntimeEffect (CPU)
//                         pixel-diff, same source texels, same size, same tint
//
// Skia has no reference compiler, so the evidence model is rendered-image fidelity (owner
// decision 2026-08-13, project_decisions.md), never mgfxc-equivalence. The KNI arm is the
// reference because ShadowDusk's OpenGL output is rung-4 proven on real KNI desktop
// (validation/KniDesktopGL). Tolerance 2/255 (Phase 62's bar: SkSL `half` precision).
//
// The Skia side is set up the way Gum's Skia renderer will use it: the container texture is the
// child shader, ShadowDusk_Color is the container tint (COLOR0, issue #368), and
// ShadowDusk_Resolution is the child size (issue #371). The KNI side draws the same texture
// through SpriteBatch with the same tint as the vertex color. Each shader runs twice: untinted
// (white, Gum's default) and tinted (a non-grey colour, so a dropped or mis-mapped COLOR0 shows).
//
// Corpus: Fading, Grayscale, Invert, Pixelated, Tint (TintShader upstream), plus the stretch case
// Mask, whose second texture is a second child shader on Skia and the MaskTexture parameter on
// KNI. Mask writes straight (non-premultiplied) alpha and expects BlendState.NonPremultiplied,
// while Skia treats a runtime effect's output as premultiplied; this gate compares the raw
// shader output, so a consumer compositing Mask must still pick the matching blend on each side.
//
// No XnaFiddle shader in this corpus is time-dependent: Fading fades by texture V, not by time.
// The only free uniform is Tint's TintColor, set to the same fixed value on both arms.
//
// Positive controls (each MUST diverge, or the gate fails):
//   * control-dropped-tint     Grayscale, tinted on KNI, ShadowDusk_Color left white on Skia
//                              (the Gum hand-port bug class, Phase 62 section 2.6);
//   * control-flipped-v        Fading's SkSL with its texture V flipped (Y orientation);
//   * control-wrong-resolution Pixelated with ShadowDusk_Resolution at half the child size.
//
// Single process: Skia's CPU raster needs no window or GL context.
// Exit: 0 pass (or no GL context without SHADOWDUSK_REQUIRE_GL=1), 1 divergence, 2 setup error.
// =============================================================================

using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using ShadowDusk.Compiler;
using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;
using ShadowDusk.Validation.SkiaVsKni;

const string ColorUniform = "ShadowDusk_Color";
const string ResolutionUniform = "ShadowDusk_Resolution";

// ---- Runtime-integrity guard (as validation/KniDesktopGL): never mislabel a MonoGame render as KNI.
AssemblyName xna = typeof(Game).Assembly.GetName();
bool isKni = xna.Name is not null
    && xna.Name.StartsWith("Xna.Framework", StringComparison.OrdinalIgnoreCase)
    && !xna.Name.StartsWith("MonoGame", StringComparison.OrdinalIgnoreCase);
if (!isKni)
{
    Console.Error.WriteLine($"[skia-kni] SETUP FAILED: expected the KNI (nkast 'Xna.Framework.*') runtime, got '{xna.Name}'");
    return 2;
}

int tolerance = 2;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string repoRoot = ShaderInputs.FindRepoRoot();
string fixtures = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "third-party", "XnaFiddle");
string outDir = Path.Combine(repoRoot, "validation", "output-skia-kni");
Directory.CreateDirectory(outDir);

Console.WriteLine("=== Phase 62 Area D: Skia vs KNI render gate (real SkiaSharp vs real KNI SDL2.GL) ===");
Console.WriteLine($"[skia-kni] KNI: {xna.Name} {xna.Version}   SkiaSharp: {typeof(SkiaSharp.SKCanvas).Assembly.GetName().Version}");
Console.WriteLine($"[skia-kni] out: {outDir}  size: {Images.Size}  tolerance: {tolerance}\n");

// Every free uniform any case declares, set identically on both arms. A converted shader whose
// SkSL declares a uniform missing here (other than the two synthesized ones) fails setup, so no
// uniform rides along unexercised.
var uniforms = new Dictionary<string, float[]>(StringComparer.Ordinal)
{
    ["TintColor"] = [1f, 0.5f, 0.5f, 1f],
};

// The texture each HLSL texture name binds: SpriteTexture is the container (SpriteBatch unit 0
// on KNI, the first child on Skia); MaskTexture is Mask's second texture.
var textureNames = new HashSet<string>(StringComparer.Ordinal) { "SpriteTexture", "MaskTexture" };

// Tint alpha stays 255, so the tint changes colour only. What either runtime then does with a
// translucent output is a compositing question (blend state), not a shader one; both arms write
// the raw output (BlendState.Opaque / SKBlendMode.Src), which Mask's rgb > a output shows is
// compared unclamped.
var tints = new (string Label, byte[] Rgba)[]
{
    ("white", [255, 255, 255, 255]),
    ("tinted", [255, 200, 150, 255]),
};

var cases = new (string Name, string File)[]
{
    ("Fading", "XnaFiddle-Fading.fx"),
    ("Grayscale", "XnaFiddle-Grayscale.fx"),
    ("Invert", "XnaFiddle-Invert.fx"),
    ("Pixelated", "XnaFiddle-Pixelated.fx"),
    ("Tint", "XnaFiddle-Tint.fx"),
    ("Mask", "XnaFiddle-Mask.fx"),
};

var setupErrors = new List<string>();
var kniJobs = new List<KniJob>();
var skiaJobs = new List<SkiaJob>();
var sksl = new Dictionary<string, SkslConversion>(StringComparer.Ordinal);

foreach ((string name, string file) in cases)
{
    string path = Path.Combine(fixtures, file);
    if (!File.Exists(path))
    {
        setupErrors.Add($"{name}: fixture not found: {path}");
        continue;
    }
    string fx = await File.ReadAllTextAsync(path);

    var mgfx = await new EffectCompiler().CompileAsync(fx, new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = path,
    });
    if (mgfx.IsFailure)
    {
        setupErrors.Add($"{name}: OpenGL compile failed: {string.Join(" | ", mgfx.Error.Select(e => $"{e.Code}: {e.Message}"))}");
        continue;
    }
    await File.WriteAllBytesAsync(Path.Combine(outDir, name + ".mgfx"), mgfx.Value.Data);

    var converted = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = path });
    if (converted.IsFailure)
    {
        setupErrors.Add($"{name}: SkSL conversion refused: {string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}"))}");
        continue;
    }
    SkslConversion conversion = converted.Value;
    sksl[name] = conversion;
    await File.WriteAllTextAsync(Path.Combine(outDir, name + ".sksl"), conversion.SkslText);

    foreach (string child in conversion.ChildShaders.Where(c => !textureNames.Contains(c)))
        setupErrors.Add($"{name}: child shader '{child}' has no texture in this gate");
    IReadOnlyList<string> declaredUniforms = SkiaArm.DeclaredUniforms(conversion.SkslText, out string? compileErrors);
    if (compileErrors is not null)
        setupErrors.Add($"{name}: Skia's compiler rejected the emission: {compileErrors}");
    foreach (string declared in declaredUniforms)
    {
        if (declared is not (ColorUniform or ResolutionUniform) && !uniforms.ContainsKey(declared))
            setupErrors.Add($"{name}: the SkSL declares uniform '{declared}' that this gate never sets");
    }

    Console.WriteLine($"[skia-kni] {name}: converted; children [{string.Join(", ", conversion.ChildShaders)}], " +
                      $"synthesized [{string.Join(", ", conversion.SynthesizedUniforms)}]");

    foreach ((string label, byte[] rgba) in tints)
    {
        string job = $"{name}-{label}";
        kniJobs.Add(new KniJob(job, mgfx.Value.Data, rgba, uniforms));
        skiaJobs.Add(new SkiaJob(job, conversion.SkslText, conversion.ChildShaders, rgba, uniforms,
            Resolution: [Images.Size, Images.Size]));
    }
}

// ---- positive controls: Skia-only renders compared against an unmutated case's KNI image ----
var controls = new List<(string Name, string Against)>();
void AddControl(string name, string against, Func<SkiaJob, SkiaJob?> mutate)
{
    SkiaJob? baseJob = skiaJobs.FirstOrDefault(j => j.Name == against);
    if (baseJob is null)
    {
        setupErrors.Add($"{name}: control base '{against}' did not convert");
        return;
    }
    SkiaJob? mutated = mutate(baseJob);
    if (mutated is null || mutated == baseJob)
    {
        setupErrors.Add($"{name}: the mutation did not change the Skia job, so the control proves nothing");
        return;
    }
    skiaJobs.Add(mutated with { Name = name });
    controls.Add((name, against));
}

AddControl("control-dropped-tint", "Grayscale-tinted",
    j => j with { Tint = [255, 255, 255, 255] });
AddControl("control-flipped-v", "Fading-white",
    j => FlipV(j.Sksl) is { } flipped && flipped != j.Sksl ? j with { Sksl = flipped } : null);
AddControl("control-wrong-resolution", "Pixelated-white",
    j => j with { Resolution = [Images.Size / 2, Images.Size / 2] });

if (setupErrors.Count > 0)
{
    Console.Error.WriteLine("\n[skia-kni] SETUP FAILED:");
    foreach (string e in setupErrors)
        Console.Error.WriteLine("  " + e);
    return 2;
}

byte[] source = Images.Source();
byte[] mask = Images.Mask();
var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal)
{
    ["SpriteTexture"] = source,
    ["MaskTexture"] = mask,
};
Images.WritePng(Path.Combine(outDir, "source.png"), source);
Images.WritePng(Path.Combine(outDir, "mask.png"), mask);

// ---- Skia arm (CPU raster) ----
var skiaImages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
foreach (SkiaJob job in skiaJobs)
{
    var rendered = SkiaArm.Render(job, textures);
    if (rendered.IsFailure)
    {
        Console.Error.WriteLine($"[skia-kni] FAIL: Skia arm, {job.Name}: {rendered.Error}");
        return 1;
    }
    skiaImages[job.Name] = rendered.Value;
    Images.WritePng(Path.Combine(outDir, job.Name + ".skia.png"), rendered.Value);
}

// ---- KNI arm (real SDL2.GL) ----
bool requireGl = Environment.GetEnvironmentVariable("SHADOWDUSK_REQUIRE_GL") == "1";
var kni = new KniArm(kniJobs, textures);
using (kni)
    kni.Run();
if (kni.Skipped)
{
    if (requireGl)
    {
        Console.Error.WriteLine($"[skia-kni] FAIL: SHADOWDUSK_REQUIRE_GL=1 but KNI got no GL context: {kni.SkipReason}");
        return 1;
    }
    Console.WriteLine($"[skia-kni] SKIPPED: KNI got no GL context: {kni.SkipReason}");
    return 0;
}
if (kni.Failures.Count > 0)
{
    foreach (string f in kni.Failures)
        Console.Error.WriteLine("[skia-kni] FAIL: KNI arm: " + f);
    return 1;
}
foreach ((string name, byte[] px) in kni.Images)
    Images.WritePng(Path.Combine(outDir, name + ".kni.png"), px);

// ---- compare ----
bool ok = true;
Console.WriteLine();
foreach (KniJob job in kniJobs)
{
    byte[] k = kni.Images[job.Name];
    byte[] s = skiaImages[job.Name];
    (int maxd, int over) = Images.Compare(k, s, tolerance);
    int distinct = Images.DistinctColors(k);
    bool nontrivial = distinct > 16;
    bool match = over == 0;
    ok &= match && nontrivial;
    Console.WriteLine($"[skia-kni] {job.Name,-18} maxd {maxd,3}  px over tol {over,5}/{Images.Size * Images.Size}  " +
                      $"distinct colors {distinct,5}  -> {(match && nontrivial ? "MATCH" : nontrivial ? "DIVERGED" : "TRIVIAL IMAGE")}");
    if (!match)
        Images.WriteDiffPng(Path.Combine(outDir, job.Name + ".diff.png"), k, s);
}
foreach ((string name, string against) in controls)
{
    (int maxd, int over) = Images.Compare(kni.Images[against], skiaImages[name], tolerance);
    bool caught = over > 0;
    ok &= caught;
    Console.WriteLine($"[skia-kni] {name,-26} maxd {maxd,3}  px over tol {over,5}  -> " +
                      (caught ? "CAUGHT (control diverges, as it must)" : "MISSED: the comparison cannot see this bug class"));
}

Console.WriteLine($"\n[skia-kni] {(ok ? "PASS" : "FAIL")}: XnaFiddle texture-only shaders, real SkiaSharp vs real KNI, " +
                  $"rendered-image fidelity at {tolerance}/255.");
return ok ? 0 : 1;

// -----------------------------------------------------------------------------

// The flip control: the converter derives the interpolated UV as `vec2 _sd_uv = coord /
// ShadowDusk_Resolution;` (issue #371). Flip its V right after that definition, so every
// arithmetic read of the UV (Fading's fade ramp) sees the Y-flipped coordinate. Null when the
// emission has no such line, which fails setup rather than passing a control that changed nothing.
static string? FlipV(string skslText)
{
    int main = skslText.IndexOf("main(", StringComparison.Ordinal);
    if (main < 0)
        return null;
    string body = skslText[main..];
    Match def = Regex.Match(body, @"(?m)^(\s*)(?:float2|vec2) _sd_uv = [^;]+;");
    if (!def.Success)
        return null;
    string indent = def.Groups[1].Value;
    string flipped = body.Insert(def.Index + def.Length, $"\n{indent}_sd_uv.y = 1.0 - _sd_uv.y;");
    return skslText[..main] + flipped;
}
