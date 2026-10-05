// =============================================================================
// RaylibRoute: Phase 59 render gate for the raylib converter, in REAL Raylib-cs.
// -----------------------------------------------------------------------------
// raylib has no reference compiler, so the evidence model is rendered-image fidelity (owner
// decision 2026-10-01, the SkSL target's model), never mgfxc-equivalence:
//
//     the same .fx --[EffectCompiler, OpenGL]--> .mgfx --> real MonoGame DesktopGL  (rung-4 proven)
//               \--[RaylibConverter]----------> glsl330 --> real Raylib-cs 8.1.0 (raylib 6.0)
//                                      pixel-diff, same uniforms, same source texels
//
// The two arms run as separate child processes of this one (MonoGame brings SDL2, raylib brings
// GLFW, and macOS will not let two windowing libraries share one process's main thread cleanly).
// This parent compiles everything, writes jobs.json, runs both arms, and compares.
//
// What it proves, per case: the converter's output LOADS in raylib (a raylib load failure falls
// back to its default shader, which is detected and failed) and RENDERS the same picture as the
// proven OpenGL backend's build of the same source, within --tolerance (default 2/255).
//
// Positive controls (each MUST diverge, or the gate fails): the converter's own emission with
//   * its COLOR0 tint dropped (the Gum hand-port bug class, Phase 62 §2.6),
//   * its texture coordinate's V flipped (the Y-orientation bug class SD0634 exists for),
//   * and the CRT's curvature uniform nudged by 0.01 on the raylib arm only (a ~1 px shift),
// so a comparator that cannot see a real converter bug turns this gate red.
//
// .slang arms (issue #253): the Slang twin of six cases (one samples its second-declared texture
// first, so a wrong texture0 shows) converts through the built-in subset
// frontend AND real slangc, and each raylib render must match the .fx case's MonoGame image; a
// mutated Sepia twin through real slangc is the arms' own positive control.
//
// Exit: 0 pass (or no GL context without SHADOWDUSK_REQUIRE_GL=1), 1 divergence, 2 setup error.
// =============================================================================

using System.Diagnostics;
using System.Reflection;
using ShadowDusk.Compiler;
using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Slang;
using ShadowDusk.Validation;
using ShadowDusk.Validation.RaylibRoute;

const int ArmNoGl = 3;

if (args.Length >= 3 && args[0] == "--arm")
    return RunArm(args[1], args[2]);

int tolerance = 2;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string repoRoot = ShaderInputs.FindRepoRoot();
string fixtures = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string outDir = Path.Combine(repoRoot, "validation", "output-raylib");
Directory.CreateDirectory(outDir);
const int Size = 128;

Console.WriteLine("=== Phase 59 raylib route render gate (real Raylib-cs vs real MonoGame DesktopGL) ===");
Console.WriteLine($"[route] out: {outDir}  size: {Size}  tolerance: {tolerance}\n");

// Every uniform any case declares, set identically on both arms. A converted shader whose
// contract names a uniform missing here fails the gate, so no uniform rides along unexercised.
var uniforms = new Dictionary<string, float[]>(StringComparer.Ordinal)
{
    ["TintColor"] = [1f, 0.5f, 0.5f, 1f],
    ["_sepiaTone"] = [1.2f, 1.0f, 0.8f],
    ["BloomThreshold"] = [0.25f, 0.25f, 0.25f, 0.25f],
    ["BloomIntensity"] = [1.5f],
    ["BloomSaturation"] = [0.8f],
    ["_attenuation"] = [800f],
    ["_linesFactor"] = [0.04f],
    ["angle"] = [0.5f],
    ["scale"] = [0.5f],
    ["ScreenSize"] = [Size, Size],
    ["_progress"] = [0.5f],
    ["_dissolveThreshold"] = [0.04f],
    ["_dissolveThresholdColor"] = [1f, 0.5f, 0f, 1f],
    ["Resolution"] = [Size, Size],
    ["Curvature"] = [0.12f],
    ["ScanlineIntensity"] = [0.35f],
    ["VignetteStrength"] = [0.4f],
    ["ChromaOffset"] = [1.5f],
    ["ShadeDark"] = [0.06f, 0.22f, 0.06f, 1f],
    ["ShadeLight"] = [0.61f, 0.74f, 0.06f, 1f],
    ["Levels"] = [4f],
    ["Saturation"] = [0.25f],
    ["CellSize"] = [4f],
    ["GapDarken"] = [0.35f],
};

// The 10-shader GL corpus (rung-4 proven on GL by Baseline/Candidate), the two effects the
// Phase 59 request named, Gum's Grayscale, whose COLOR0 tint is the varying the SkSL target
// cannot carry and raylib can (fragColor), and the two issue #308 fixtures whose legacy samplers
// an #include or a macro supplies (issue #327 for the converter).
var cases = new List<(string Name, string Path)>();
foreach (string name in ShaderInputs.ShaderNames)
    cases.Add((name, Path.Combine(fixtures, name + ".fx")));
cases.Add(("CrtFilter", Path.Combine(fixtures, "raylib", "CrtFilter.fx")));
cases.Add(("RetroHandheld", Path.Combine(fixtures, "raylib", "RetroHandheld.fx")));
cases.Add(("GumGrayscale", Path.Combine(fixtures, "third-party", "Gum", "MonoGameInCode-Grayscale.fx")));
// Issue #327: the legacy samplers live in an #include (LegacyInclude) or come out of macros
// (LegacyMacroDecl), the shapes the converter used to refuse with DXC's own error. Both masks
// are bound by name to the extra texture, on units 2 and 3, so the picture is (src.r, src.g, 0, 1)
// only when the recovered conversion binds exactly what the OpenGL build binds.
cases.Add(("LegacyInclude", Path.Combine(fixtures, "SamplerLegacyInclude.fx")));
cases.Add(("LegacyMacroDecl", Path.Combine(fixtures, "SamplerLegacyMacroDecl.fx")));
// Issue #253: two textures, the SECOND declared sampled FIRST. Real slangc emits globals in
// first-use order, so this is the case where the real-slangc raylib route must still bind the
// first-DECLARED texture to texture0. The extra texture both arms bind is a mirrored,
// channel-rotated copy of the source (JobIo.ExtraFrom), so a swap is visible. Written here, not
// under tests/fixtures/shaders, so it does not join the compiled fixture corpus.
const string TwoTextureOrderBody = """
    Texture2D Base;
    SamplerState BaseSampler;
    Texture2D Mask;
    SamplerState MaskSampler;

    float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target
    {
        float4 m = Mask.Sample(MaskSampler, uv);
        float4 b = Base.Sample(BaseSampler, uv);
        return float4(b.r, lerp(b.g, m.g, 0.5), m.b, 1.0) * color;
    }
    """;
string twoTextureFx = Path.Combine(outDir, "TwoTextureOrder.fx");
File.WriteAllText(twoTextureFx, TwoTextureOrderBody + "\ntechnique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }\n");
string twoTextureSlang = Path.Combine(outDir, "TwoTextureOrder.slang");
File.WriteAllText(twoTextureSlang, TwoTextureOrderBody.Replace("float4 MainPS(", "[shader(\"fragment\")]\nfloat4 MainPS(", StringComparison.Ordinal));
cases.Add(("TwoTextureOrder", twoTextureFx));

// A tint that is neither white nor grey, so a dropped or mis-mapped COLOR0 changes the picture.
byte[] tint = [255, 200, 150, 255];

var jobs = new List<RenderJob>();
var fragments = new Dictionary<string, string>(StringComparer.Ordinal);
var setupErrors = new List<string>();

foreach ((string name, string path) in cases)
{
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
    string mgfxPath = Path.Combine(outDir, name + ".mgfx");
    await File.WriteAllBytesAsync(mgfxPath, mgfx.Value.Data);

    var converted = RaylibConverter.Convert(fx, new RaylibConvertOptions { SourceName = path });
    if (converted.IsFailure)
    {
        setupErrors.Add($"{name}: raylib conversion refused: {string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}"))}");
        continue;
    }

    RaylibShader shader = converted.Value;
    foreach (RaylibUniform u in shader.Uniforms.Where(u => !uniforms.ContainsKey(u.Name)))
        setupErrors.Add($"{name}: the converted shader declares uniform '{u.Name}' that this gate never sets, so it would ride along unexercised");
    foreach (var s in shader.Samplers.Where(s => s.BakedSamplerState.Count > 0))
        setupErrors.Add($"{name}: sampler '{s.HlslSamplerName}' bakes state this gate does not apply on the raylib arm");

    string fsPath = Path.Combine(outDir, name + ".fs");
    await File.WriteAllTextAsync(fsPath, shader.FragmentShader);
    fragments[name] = shader.FragmentShader;

    Console.WriteLine($"[route] {name}: converted; uniforms [{string.Join(", ", shader.Uniforms.Select(u => u.Name))}], " +
                      $"samplers [{string.Join(", ", shader.Samplers.Select(s => $"{s.UniformName}<-{s.HlslTextureName}"))}]");

    jobs.Add(new RenderJob(
        name, mgfxPath, fsPath, uniforms,
        shader.Samplers.Where(s => s.BoundByDrawCall)
            .Select(s => new ExtraTexture(s.HlslTextureName, s.UniformName, s.HlslSamplerName)).ToArray(),
        shader.Samplers.Where(s => !s.BoundByDrawCall)
            .Select(s => new ExtraTexture(s.HlslTextureName, s.UniformName, s.HlslSamplerName)).ToArray()));
}

// Issue #253: .slang input. The Slang twin (tests/fixtures/shaders/slang-sksl, same math in
// Slang spelling) of each case above converts through BOTH Slang routes, the built-in subset
// frontend (RaylibConverter.ConvertSlang) and real slangc (SlangCompiler.ConvertToRaylib). Each
// is rendered on the raylib arm only and compared against the .fx case's MonoGame image.
var slangArms = new List<(string Name, string Against)>();
var slangCompiler = new SlangCompiler();
foreach ((string twin, string against, string path) in new[]
{
    ("Sepia", "Sepia", Path.Combine(fixtures, "slang-sksl", "Sepia.slang")),
    ("Bloom", "Saturate", Path.Combine(fixtures, "slang-sksl", "Bloom.slang")),
    ("Scanlines", "Scanlines", Path.Combine(fixtures, "slang-sksl", "Scanlines.slang")),
    ("Dots", "Dots", Path.Combine(fixtures, "slang-sksl", "Dots.slang")),
    ("GumGrayscale", "GumGrayscale", Path.Combine(fixtures, "slang-sksl", "GumGrayscale.slang")),
    ("TwoTextureOrder", "TwoTextureOrder", twoTextureSlang),
})
{
    string slang = await File.ReadAllTextAsync(path);
    var options = new RaylibConvertOptions { SourceName = path };
    AddSlangArm($"{twin}-slang-subset", against, RaylibConverter.ConvertSlang(slang, options));
    AddSlangArm($"{twin}-slang-slangc", against, slangCompiler.ConvertToRaylib(slang, options));
}

void AddSlangArm(string name, string against, Result<RaylibShader, ShaderError[]> converted)
{
    if (converted.IsFailure)
    {
        setupErrors.Add($"{name}: raylib conversion refused: {string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}"))}");
        return;
    }
    if (jobs.FirstOrDefault(j => j.Name == against) is not { } baseJob)
    {
        setupErrors.Add($"{name}: the .fx case '{against}' it is compared against did not convert");
        return;
    }
    RaylibShader shader = converted.Value;
    foreach (RaylibUniform u in shader.Uniforms.Where(u => !uniforms.ContainsKey(u.Name)))
        setupErrors.Add($"{name}: the converted shader declares uniform '{u.Name}' that this gate never sets, so it would ride along unexercised");
    string fsPath = Path.Combine(outDir, name + ".fs");
    File.WriteAllText(fsPath, shader.FragmentShader);
    fragments[name] = shader.FragmentShader;
    Console.WriteLine($"[route] {name}: converted; samplers [{string.Join(", ", shader.Samplers.Select(s => $"{s.UniformName}<-{s.HlslTextureName}"))}]");
    jobs.Add(baseJob with
    {
        Name = name,
        MgfxPath = null,
        FragmentPath = fsPath,
        DrawTextures = shader.Samplers.Where(s => s.BoundByDrawCall)
            .Select(s => new ExtraTexture(s.HlslTextureName, s.UniformName, s.HlslSamplerName)).ToArray(),
        ExtraTextures = shader.Samplers.Where(s => !s.BoundByDrawCall)
            .Select(s => new ExtraTexture(s.HlslTextureName, s.UniformName, s.HlslSamplerName)).ToArray(),
    });
    slangArms.Add((name, against));
}

// Positive controls: raylib-arm-only renders compared against the unmutated case's MonoGame image.
var controls = new List<(string Name, string Against)>();

// The .slang arms' own control: the Sepia twin with one weight changed, through real slangc.
{
    string sepiaPath = Path.Combine(fixtures, "slang-sksl", "Sepia.slang");
    string sepia = await File.ReadAllTextAsync(sepiaPath);
    string mutated = sepia.Replace("0.59", "0.89", StringComparison.Ordinal);
    if (mutated == sepia)
    {
        setupErrors.Add("control-slang-mutated: the mutation did not change the Sepia twin, so the control proves nothing");
    }
    else
    {
        int before = slangArms.Count;
        AddSlangArm("control-slang-mutated", "Sepia",
            slangCompiler.ConvertToRaylib(mutated, new RaylibConvertOptions { SourceName = sepiaPath }));
        if (slangArms.Count > before)
        {
            slangArms.RemoveAt(slangArms.Count - 1);
            controls.Add(("control-slang-mutated", "Sepia"));
        }
    }
}
void AddMutant(string name, string against, Func<string, string> mutate)
{
    if (!fragments.TryGetValue(against, out string? original))
    {
        setupErrors.Add($"{name}: control base '{against}' did not convert");
        return;
    }
    string mutated = mutate(original);
    if (mutated == original)
    {
        setupErrors.Add($"{name}: the mutation did not change the emission, so the control proves nothing");
        return;
    }
    string fsPath = Path.Combine(outDir, name + ".fs");
    File.WriteAllText(fsPath, mutated);
    RenderJob baseJob = jobs.Single(j => j.Name == against);
    jobs.Add(baseJob with { Name = name, MgfxPath = null, FragmentPath = fsPath });
    controls.Add((name, against));
}

AddMutant("control-dropped-tint", "GumGrayscale",
    fs => ReplaceBody(fs, "fragColor", "vec4(1.0)"));
AddMutant("control-flipped-v", "Fading",
    fs => ReplaceBody(fs, "fragTexCoord", "vec2(fragTexCoord.x, 1.0 - fragTexCoord.y)"));

var curvatureNudged = new Dictionary<string, float[]>(uniforms) { ["Curvature"] = [0.13f] };
if (jobs.FirstOrDefault(j => j.Name == "CrtFilter") is { } crt)
{
    jobs.Add(crt with { Name = "control-curvature-nudge", MgfxPath = null, Uniforms = curvatureNudged });
    controls.Add(("control-curvature-nudge", "CrtFilter"));
}

if (setupErrors.Count > 0)
{
    Console.Error.WriteLine("\n[route] SETUP FAILED:");
    foreach (string e in setupErrors)
        Console.Error.WriteLine("  " + e);
    return 2;
}

string jobsPath = Path.Combine(outDir, "jobs.json");
JobIo.Write(jobsPath, new JobFile(Size, tint, jobs));

bool requireGl = Environment.GetEnvironmentVariable("SHADOWDUSK_REQUIRE_GL") == "1";
foreach (string arm in new[] { "monogame", "raylib" })
{
    int code = await RunChildAsync(arm, jobsPath);
    if (code == ArmNoGl)
    {
        if (requireGl)
        {
            Console.Error.WriteLine($"[route] FAIL: SHADOWDUSK_REQUIRE_GL=1 but the {arm} arm got no GL context");
            return 1;
        }
        Console.WriteLine($"[route] SKIPPED: the {arm} arm got no GL context");
        return 0;
    }
    if (code != 0)
    {
        Console.Error.WriteLine($"[route] FAIL: the {arm} arm exited {code}");
        return 1;
    }
}

// ---- compare ----------------------------------------------------------------
bool ok = true;
Console.WriteLine();
foreach (RenderJob job in jobs.Where(j => j.MgfxPath is not null))
{
    byte[] mono = await File.ReadAllBytesAsync(JobIo.ImagePath(outDir, "monogame", job.Name));
    byte[] ray = await File.ReadAllBytesAsync(JobIo.ImagePath(outDir, "raylib", job.Name));
    (int maxd, int over) = Compare(mono, ray, tolerance);
    int distinct = DistinctColors(mono);
    bool nontrivial = distinct > 16;
    bool match = over == 0;
    ok &= match && nontrivial;
    Console.WriteLine($"[route] {job.Name,-16} maxd {maxd,3}  px over tol {over,5}/{Size * Size}  " +
                      $"distinct colors {distinct,5}  -> {(match && nontrivial ? "MATCH" : nontrivial ? "DIVERGED" : "TRIVIAL IMAGE")}");
}
foreach ((string name, string against) in slangArms)
{
    byte[] mono = await File.ReadAllBytesAsync(JobIo.ImagePath(outDir, "monogame", against));
    byte[] ray = await File.ReadAllBytesAsync(JobIo.ImagePath(outDir, "raylib", name));
    (int maxd, int over) = Compare(mono, ray, tolerance);
    bool match = over == 0;
    ok &= match;
    Console.WriteLine($"[route] {name,-24} maxd {maxd,3}  px over tol {over,5}/{Size * Size}  vs {against}.fx on MonoGame  -> " +
                      (match ? "MATCH" : "DIVERGED"));
}
foreach ((string name, string against) in controls)
{
    byte[] mono = await File.ReadAllBytesAsync(JobIo.ImagePath(outDir, "monogame", against));
    byte[] ray = await File.ReadAllBytesAsync(JobIo.ImagePath(outDir, "raylib", name));
    (int maxd, int over) = Compare(mono, ray, tolerance);
    bool caught = over > 0;
    ok &= caught;
    Console.WriteLine($"[route] {name,-24} maxd {maxd,3}  px over tol {over,5}  -> " +
                      (caught ? "CAUGHT (control diverges, as it must)" : "MISSED: the comparison cannot see this bug class"));
}

Console.WriteLine($"\n[route] {(ok ? "PASS" : "FAIL")}: raylib route, rendered-image fidelity vs the proven OpenGL backend.");
return ok ? 0 : 1;

// -----------------------------------------------------------------------------

static string ReplaceBody(string fs, string word, string replacement)
{
    // Leave the `in` declarations alone so the mutant still links against raylib's vertex shader.
    int main = fs.IndexOf("void main()", StringComparison.Ordinal);
    return fs[..main] + System.Text.RegularExpressions.Regex.Replace(fs[main..], $@"\b{word}\b", replacement);
}

static (int MaxDelta, int OverTolerance) Compare(byte[] a, byte[] b, int tolerance)
{
    int maxd = 0, over = 0;
    for (int i = 0; i < a.Length; i += 4)
    {
        int d = 0;
        for (int c = 0; c < 4; c++)
            d = Math.Max(d, Math.Abs(a[i + c] - b[i + c]));
        maxd = Math.Max(maxd, d);
        if (d > tolerance)
            over++;
    }
    return (maxd, over);
}

static int DistinctColors(byte[] rgba)
{
    var seen = new HashSet<int>();
    for (int i = 0; i < rgba.Length; i += 4)
        seen.Add(BitConverter.ToInt32(rgba, i));
    return seen.Count;
}

static async Task<int> RunChildAsync(string arm, string jobsPath)
{
    string self = Assembly.GetEntryAssembly()!.Location;
    string host = Environment.ProcessPath!;
    var psi = new ProcessStartInfo
    {
        FileName = host,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = Path.GetDirectoryName(self)!,
    };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        psi.ArgumentList.Add(self);
    psi.ArgumentList.Add("--arm");
    psi.ArgumentList.Add(arm);
    psi.ArgumentList.Add(jobsPath);

    using var process = Process.Start(psi)!;
    process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"  [{arm}] {e.Data}"); };
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"  [{arm}] {e.Data}"); };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    await process.WaitForExitAsync(timeout.Token);
    return process.ExitCode;
}

static int RunArm(string arm, string jobsPath)
{
    JobFile jobs = JobIo.Read(jobsPath);
    string outDir = Path.GetDirectoryName(jobsPath)!;
    var failures = new List<string>();
    int code;
    if (arm == "monogame")
    {
        using var game = new MonoGameArm(ShaderInputs.CatPath(ShaderInputs.FindRepoRoot()), outDir, jobs);
        game.Run();
        if (game.Skipped)
        {
            Console.Error.WriteLine($"NO GL CONTEXT: {game.SkipReason}");
            return ArmNoGl;
        }
        failures.AddRange(game.Failures);
        code = 0;
    }
    else
    {
        code = RaylibArm.Run(outDir, jobs, failures);
    }
    foreach (string f in failures)
        Console.Error.WriteLine("FAIL: " + f);
    return code != 0 ? code : failures.Count > 0 ? 1 : 0;
}
