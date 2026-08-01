// =============================================================================
// ShaderToyRouteDx — Phase 51 A5/A10 rung-4 render validation for the ShaderToy /
// `.glsl` FRONTEND ROUTE, in REAL MonoGame WindowsDX (DirectX 11).
// -----------------------------------------------------------------------------
// The DirectX arm of `validation/ShaderToyRouteGl`. Same claim, same pinning, same
// two arms — only the runtime and the golden change:
//
//     GradientToy.glsl --[ShaderToyConverter]--> GradientToy.fx
//         |                                          |
//         | ShadowDusk EffectCompiler(DirectX)       | real mgfxc /Profile:DirectX_11
//         v                                          v
//     candidate .mgfx                            golden .mgfx  (committed)
//         \________ rendered in real MonoGame WindowsDX (DX11) ________/
//                          pixel-diff, both arms
//
// ==================== WHY THIS ARM DID NOT EXIST BEFORE =====================
// It could not. The converter used to emit `vs_3_0`/`ps_3_0` in BOTH arms of its
// `#if OPENGL … #else … #endif` header, and MonoGame's DirectX_11 shader profile
// REFUSES anything below SM 4.0 level 9.1:
//
//     mgfxc /Profile:DirectX_11 GradientToy.fx
//       Invalid profile 'vs_3_0'. Vertex shader 'VSMain' must be SM 4.0 level 9.1
//       or higher!
//
// So there was no DirectX golden to diff against, for a real reason — the route's
// own output was not buildable by the reference compiler on this target at all.
// Phase 51 A10 fixed the emission (the DirectX arm now names the *_4_0_level_9_1
// pair), which is what makes this gate possible; the same change taught ShadowDusk
// to reject sub-floor profiles itself (SD0015) so the two compilers agree on the
// reject side too.
//
// ==================== WHY THE FIXTURE IS PINNED (unchanged) =================
// The golden is mgfxc's build of one specific `.fx`. If the converter's output
// drifted and nothing checked, the golden would silently stop corresponding to what
// the route emits and the diff would compare two different shaders. So this driver
// converts the `.glsl` IN PROCESS and asserts the result matches the committed
// `tests/fixtures/shaders/shadertoy/GradientToy.fx` before it renders anything.
//
// ============================ WHAT IS PROVEN HERE ============================
//   A. ABSOLUTE — the render is a real, two-axis gradient (the corners differ from
//      each other in the channels the shader drives). A flat, black, or single-axis
//      frame fails, so "it rendered nothing" cannot pass as agreement.
//   B. vs mgfxc — ShadowDusk's build and mgfxc's build of the SAME converted `.fx`,
//      rendered in the same scene, pixel-diffed.
//
// ===================== HONEST LIMITATIONS (NOT hidden) ======================
//   * One shader, one target (DirectX 11). It pins the route at rung 4 on DX; it is
//     not a sweep of the ShaderToy corpus (that is the fidelity gate's job).
//   * GradientToy is deliberately time-INDEPENDENT (no iTime), so the comparison is
//     deterministic. A time-driven shader would need a pinned clock on both arms.
//   * Windows + a DX11 GPU only. There is no headless D3D driver CI can run, which
//     is why this lives in run-windows-render-gates.ps1 and not in a workflow.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.ShaderToy;
using ShadowDusk.Validation;

int tolerance = 4;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string repoRoot   = ShaderInputs.FindRepoRoot();
string glslPath   = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "shadertoy", "GradientToy.glsl");
string fxPath     = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "shadertoy", "GradientToy.fx");
string goldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_11", "GradientToy.mgfx");
string outDir     = Path.Combine(repoRoot, "validation", "output-shadertoy-dx");

Directory.CreateDirectory(outDir);

Console.WriteLine("=== Phase 51 A5/A10 ShaderToy `.glsl`-route rung-4 render validation (real MonoGame WindowsDX) ===");
Console.WriteLine($"[toydx] out: {outDir}  tolerance: {tolerance}\n");

// ---- 1. Run the ACTUAL frontend the route uses ------------------------------
string glsl = await File.ReadAllTextAsync(glslPath);
ConvertResult conversion = ShaderToyConverter.Convert(glsl, new ConvertOptions
{
    EffectName    = "GradientToy",
    TechniqueName = "ShaderToy",
});

if (!conversion.Success || conversion.Fx is null)
{
    Console.Error.WriteLine("[toydx] conversion FAILED:");
    foreach (ConvertDiagnostic d in conversion.Diagnostics)
        Console.Error.WriteLine($"        {d.Severity} {d.Line}:{d.Column} {d.Message}");
    return 2;
}
Console.WriteLine($"[toydx] converted {Path.GetFileName(glslPath)} -> .fx ({conversion.Fx.Length} chars); " +
                  $"uniforms used = [{string.Join(", ", conversion.UsedUniforms)}]");

// Always write what the converter produced, so regenerating the pinned fixture after an
// intentional converter change is a copy rather than a puzzle.
string emittedPath = Path.Combine(outDir, "GradientToy.converted.fx");
await File.WriteAllTextAsync(emittedPath, conversion.Fx);

// ---- 2. Pin it against the committed .fx the golden was built from ----------
if (!File.Exists(fxPath))
{
    Console.Error.WriteLine(
        $"[toydx] FAIL — the pinned fixture is missing: {fxPath}\n" +
        $"        The converter's current output was written to {emittedPath}.\n" +
        $"        Commit it as the fixture and regenerate the mgfxc golden from it.");
    return 2;
}
string pinnedFx = await File.ReadAllTextAsync(fxPath);
if (!string.Equals(Normalize(pinnedFx), Normalize(conversion.Fx), StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        $"[toydx] FAIL — the converter no longer emits the pinned fixture.\n" +
        $"        pinned:  {fxPath}\n" +
        $"        emitted: {emittedPath}\n" +
        $"        The committed mgfxc golden was built from the PINNED .fx, so rendering the\n" +
        $"        emitted one would diff two different shaders. If the change is intended,\n" +
        $"        copy the emitted file over the fixture and regenerate BOTH goldens:\n" +
        $"          tools/compile-fixtures.ps1 -Profiles DirectX_11,OpenGL");
    return 1;
}
Console.WriteLine("[toydx] converter output matches the pinned fixture — the golden still corresponds to the route");

// ---- 3. Compile the converted .fx through the real product pipeline ---------
byte[] candidate;
try
{
    var result = await new EffectCompiler().CompileAsync(conversion.Fx, new CompilerOptions
    {
        Target          = PlatformTarget.DirectX,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName  = fxPath,
    });
    if (result.IsFailure)
        throw new Exception("compile GradientToy.fx (DirectX_11) failed: " +
            string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
    candidate = result.Value.Data;
}
catch (Exception ex)
{
    Console.Error.WriteLine("[toydx] " + ex.Message);
    return 2;
}
Console.WriteLine($"[toydx] compiled OK: candidate {candidate.Length} B");

// The golden is REQUIRED here, unlike the GL arm's optional-golden shape: the whole
// point of this driver is the mgfxc comparison A10 unblocked. A missing golden means
// the regeneration step was skipped, and reporting an absolute-only pass would hide it.
if (!File.Exists(goldenPath))
{
    Console.Error.WriteLine(
        $"[toydx] FAIL — no mgfxc golden at {goldenPath}.\n" +
        $"        Generate it with:  tools/compile-fixtures.ps1 -Profiles DirectX_11,OpenGL");
    return 2;
}
byte[] golden = await File.ReadAllBytesAsync(goldenPath);
Console.WriteLine($"[toydx] mgfxc golden: {golden.Length} B — arm B pixel-diffs vs the reference compiler");

using var game = new ShaderToyRouteDxGame(candidate, golden, outDir, tolerance);
game.Run();

if (game.Skipped)
{
    Console.Error.WriteLine($"\n[toydx] FAIL — the DX device could not be created: {game.SkipReason}");
    return 1;
}

Console.WriteLine();
foreach (string line in game.Report)
    Console.WriteLine(line);

Console.WriteLine($"\n[toydx] {(game.Passed ? "PASS" : "FAIL")} — rung-4 ShaderToy `.glsl`-route DirectX validation.");
return game.Passed ? 0 : 1;

// Line-ending-insensitive compare: the fixture is committed through git's autocrlf and the
// converter emits whatever it emits. Nothing here depends on line endings, and a spurious
// CRLF failure would train people to ignore this gate.
static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

// -----------------------------------------------------------------------------

sealed class ShaderToyRouteDxGame : Game
{
    private const int Size = 64;

    private readonly GraphicsDeviceManager _gdm;
    private readonly byte[] _candidate;
    private readonly byte[] _golden;
    private readonly string _outDir;
    private readonly int _tolerance;
    private bool _done;

    public bool Passed { get; private set; }
    public bool Skipped { get; private set; }
    public string? SkipReason { get; private set; }
    public List<string> Report { get; } = new();

    public ShaderToyRouteDxGame(byte[] candidate, byte[] golden, string outDir, int tolerance)
    {
        _candidate = candidate;
        _golden = golden;
        _outDir = outDir;
        _tolerance = tolerance;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = Size,
            PreferredBackBufferHeight = Size,
            GraphicsProfile           = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk ShaderToy route validation (WindowsDX)";
    }

    protected override void Initialize()
    {
        try { base.Initialize(); }
        catch (Exception ex)
        {
            Skipped = true;
            SkipReason = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done || Skipped) { Exit(); return; }
        _done = true;

        try { Passed = Validate(); }
        catch (Exception ex)
        {
            Report.Add($"[toydx] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Passed = false;
        }
        Exit();
    }

    private bool Validate()
    {
        GraphicsDevice gd = GraphicsDevice;

        Effect candidate;
        try { candidate = new Effect(gd, _candidate); }
        catch (Exception ex)
        {
            Report.Add($"[toydx] candidate new Effect() FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        Report.Add("[toydx] candidate new Effect(gd, mgfx) loaded OK in real WindowsDX; params = [" +
                   string.Join(", ", candidate.Parameters.Select(p => p.Name)) + "]");

        Color[] img = Render(gd, candidate);
        SavePng(gd, img, "candidate.png");

        bool ok = AssertGradient("A candidate", img);

        Effect golden;
        try { golden = new Effect(gd, _golden); }
        catch (Exception ex)
        {
            Report.Add($"[toydx] GOLDEN new Effect() threw (control failure): {ex.GetType().Name}: {ex.Message}");
            candidate.Dispose();
            return false;
        }
        Report.Add("[toydx] golden params = [" +
                   string.Join(", ", golden.Parameters.Select(p => p.Name)) + "]");

        Color[] goldImg = Render(gd, golden);
        SavePng(gd, goldImg, "golden.png");

        // The golden gets the same absolute check, so a diff failure is attributed to
        // the right side instead of blaming the candidate by default.
        bool goldenAbsolute = AssertGradient("B golden ", goldImg);
        if (!goldenAbsolute)
            Report.Add("[toydx] NOTE: the mgfxc golden itself does not render the expected shape — " +
                       "read the rows above before attributing the diff below.");

        (int maxDelta, int diffCount) = Compare(img, goldImg);
        bool match = diffCount == 0;
        Report.Add($"[toydx] vs mgfxc golden: maxd {maxDelta}, {diffCount} px over tolerance " +
                   $"{_tolerance} -> {OkWrong(match)}");
        ok &= match && goldenAbsolute;

        golden.Dispose();
        candidate.Dispose();
        return ok;
    }

    /// <summary>
    /// The shader is `fragColor = vec4(fragCoord / iResolution.xy, 0, 1)`, so the frame must
    /// vary along BOTH axes, in two different channels. Asserting the shape rather than exact
    /// colours keeps this independent of the converter's Y-origin convention (ShaderToy's
    /// origin is bottom-left) — that is the fidelity gate's claim, not this one's — while
    /// still refusing a flat, black, or one-axis frame.
    /// </summary>
    private bool AssertGradient(string tag, Color[] img)
    {
        Color tl = img[Px(2, 2)], tr = img[Px(Size - 3, 2)];
        Color bl = img[Px(2, Size - 3)], br = img[Px(Size - 3, Size - 3)];

        int horizontal = Math.Abs(tr.R - tl.R);          // R tracks x
        int vertical   = Math.Abs(bl.G - tl.G);          // G tracks y
        bool blueZero  = tl.B < 8 && br.B < 8;           // the shader writes 0 to blue
        bool opaque    = tl.A > 247 && br.A > 247;       // ... and 1 to alpha

        bool ok = horizontal > 128 && vertical > 128 && blueZero && opaque;

        Report.Add($"[{tag}] corners TL{Fmt(tl)} TR{Fmt(tr)} BL{Fmt(bl)} BR{Fmt(br)}");
        Report.Add($"[{tag}] R varies across x by {horizontal} (want >128) -> {OkWrong(horizontal > 128)}; " +
                   $"G varies across y by {vertical} (want >128) -> {OkWrong(vertical > 128)}; " +
                   $"B==0 -> {OkWrong(blueZero)}; A==255 -> {OkWrong(opaque)}");
        if (!ok)
            Report.Add($"[{tag}] -> WRONG: this is not the two-axis gradient the shader describes " +
                       "(a flat, black, or single-axis frame cannot pass as agreement)");
        return ok;
    }

    /// <summary>
    /// The converted effect is VS-DRIVEN and its generated header states the host contract
    /// explicitly: "the host draws a quad/triangle whose POSITION is already in NDC ([-1,1]);
    /// we pass it straight through". SpriteBatch cannot be used here — it feeds screen-space
    /// positions expecting the effect to apply its own transform, and this VS applies none, so
    /// the quad would land far outside the frustum and render nothing. So this drives a real
    /// NDC fullscreen quad, which is also exactly what a consumer of the ShaderToy route does.
    /// </summary>
    private Color[] Render(GraphicsDevice gd, Effect effect)
    {
        // iResolution is what the shader divides by; without it the gradient is undefined.
        effect.Parameters["iResolution"]?.SetValue(new Vector3(Size, Size, 1f));

        var quad = new[]
        {
            new NdcVertex(-1f, -1f), new NdcVertex(-1f, 1f), new NdcVertex(1f, -1f),
            new NdcVertex( 1f, -1f), new NdcVertex(-1f, 1f), new NdcVertex(1f,  1f),
        };

        using var rt = new RenderTarget2D(gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(rt);
        gd.Clear(Color.Black);
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.None;
        gd.RasterizerState = RasterizerState.CullNone;   // the quad's winding is not the subject

        foreach (EffectPass pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserPrimitives(PrimitiveType.TriangleList, quad, 0, 2);
        }

        gd.SetRenderTarget(null);
        var px = new Color[Size * Size];
        rt.GetData(px);
        return px;
    }

    private (int MaxDelta, int DiffCount) Compare(Color[] a, Color[] b)
    {
        int maxDelta = 0, diffCount = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Max(Math.Max(Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].G - b[i].G)),
                             Math.Max(Math.Abs(a[i].B - b[i].B), Math.Abs(a[i].A - b[i].A)));
            if (d > maxDelta) maxDelta = d;
            if (d > _tolerance) diffCount++;
        }
        return (maxDelta, diffCount);
    }

    private void SavePng(GraphicsDevice gd, Color[] img, string name)
    {
        using var rt = new RenderTarget2D(gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
        rt.SetData(img);
        using var fs = File.Create(Path.Combine(_outDir, name));
        rt.SaveAsPng(fs, Size, Size);
    }

    private static int Px(int x, int y) => y * Size + x;
    private static string Fmt(Color c) => $"({c.R},{c.G},{c.B},{c.A})";
    private static string OkWrong(bool ok) => ok ? "OK" : "WRONG";
}

/// <summary>
/// The converted effect's only vertex input is <c>float4 Position : POSITION</c>, so the
/// vertex declaration is exactly that and nothing else — feeding it a fatter layout would
/// test the harness rather than the shader.
/// </summary>
readonly struct NdcVertex : IVertexType
{
    private readonly Vector4 _position;

    public NdcVertex(float x, float y) => _position = new Vector4(x, y, 0f, 1f);

    public static readonly VertexDeclaration Declaration = new(
        new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.Position, 0));

    VertexDeclaration IVertexType.VertexDeclaration => Declaration;
}
