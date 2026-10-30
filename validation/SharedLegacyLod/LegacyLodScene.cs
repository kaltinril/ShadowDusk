#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Validation.LegacyLod;

/// <summary>Which picture a job draws.</summary>
public enum LegacyLodDraw
{
    /// <summary><c>Tex2DLodRender.fx</c>'s four-column quad over the mip-chain texture.</summary>
    LodQuad,
    /// <summary>MonoGame's own <c>VertexTextureEffect.fx</c>: a height-mapped grid (vertex texture fetch).</summary>
    MonoGameVtf,
    /// <summary>The same grid with a FLAT height map: must differ from <see cref="MonoGameVtf"/>,
    /// or the fetch proves nothing.</summary>
    MonoGameVtfFlat,
}

/// <summary>One effect to draw through the scene.</summary>
public sealed record LegacyLodJob(string Name, byte[] Mgfx, LegacyLodDraw Draw = LegacyLodDraw.LodQuad);

/// <summary>
/// The render scene for <c>tests/fixtures/golden/legacy-lod-intrinsics/Tex2DLodRender.fx</c>
/// (tex2Dlod in the vertex AND pixel stage, tex2Dbias, tex2Dproj), shared by the DirectX 11,
/// DirectX 12 and Vulkan drivers so the three targets draw the identical scene. Source-linked
/// into each driver; it uses only the MonoGame API common to 3.8.x WindowsDX and the 3.8.5
/// native runtimes.
///
/// <para>The effect parameter <c>Tex</c> is bound to a <see cref="TextureSize"/>-square texture
/// with a full mip chain whose levels differ: texel (x, y) of level L is
/// (28 * L, 255 * x / (n - 1), 255 * y / (n - 1)) for a level n texels wide. Both sampler stages
/// are set to <see cref="SamplerState.PointWrap"/> (the legacy fixture bakes the same state), so
/// every pixel is one exact texel of one exact level. A single quad covers the
/// <see cref="Size"/>-square target, making the implicit LOD exactly 2, and the fixture splits
/// it into four <see cref="Columns"/> (VS tex2Dlod, PS tex2Dlod, tex2Dbias, tex2Dproj).</para>
///
/// <para>Every job draws on ONE device in ONE <see cref="Game"/> (DesktopVK does not survive a
/// second device in a process), so any pixel difference comes from the effect bytes alone.</para>
/// </summary>
public sealed class LegacyLodScene : Game
{
    public const int Size = 128;
    public const int TextureSize = 512;
    public const int Columns = 4;
    public const int GridSize = 16;

    // VertexTextureEffect.fx's vertex input: one float2 POSITION.
    private readonly struct GridVertex : IVertexType
    {
        public readonly Vector2 Position;
        public GridVertex(Vector2 position) => Position = position;
        public static readonly VertexDeclaration Declaration =
            new(new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0));
        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    private static readonly short[] QuadIndices = { 0, 1, 2, 2, 1, 3 };

    private readonly GraphicsDeviceManager _gdm;
    private readonly IReadOnlyList<LegacyLodJob> _jobs;
    private readonly string? _outDir;
    private bool _done;

    /// <summary>The rendered pixels of each job that loaded and drew, row-major, Size x Size.</summary>
    public Dictionary<string, Color[]> Captures { get; } = new(StringComparer.Ordinal);

    /// <summary>Why a job did not render (load failure, missing parameter, a throw while drawing).</summary>
    public Dictionary<string, string> Errors { get; } = new(StringComparer.Ordinal);

    public LegacyLodScene(IReadOnlyList<LegacyLodJob> jobs, string? outDir)
    {
        _jobs = jobs;
        _outDir = outDir;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Size,
            PreferredBackBufferHeight = Size,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk legacy LOD intrinsics (headless)";
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }
        GraphicsDevice gd = GraphicsDevice;
        // SHADOWDUSK_LOD_TRACE=1 prints each step to stderr (flushed), so a native fault inside
        // the runtime names the job and the call it died in (how the Vulkan pairing gap was found).
        bool trace = Environment.GetEnvironmentVariable("SHADOWDUSK_LOD_TRACE") == "1";
        void Trace(string step) { if (trace) { Console.Error.WriteLine($"  [legacy-lod trace] {step}"); Console.Error.Flush(); } }

        Trace("texture");
        using var texture = new Texture2D(gd, TextureSize, TextureSize, true, SurfaceFormat.Color);
        for (int level = 0, n = TextureSize; level < texture.LevelCount; level++, n = Math.Max(1, n / 2))
        {
            var texels = new Color[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                texels[y * n + x] = new Color(
                    (byte)(28 * level),
                    (byte)(n > 1 ? 255 * x / (n - 1) : 0),
                    (byte)(n > 1 ? 255 * y / (n - 1) : 0),
                    (byte)255);
            }
            texture.SetData(level, null, texels, 0, texels.Length);
        }

        // One quad over the whole target, uv (0,0) top-left.
        var quad = new[]
        {
            new VertexPositionTexture(new Vector3(-1,  1, 0), new Vector2(0, 0)),
            new VertexPositionTexture(new Vector3( 1,  1, 0), new Vector2(1, 0)),
            new VertexPositionTexture(new Vector3(-1, -1, 0), new Vector2(0, 1)),
            new VertexPositionTexture(new Vector3( 1, -1, 0), new Vector2(1, 1)),
        };

        // MonoGame's VertexTextureEffect.fx: a GridSize x GridSize grid of float2 POSITIONs, each
        // vertex raised by the height map texel it samples in the VS (point, no mips).
        using var height = new Texture2D(gd, GridSize, GridSize, false, SurfaceFormat.Color);
        using var flat = new Texture2D(gd, GridSize, GridSize, false, SurfaceFormat.Color);
        var heights = new Color[GridSize * GridSize];
        for (int i = 0; i < heights.Length; i++)
            heights[i] = new Color((byte)((i * 37 + (i / GridSize) * 91) % 256), (byte)0, (byte)0, (byte)255);
        height.SetData(heights);
        flat.SetData(Enumerable.Repeat(new Color(0, 0, 0, 255), heights.Length).ToArray());
        var grid = new GridVertex[GridSize * GridSize];
        for (int y = 0; y < GridSize; y++)
        for (int x = 0; x < GridSize; x++)
            grid[y * GridSize + x] = new GridVertex(new Vector2(x, y));
        var gridIndices = new List<short>();
        for (int y = 0; y < GridSize - 1; y++)
        for (int x = 0; x < GridSize - 1; x++)
        {
            short tl = (short)(y * GridSize + x), tr = (short)(tl + 1), bl = (short)(tl + GridSize), br = (short)(bl + 1);
            gridIndices.AddRange(new[] { tl, tr, bl, tr, br, bl });
        }
        short[] gridIdx = gridIndices.ToArray();
        // Row-vector convention (mul(float4(x, height, y, 1), WorldViewProj)): x across, the grid's
        // y up the screen, and the height pushing each vertex further up, so a wrong fetch moves it.
        float step = 1f / (GridSize - 1);
        var worldViewProj = new Matrix(
            2f * step, 0f,          0f,   0f,
            0f,        0.6f,        0f,   0f,
            0f,        1.2f * step, 0f,   0f,
            -1f,       -0.9f,       0.5f, 1f);

        foreach (LegacyLodJob job in _jobs)
        {
            Trace($"{job.Name}: render target");
            using var rt = new RenderTarget2D(gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
            try
            {
                Trace($"{job.Name}: effect");
                using var effect = new Effect(gd, job.Mgfx);
                if (job.Draw != LegacyLodDraw.LodQuad)
                {
                    EffectParameter? map = effect.Parameters["HeightMapTexture"];
                    if (map is null || effect.Parameters["WorldViewProj"] is null || effect.Parameters["HeightMapSize"] is null)
                    {
                        Errors[job.Name] = "the effect lacks HeightMapTexture / WorldViewProj / HeightMapSize (parameters: " +
                                           string.Join(", ", effect.Parameters.Select(p => p.Name)) + ")";
                        continue;
                    }
                    gd.SetRenderTarget(rt);
                    gd.Clear(Color.Black);
                    gd.BlendState = BlendState.Opaque;
                    gd.DepthStencilState = DepthStencilState.None;
                    gd.RasterizerState = RasterizerState.CullNone;
                    map.SetValue(job.Draw == LegacyLodDraw.MonoGameVtfFlat ? flat : height);
                    effect.Parameters["WorldViewProj"]!.SetValue(worldViewProj);
                    effect.Parameters["HeightMapSize"]!.SetValue((float)GridSize);
                    foreach (EffectPass pass in effect.CurrentTechnique.Passes)
                    {
                        gd.SamplerStates[0] = SamplerState.PointClamp;
                        gd.VertexSamplerStates[0] = SamplerState.PointClamp;
                        Trace($"{job.Name}: apply");
                        pass.Apply();
                        Trace($"{job.Name}: draw");
                        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, grid, 0, grid.Length, gridIdx, 0, gridIdx.Length / 3, GridVertex.Declaration);
                    }
                    gd.SetRenderTarget(null);
                    Trace($"{job.Name}: readback");
                    var gridPixels = new Color[Size * Size];
                    rt.GetData(gridPixels);
                    Captures[job.Name] = gridPixels;
                    continue;
                }

                EffectParameter? tex = effect.Parameters["Tex"];
                if (tex is null)
                {
                    Errors[job.Name] = "the effect has no 'Tex' parameter (parameters: " +
                                       string.Join(", ", effect.Parameters.Select(p => p.Name)) + ")";
                    continue;
                }

                gd.SetRenderTarget(rt);
                gd.Clear(Color.Black);
                gd.BlendState = BlendState.Opaque;
                gd.DepthStencilState = DepthStencilState.None;
                gd.RasterizerState = RasterizerState.CullNone;
                tex.SetValue(texture);
                foreach (EffectPass pass in effect.CurrentTechnique.Passes)
                {
                    gd.SamplerStates[0] = SamplerState.PointWrap;
                    gd.VertexSamplerStates[0] = SamplerState.PointWrap;
                    Trace($"{job.Name}: apply");
                    pass.Apply();
                    Trace($"{job.Name}: draw");
                    // An indexed triangle LIST, the draw the other VS-driven drivers use: MonoGame
                    // 3.8.5's DesktopVK faults natively on a user-primitive triangle strip.
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quad, 0, quad.Length, QuadIndices, 0, 2);
                }
                gd.SetRenderTarget(null);

                Trace($"{job.Name}: readback");
                var pixels = new Color[Size * Size];
                rt.GetData(pixels);
                Captures[job.Name] = pixels;

                if (_outDir is not null)
                {
                    try
                    {
                        Directory.CreateDirectory(_outDir);
                        using var png = File.Create(Path.Combine(_outDir, job.Name + ".png"));
                        rt.SaveAsPng(png, Size, Size);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [legacy-lod] {job.Name}: PNG not written ({ex.GetType().Name}: {ex.Message})");
                    }
                }
            }
            catch (Exception ex)
            {
                Errors[job.Name] = $"{ex.GetType().Name}: {ex.Message}";
                try { gd.SetRenderTarget(null); } catch { }
            }
        }

        _done = true;
        Exit();
    }

    /// <summary>The largest per-channel difference in column <paramref name="column"/> (all columns when null).</summary>
    public static int MaxDelta(Color[] a, Color[] b, int? column = null)
    {
        int max = 0;
        int width = Size / Columns;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            if (column is int c && x / width != c)
                continue;
            Color p = a[y * Size + x], q = b[y * Size + x];
            max = Math.Max(max, Math.Max(Math.Max(Math.Abs(p.R - q.R), Math.Abs(p.G - q.G)),
                                         Math.Max(Math.Abs(p.B - q.B), Math.Abs(p.A - q.A))));
        }
        return max;
    }

    /// <summary>The mip level each column's centre pixel reads, from the red channel (28 * level).</summary>
    public static string ColumnLevels(Color[] pixels)
    {
        int width = Size / Columns;
        return string.Join(" ", Enumerable.Range(0, Columns).Select(c =>
        {
            Color p = pixels[(Size / 2) * Size + c * width + width / 2];
            return $"c{c}:L{p.R / 28.0:0.#}({p.R},{p.G},{p.B})";
        }));
    }

    /// <summary>
    /// Verdict for MonoGame's own VertexTextureEffect.fx: the candidate equals the reference, and the
    /// reference with a flat height map differs (the vertex texture fetch moves the grid).
    /// </summary>
    public bool JudgeVtf(string tag, string reference, string candidate, string flat, int tolerance)
    {
        foreach (string name in new[] { reference, candidate, flat })
        {
            if (!Captures.ContainsKey(name))
            {
                Console.WriteLine($"[{tag}] FAIL, {name} did not render{(Errors.TryGetValue(name, out string? e) ? ": " + e : "")}.");
                return false;
            }
        }
        int maxd = MaxDelta(Captures[reference], Captures[candidate]);
        int moved = MaxDelta(Captures[reference], Captures[flat]);
        int lit = Captures[candidate].Count(p => p.R > 0 || p.G > 0);
        Console.WriteLine($"[{tag}] VertexTextureEffect.fx candidate vs reference: maxd {maxd} (tolerance {tolerance}); " +
                          $"flat height map moves the picture by {moved}; candidate lit pixels {lit}");
        return maxd <= tolerance && moved > 16 && lit > Size * Size / 8;
    }

    /// <summary>
    /// The positive controls: each overrides ONE macro of the fixture, which models the rewrite
    /// dropping that intrinsic's w operand (the LOD, the bias, the divide), and names the one
    /// column that must move. A scene that cannot see the change cannot prove the rewrite.
    /// </summary>
    public static IReadOnlyList<(string Name, string Macro, string Value, int Column)> Controls { get; } =
    [
        ("control-vs-lod",   "LOD_VS", "0", 0),
        ("control-ps-lod",   "LOD_PS", "0", 1),
        ("control-bias",     "BIAS",   "0", 2),
        ("control-proj-div", "PROJ_Q", "1", 3),
    ];

    /// <summary>
    /// Verdict for one target. <paramref name="reference"/> and <paramref name="candidate"/> are
    /// capture names; <paramref name="extraEqual"/> are further captures that must equal the
    /// reference too (e.g. mgfxc's build of the modern twin). Prints every number it decides on.
    /// </summary>
    public bool Judge(string tag, string reference, string candidate, int tolerance, params string[] extraEqual)
    {
        bool ok = true;
        foreach ((string name, string error) in Errors)
            Console.WriteLine($"[{tag}] {name}: DID NOT RENDER: {error}");

        if (!Captures.TryGetValue(reference, out Color[]? refPx) || !Captures.TryGetValue(candidate, out Color[]? candPx))
        {
            Console.WriteLine($"[{tag}] FAIL, reference or candidate did not render.");
            return false;
        }

        Console.WriteLine($"[{tag}] {reference,-22} {ColumnLevels(refPx)}");
        Console.WriteLine($"[{tag}] {candidate,-22} {ColumnLevels(candPx)}");
        int maxd = MaxDelta(refPx, candPx);
        Console.WriteLine($"[{tag}] candidate vs reference: maxd {maxd} (tolerance {tolerance})");
        ok &= maxd <= tolerance;

        foreach (string extra in extraEqual)
        {
            if (!Captures.TryGetValue(extra, out Color[]? extraPx))
            {
                Console.WriteLine($"[{tag}] FAIL, {extra} did not render.");
                ok = false;
                continue;
            }
            int d = MaxDelta(refPx, extraPx);
            Console.WriteLine($"[{tag}] {extra} vs reference: maxd {d} (tolerance {tolerance})");
            ok &= d <= tolerance;
        }

        // The scene must read four different levels (VS L4, PS L1, bias L3, proj L2), or the
        // diff above proves less than it claims.
        int width = Size / Columns;
        var levels = Enumerable.Range(0, Columns)
            .Select(c => refPx[(Size / 2) * Size + c * width + width / 2].R)
            .ToArray();
        bool distinct = levels.Distinct().Count() == Columns;
        Console.WriteLine($"[{tag}] reference reads four distinct levels: {distinct}");
        ok &= distinct;

        foreach ((string name, _, _, int column) in Controls)
        {
            if (!Captures.TryGetValue(name, out Color[]? ctl))
            {
                Console.WriteLine($"[{tag}] FAIL, positive control {name} did not render.");
                ok = false;
                continue;
            }
            int moved = MaxDelta(refPx, ctl, column);
            int others = Enumerable.Range(0, Columns).Where(c => c != column).Max(c => MaxDelta(refPx, ctl, c));
            bool detected = moved > 16 && others <= tolerance;
            Console.WriteLine($"[{tag}] {name}: column {column} moved by {moved}, other columns by {others}: " +
                              (detected ? "DETECTED" : "NOT DETECTED"));
            ok &= detected;
        }

        return ok;
    }
}

/// <summary>
/// One target's legacy-LOD arm: compiles <c>Tex2DLodRender.fx</c> with ShadowDusk for the target
/// (the candidate, plus the four <see cref="LegacyLodScene.Controls"/>), loads the committed
/// reference effects, draws everything through one <see cref="LegacyLodScene"/> and returns its
/// verdict. The first reference is the one the candidate is diffed against; any further
/// reference must equal it.
/// </summary>
public static class LegacyLodArm
{
    public static async Task<bool> RunAsync(
        PlatformTarget target, string repoRoot, string tag, string outDirName, int tolerance,
        string vtfReference, params (string Name, string File)[] references)
    {
        string dir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "legacy-lod-intrinsics");
        string source = await File.ReadAllTextAsync(Path.Combine(dir, "Tex2DLodRender.fx"));
        var jobs = new List<LegacyLodJob>();
        foreach ((string name, string file) in references)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                Console.WriteLine($"[{tag}] FAIL, reference missing: {path}");
                return false;
            }
            jobs.Add(new LegacyLodJob(name, await File.ReadAllBytesAsync(path)));
        }

        async Task<bool> Compile(string name, params UserDefine[] defines)
        {
            var r = await new EffectCompiler().CompileAsync(source, new CompilerOptions
            {
                Target = target,
                SourceFileName = "Tex2DLodRender.fx",
                Defines = defines,
            });
            if (r.IsFailure)
            {
                Console.WriteLine($"[{tag}] {name}: COMPILE FAIL: " +
                                  string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}")));
                return false;
            }
            Console.WriteLine($"[{tag}] {name}: {r.Value.Data.Length} bytes");
            jobs.Add(new LegacyLodJob(name, r.Value.Data));
            return true;
        }

        bool compiled = await Compile("candidate-sd");
        foreach ((string name, string macro, string value, _) in LegacyLodScene.Controls)
            compiled &= await Compile(name, new UserDefine(macro, value));

        // MonoGame's own VertexTextureEffect.fx (vendored, tests/fixtures/shaders/third-party/MonoGame):
        // its SM4 branch fetches the height map with tex2Dlod. It failed FX0012 on every target before
        // the rewrite, DirectX 12 and Vulkan included although their SM6 branch uses SampleLevel (the
        // raw-text pre-parse saw the dead branch's call). Reference: the reference compiler's build of
        // the same file for this target, drawn on the same device.
        string vtfGolden = Path.Combine(dir, vtfReference);
        string vtfSource = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "third-party", "MonoGame", "VertexTextureEffect.fx");
        var vtf = await new EffectCompiler().CompileAsync(await File.ReadAllTextAsync(vtfSource), new CompilerOptions
        {
            Target = target,
            SourceFileName = "VertexTextureEffect.fx",
        });
        if (vtf.IsFailure || !File.Exists(vtfGolden))
        {
            Console.WriteLine($"[{tag}] VertexTextureEffect.fx: " + (vtf.IsFailure
                ? "COMPILE FAIL: " + string.Join(" | ", vtf.Error.Select(e => $"{e.Code}: {e.Message}"))
                : $"reference missing: {vtfGolden}"));
            return false;
        }
        byte[] vtfGoldenBytes = await File.ReadAllBytesAsync(vtfGolden);
        jobs.Add(new LegacyLodJob("vtf-mgfxc", vtfGoldenBytes, LegacyLodDraw.MonoGameVtf));
        jobs.Add(new LegacyLodJob("vtf-candidate-sd", vtf.Value.Data, LegacyLodDraw.MonoGameVtf));
        jobs.Add(new LegacyLodJob("vtf-mgfxc-flat", vtfGoldenBytes, LegacyLodDraw.MonoGameVtfFlat));
        if (!compiled)
            return false;

        using var scene = new LegacyLodScene(jobs, Path.Combine(repoRoot, "validation", "output", outDirName));
        scene.Run();
        bool lod = scene.Judge(tag, references[0].Name, "candidate-sd", tolerance,
            references.Skip(1).Select(r => r.Name).ToArray());
        bool vtfOk = scene.JudgeVtf(tag, "vtf-mgfxc", "vtf-candidate-sd", "vtf-mgfxc-flat", tolerance);
        return lod && vtfOk;
    }
}
