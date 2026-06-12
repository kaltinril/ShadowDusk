// Phase 43C (F4/F5/F6) rung-4 validation: the GL cbuffer/array-model corpus must
// LOAD in a REAL MonoGame 3.8.2 DesktopGL Effect and render pixel-equivalent to
// the same .fx compiled by the real mgfxc 3.8.2.1105 (the committed golden),
// with the parameters SET BY NAME — including ARRAY parameters set beyond
// element 0 (`Parameters["Colors"].SetValue(Vector4[])`, `.Elements[2]`), the
// exact surface the pre-43C Elements gap made impossible, and a shared-cbuffer
// VS transform, which pre-43C silently read zero (rendered black).
//
//   dotnet run -c Release --project validation/CbufferModel
//
// Every row renders BOTH arms through an identical path and pixel-compares them
// in memory; rows additionally assert the candidate image is NOT near-black, so
// a both-arms-broken-identically outcome cannot pass vacuously (pre-43C the
// SharedCbuffer candidate rendered black — that exact failure must stay caught).
//
// Exit 0 iff every row passes (default tolerance 4, the Phase 18 bar).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

int tolerance = 4;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string repoRoot  = ShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL");
string catPath   = ShaderInputs.CatPath(repoRoot);
string outDir    = Path.Combine(repoRoot, "validation", "output-cbuffer");

// (parameter-setting lives in RowParams below — top-level local functions are not
// reachable from class members.)

// (Name, VsDriven). VS rows draw a custom quad (SpriteBatch supplies its own VS);
// PS rows go through the identical SpriteBatch path both arms.
var rows = new (string Name, bool VsDriven)[]
{
    ("SharedCbuffer",  true),
    ("MultiCbuffer",   false),
    ("MultiCbufferVs", true),
    ("ArrayUniform",   false),
    ("ArrayUniformVs", true),
};

Console.WriteLine($"[cbuffer] cat: {catPath}");
Console.WriteLine($"[cbuffer] out: {outDir}  tolerance: {tolerance}\n");

// ---- Compile every candidate with ShadowDusk (OpenGL, in memory). ----
var compiler = new EffectCompiler();
var jobs = new List<CbufferRow>();
foreach (var (name, vsDriven) in rows)
{
    string fxPath = Path.Combine(shaderDir, name + ".fx");
    string src = await File.ReadAllTextAsync(fxPath);
    var result = await compiler.CompileAsync(src, new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fxPath,
    });

    byte[]? candidate = result.IsSuccess ? result.Value.Data : null;
    string? compileError = result.IsFailure
        ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))
        : null;

    string goldenPath = Path.Combine(goldenDir, name + ".mgfx");
    byte[]? golden = File.Exists(goldenPath) ? await File.ReadAllBytesAsync(goldenPath) : null;
    if (golden is null)
        compileError = (compileError is null ? "" : compileError + " | ") +
                       $"golden missing: {goldenPath}";

    jobs.Add(new CbufferRow(name, vsDriven, candidate, golden, compileError));
}

foreach (var j in jobs)
    Console.WriteLine($"  [{(j.CandidateBytes is null ? "FAIL" : "OK  ")}] compile {j.Name,-16} " +
                      $"{(j.CompileError ?? $"{j.CandidateBytes!.Length} bytes")}");
Console.WriteLine();

using var game = new CbufferModelGame(catPath, outDir, jobs, tolerance);
game.Run();

int ok = 0;
Console.WriteLine("\n[cbuffer] results:");
foreach (var o in game.Outcomes)
{
    if (o.Pass) ok++;
    Console.WriteLine($"  [{(o.Pass ? "PASS" : "FAIL")}] {o.Name,-16} {o.Detail}");
}
Console.WriteLine($"\n[cbuffer] {ok}/{game.Outcomes.Count} rows passed.");
return ok == game.Outcomes.Count ? 0 : 1;

/// <summary>
/// The shared parameter values — set BY NAME on BOTH arms (identical calls), so any
/// pixel difference is attributable solely to the .mgfx bytes.
/// </summary>
internal static class RowParams
{
    public static void Set(Effect e, Texture2D cat)
    {
    e.Parameters["SpriteTexture"]?.SetValue(cat);

    // SharedCbuffer (F4): identity transform + a strong tint. Pre-43C the VS read
    // an unbindable vs_uniforms_vec4 — WorldViewProjection was zero and the quad
    // collapsed to the origin (black frame).
    e.Parameters["WorldViewProjection"]?.SetValue(Matrix.Identity);
    e.Parameters["DiffuseColor"]?.SetValue(new Vector4(1f, 0.65f, 0.4f, 1f));

    // MultiCbuffer (F5): members of BOTH cbuffers, all non-default.
    e.Parameters["TintA"]?.SetValue(new Vector4(1f, 0.2f, 0.2f, 1f));
    e.Parameters["TintB"]?.SetValue(new Vector4(0.2f, 0.4f, 1f, 1f));
    e.Parameters["MixAmount"]?.SetValue(0.65f);

    // MultiCbufferVs (F5, VS): both cbuffers feed the vertex stage.
    e.Parameters["PositionOffset"]?.SetValue(new Vector4(0.1f, -0.05f, 0f, 0f));
    e.Parameters["ColorScale"]?.SetValue(new Vector4(0.9f, 1f, 0.8f, 1f));

    // ArrayUniform (F6): the WHOLE array set from managed code — elements 1..3
    // carry the signal. Also overwrite element 2 INDIVIDUALLY via .Elements[2],
    // the recursive sub-parameter surface MonoGame builds from the element
    // records (impossible pre-43C: Elements count was 0 on every target).
    if (e.Parameters["Colors"] is { } colors)
    {
        colors.SetValue(new[]
        {
            new Vector4(1f, 0f, 0f, 1f),
            new Vector4(0f, 1f, 0f, 1f),
            new Vector4(0f, 0f, 0f, 1f),   // overwritten below via Elements[2]
            new Vector4(1f, 1f, 1f, 1f),
        });
        colors.Elements[2].SetValue(new Vector4(0.2f, 0.2f, 1f, 1f));
    }
    e.Parameters["Weights"]?.SetValue(new[] { 1f, 0.8f, 0.9f, 0.6f });

    // ArrayUniformVs (F6, VS): the shader BLENDS both elements of both arrays
    // (p0*0.35 + p1*0.65) with DISTINCT per-element values, so the image is
    // right only when every element lands at its exact register offset — a
    // swapped/strided/element-0-only upload cannot render like the golden.
    if (e.Parameters["Bones"] is { } bones)
    {
        bones.SetValue(new[]
        {
            Matrix.CreateScale(0.7f, 0.85f, 1f),
            Matrix.CreateScale(0.95f, 1f, 1f),
        });
    }
    if (e.Parameters["PosOffsets"] is { } posOffsets)
    {
        posOffsets.SetValue(new[]
        {
            new Vector4(0.02f, -0.06f, 0f, 0f),
            new Vector4(0.05f, 0.1f, 0f, 0f),
        });
    }
    }
}

internal sealed record CbufferRow(
    string Name, bool VsDriven,
    byte[]? CandidateBytes, byte[]? GoldenBytes, string? CompileError);

internal sealed record CbufferOutcome(string Name, bool Pass, string Detail);

/// <summary>
/// One real MonoGame 3.8.2 DesktopGL device: loads each row's candidate (ShadowDusk)
/// and golden (mgfxc) bytes into real <see cref="Effect"/>s, sets the row's
/// parameters BY NAME on both (including array elements beyond 0), renders both arms
/// through an identical path (SpriteBatch for PS rows, a custom vertex-buffer quad
/// for VS rows), and pixel-compares the arms in memory.
/// </summary>
internal sealed class CbufferModelGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly IReadOnlyList<CbufferRow> _rows;
    private readonly int _tolerance;
    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<CbufferOutcome> Outcomes { get; } = new();

    public CbufferModelGame(string catPath, string outDir, IReadOnlyList<CbufferRow> rows, int tolerance)
    {
        _catPath = catPath;
        _outDir = outDir;
        _rows = rows;
        _tolerance = tolerance;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk Phase 43C cbuffer-model validation (headless)";
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        using var fs = File.OpenRead(_catPath);
        _cat = Texture2D.FromStream(GraphicsDevice, fs);
        Directory.CreateDirectory(_outDir);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }
        GraphicsDevice.Clear(Color.Black);

        foreach (CbufferRow row in _rows)
            Outcomes.Add(RunRow(row));

        _done = true;
        Exit();
    }

    private CbufferOutcome RunRow(CbufferRow row)
    {
        if (row.CandidateBytes is null || row.GoldenBytes is null)
            return new CbufferOutcome(row.Name, false, $"compile/golden failure: {row.CompileError}");

        Effect candidate;
        try { candidate = new Effect(GraphicsDevice, row.CandidateBytes); }
        catch (Exception ex)
        {
            return new CbufferOutcome(row.Name, false, $"candidate new Effect() threw: {ex.Message}");
        }

        Effect golden;
        try { golden = new Effect(GraphicsDevice, row.GoldenBytes); }
        catch (Exception ex)
        {
            return new CbufferOutcome(row.Name, false, $"GOLDEN new Effect() threw (control failure): {ex.Message}");
        }

        Color[]? candPixels = RenderArm(candidate, row, row.Name + ".candidate", out string? candErr);
        if (candPixels is null)
            return new CbufferOutcome(row.Name, false, $"candidate render failed: {candErr}");
        Color[]? goldPixels = RenderArm(golden, row, row.Name + ".golden", out string? goldErr);
        if (goldPixels is null)
            return new CbufferOutcome(row.Name, false, $"golden render failed: {goldErr}");

        int maxDelta = 0, diffCount = 0;
        long brightness = 0;
        for (int i = 0; i < candPixels.Length; i++)
        {
            int d = Math.Max(
                Math.Max(Math.Abs(candPixels[i].R - goldPixels[i].R),
                         Math.Abs(candPixels[i].G - goldPixels[i].G)),
                Math.Max(Math.Abs(candPixels[i].B - goldPixels[i].B),
                         Math.Abs(candPixels[i].A - goldPixels[i].A)));
            if (d > 0) diffCount++;
            if (d > maxDelta) maxDelta = d;
            brightness += candPixels[i].R + candPixels[i].G + candPixels[i].B;
        }

        // Vacuity guard: the pre-43C SharedCbuffer failure mode is a BLACK frame
        // (VS transform read zero). Both arms agreeing on black must not pass.
        double meanChannel = brightness / (double)(candPixels.Length * 3);
        if (meanChannel < 4.0)
            return new CbufferOutcome(row.Name, false,
                $"candidate rendered (near-)black (mean channel {meanChannel:F2}) — the " +
                "uniform upload is not reaching the shader");

        bool pass = maxDelta <= _tolerance;
        return new CbufferOutcome(row.Name, pass,
            $"rendered both arms; diffPixels={diffCount} maxDelta={maxDelta} " +
            $"(tolerance {_tolerance}); candidate mean channel {meanChannel:F1}");
    }

    // Vertex with POSITION / COLOR0 / TEXCOORD0 — the SpriteBatch-compatible set the
    // VS fixtures consume (matches the .mgfx attribute table vs_v0/vs_v1/vs_v2).
    private readonly struct VsVertex : IVertexType
    {
        public readonly Vector3 Position;
        public readonly Color Color;
        public readonly Vector2 TexCoord;

        public VsVertex(Vector3 position, Color color, Vector2 texCoord)
        {
            Position = position; Color = color; TexCoord = texCoord;
        }

        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Color, VertexElementUsage.Color, 0),
            new VertexElement(16, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    private Color[]? RenderArm(Effect effect, CbufferRow row, string pngStem, out string? error)
    {
        error = null;
        int w = _cat.Width, h = _cat.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        try
        {
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Black);

            RowParams.Set(effect, _cat);

            if (row.VsDriven)
            {
                GraphicsDevice.BlendState = BlendState.Opaque;
                GraphicsDevice.DepthStencilState = DepthStencilState.None;
                GraphicsDevice.RasterizerState = RasterizerState.CullNone;
                GraphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;

                // A near-full-screen clip-space quad (slightly inset so the
                // PositionOffset rows keep the geometry on screen).
                var verts = new[]
                {
                    new VsVertex(new Vector3(-0.85f,  0.85f, 0f), Color.White, new Vector2(0f, 0f)),
                    new VsVertex(new Vector3( 0.85f,  0.85f, 0f), Color.White, new Vector2(1f, 0f)),
                    new VsVertex(new Vector3(-0.85f, -0.85f, 0f), Color.White, new Vector2(0f, 1f)),
                    new VsVertex(new Vector3( 0.85f, -0.85f, 0f), Color.White, new Vector2(1f, 1f)),
                };
                var indices = new short[] { 0, 1, 2, 2, 1, 3 };

                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    GraphicsDevice.DrawUserIndexedPrimitives(
                        PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, 2,
                        VsVertex.Declaration);
                }
            }
            else
            {
                // PS-only rows: the identical SpriteBatch path both arms.
                _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque,
                    SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
                _sb.Draw(_cat, new Rectangle(0, 0, w, h), Color.White);
                _sb.End();
            }

            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[w * h];
            rt.GetData(pixels);

            string png = Path.Combine(_outDir, pngStem + ".png");
            using (var outFs = File.Create(png))
                rt.SaveAsPng(outFs, w, h);

            return pixels;
        }
        catch (Exception ex)
        {
            try { _sb.End(); } catch { /* may not be in a batch */ }
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            effect.Dispose();
        }
    }
}
