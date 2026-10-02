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
using ShadowDusk.Slang;

namespace ShadowDusk.Validation.Slang;

/// <summary>
/// Issue #230: the real-<c>Effect</c> render gate for <see cref="SlangCompiler"/> on a MonoGame
/// 3.8.5 native backend (DirectX_12 via <c>validation/SlangFullCorpusDx12</c>, Vulkan via
/// <c>validation/SlangFullCorpusVulkan</c>). Same-backend only: both arms load into ONE real
/// <c>GraphicsDevice</c> of the target's own runtime.
///
/// <list type="bullet">
/// <item><b>Candidate</b>: <see cref="SlangCompiler"/>'s output for the target - the product route.</item>
/// <item><b>Reference</b>: the real <c>mgfxc</c> 3.8.5 build of the SAME assembled <c>.fx</c>
/// <see cref="SlangCompiler"/> handed the faithful pipeline, with only its shader-model header
/// switched to SM6 (<see cref="SlangGateCorpus.WithSm6ProfileHeader"/>) and, on Vulkan only, its
/// textures/samplers given explicit registers (<see cref="SlangGateCorpus.WithExplicitTextureRegisters"/>).
/// The driver proves both changes are a no-op for ShadowDusk: <see cref="EffectCompiler"/> on the switched text must give
/// bytes IDENTICAL to the candidate, or the row fails as a parity break.</item>
/// </list>
///
/// PS-only effects draw the cat through <see cref="SpriteBatch"/> (the corpus gates' scene);
/// effects with a vertex stage draw a clip-space quad through a custom vertex declaration so
/// their OWN vertex shader runs, under <see cref="SlangShaderParams.VertexTransform"/>.
///
/// Positive controls run on every invocation as extra rows that MUST diverge from the
/// reference: <c>Invert</c> with two channels swapped (pixel stage) and <c>Desaturate</c> with
/// its transform transposed (vertex stage). With <see cref="SlangGateCorpus.ControlEnvVar"/>=1
/// the same defects replace the gated rows' candidates, so the run must exit non-zero.
/// </summary>
public static class SlangMonoGameGate
{
    public static async Task<int> RunAsync(PlatformTarget target, string mgfxcProfile, string label, int tolerance)
    {
        string repoRoot = SlangGateCorpus.FindRepoRoot();
        string[] corpus = SlangGateCorpus.Corpus(repoRoot);
        string outDir = Path.Combine(repoRoot, "validation", "output-slang-full-corpus", label);
        string fxDir = Path.Combine(outDir, "reference-fx");
        string mgfxDir = Path.Combine(outDir, "mgfx");
        Directory.CreateDirectory(fxDir);
        Directory.CreateDirectory(mgfxDir);

        bool plantControls = SlangGateCorpus.ControlRequested();
        string mgfxc = SlangGateCorpus.LocateMgfxc();

        // The donor vertex shader for PS-only effects that do not read COLOR (see
        // SlangGateCorpus.PixelStageReadsColor), built by the reference compiler.
        string donorFx = Path.Combine(fxDir, "_Donor.fx");
        await File.WriteAllTextAsync(donorFx, SlangGateCorpus.DonorFx);
        var (donorBytes, donorErr) = await SlangGateCorpus.RunMgfxcAsync(
            mgfxc, donorFx, Path.Combine(mgfxDir, "_Donor.mgfx"), mgfxcProfile);
        if (donorBytes is null)
        {
            Console.Error.WriteLine($"[slang-{label}] FAIL: the donor vertex shader did not build: {donorErr}");
            return 1;
        }

        Console.WriteLine($"[slang-{label}] corpus    : {corpus.Length} shaders (ShadowDusk.Slang real-slangc route)");
        Console.WriteLine($"[slang-{label}] slangc    : {SlangToolPath.Resolve() ?? "(not found)"}");
        Console.WriteLine($"[slang-{label}] reference : mgfxc {SlangGateCorpus.MgfxcVersion} /Profile:{mgfxcProfile} ({mgfxc})");
        Console.WriteLine($"[slang-{label}] tolerance : {tolerance}/255 per channel");
        Console.WriteLine($"[slang-{label}] out       : {outDir}");
        if (plantControls)
            Console.WriteLine($"[slang-{label}] POSITIVE CONTROL ACTIVE ({SlangGateCorpus.ControlEnvVar}=1): this run is expected to FAIL");
        Console.WriteLine();

        var rows = new List<Row>();
        var jobs = new List<RenderJob>();
        var compiler = new SlangCompiler();
        int failures = 0;

        foreach (string file in corpus)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string source = await File.ReadAllTextAsync(file);
            bool vs = SlangGateCorpus.HasVertexStage(source);
            Scene scene = vs ? Scene.VsQuad
                : SlangGateCorpus.PixelStageReadsColor(source) ? Scene.Sprite
                : Scene.DonorQuad;
            var options = new CompilerOptions { Target = target, SourceFileName = Path.GetFileName(file) };

            // ---- Candidate: the product route.
            var candidate = compiler.Compile(source, options);
            byte[]? candBytes = candidate.IsSuccess ? candidate.Value.Data : null;
            string? candErr = candidate.IsFailure
                ? string.Join(" | ", candidate.Error.Select(e => $"{e.Code}: {e.Message}"))
                : null;

            // ---- Reference: mgfxc on the assembled .fx (SM6 header), plus the parity proof.
            byte[]? refBytes = null;
            string? refErr = null;
            string? parityErr = null;
            try
            {
                string fx6 = SlangGateCorpus.WithSm6ProfileHeader(
                    SlangGateCorpus.CaptureAssembledFx(source, Path.GetFileName(file), target));
                if (target == PlatformTarget.Vulkan)
                    fx6 = SlangGateCorpus.WithExplicitTextureRegisters(fx6);
                string fxPath = Path.Combine(fxDir, name + ".fx");
                await File.WriteAllTextAsync(fxPath, fx6);

                var parity = await new EffectCompiler().CompileAsync(fx6, new CompilerOptions
                {
                    Target = target,
                    SourceFileName = Path.GetFileName(file),
                });
                if (parity.IsFailure)
                    parityErr = "ShadowDusk rejects the SM6-header text: " + string.Join(" | ", parity.Error.Select(e => $"{e.Code}: {e.Message}"));
                else if (candBytes is not null && !parity.Value.Data.AsSpan().SequenceEqual(candBytes))
                    parityErr = "the reference-text adjustment (SM6 header, Vulkan explicit registers) changed ShadowDusk's output bytes, so mgfxc would not be compiling the candidate's program";

                (refBytes, refErr) = await SlangGateCorpus.RunMgfxcAsync(
                    mgfxc, fxPath, Path.Combine(mgfxDir, name + ".reference.mgfx"), mgfxcProfile);
            }
            catch (Exception ex)
            {
                refErr = ex.Message;
            }

            if (candBytes is not null)
                await File.WriteAllBytesAsync(Path.Combine(mgfxDir, name + ".candidate.mgfx"), candBytes);

            if (plantControls && (name == SlangGateCorpus.PixelControlShader || name == SlangGateCorpus.VertexControlShader))
                (candBytes, candErr) = CompileControl(compiler, source, name, options);

            var row = new Row(name, scene, Gate: true, parityErr);
            rows.Add(row);
            jobs.Add(new RenderJob(row.RefKey, refBytes, refErr, scene));
            jobs.Add(new RenderJob(row.CandKey, candBytes, candErr, scene));

            // ---- The always-on positive controls (expected to DIVERGE from this reference).
            if (name == SlangGateCorpus.PixelControlShader || name == SlangGateCorpus.VertexControlShader)
            {
                var (ctlBytes, ctlErr) = CompileControl(compiler, source, name, options);
                var ctl = new Row(name + "~control", scene, Gate: false, null, RefKeyOverride: row.RefKey);
                rows.Add(ctl);
                jobs.Add(new RenderJob(ctl.CandKey, ctlBytes, ctlErr, scene));
            }
        }

        Console.WriteLine($"[slang-{label}] compile results:");
        foreach (var j in jobs)
            Console.WriteLine($"  [{(j.Bytes is null ? "FAIL" : "OK  ")}] {j.Key,-32} {(j.Error ?? $"{j.Bytes!.Length} bytes")}");
        Console.WriteLine();

        var outcomes = new Dictionary<string, Outcome>();
        using (var game = new SlangEffectRenderer(SlangGateCorpus.CatPath(repoRoot), outDir, jobs, donorBytes))
        {
            game.Run();
            foreach (var o in game.Outcomes)
                outcomes[o.Key] = o;
        }

        Console.WriteLine($"{"shader",-24} {"scene",-7} {"ref",-5} {"cand",-5} {"maxd",5} {"over",6} {"varied",7}  verdict");
        Console.WriteLine(new string('-', 80));
        int gatePass = 0, gateTotal = 0, controlsCaught = 0, controlTotal = 0;
        foreach (var row in rows)
        {
            Outcome r = outcomes[row.RefKey];
            Outcome c = outcomes[row.CandKey];
            int maxd = -1, over = -1;
            double varied = -1;
            string verdict;

            if (r.Pixels is null || c.Pixels is null || !r.Rendered || !c.Rendered)
            {
                verdict = "FAIL (load/render)";
            }
            else
            {
                (maxd, over) = SlangGateCorpus.Compare(r.Pixels, c.Pixels, tolerance);
                varied = SlangGateCorpus.VariedFraction(c.Pixels);
                if (!row.Gate)
                    verdict = over > 0 ? "CAUGHT (control diverged, as required)" : "FAIL (control NOT detected)";
                else if (row.ParityError is not null)
                    verdict = "FAIL (parity)";
                else if (varied < 0.05)
                    verdict = "FAIL (near-constant image)";
                else if (over > 0)
                    verdict = "FAIL (pixels differ)";
                else
                    verdict = "PASS";
            }

            if (row.Gate)
            {
                gateTotal++;
                if (verdict == "PASS") gatePass++; else failures++;
            }
            else
            {
                controlTotal++;
                if (verdict.StartsWith("CAUGHT", StringComparison.Ordinal)) controlsCaught++; else failures++;
            }

            Console.WriteLine(
                $"{row.Name,-24} {SceneLabel(row.Scene),-7} {(r.Rendered ? "ok" : "FAIL"),-5} {(c.Rendered ? "ok" : "FAIL"),-5} " +
                $"{(maxd >= 0 ? maxd.ToString() : "-"),5} {(over >= 0 ? over.ToString() : "-"),6} {(varied >= 0 ? varied.ToString("P0") : "-"),7}  {verdict}");
            if (r.Error is not null) Console.WriteLine($"{"",-26}ref:    {r.Error}");
            if (c.Error is not null) Console.WriteLine($"{"",-26}cand:   {c.Error}");
            if (row.ParityError is not null) Console.WriteLine($"{"",-26}parity: {row.ParityError}");
            if (r.ParamsSet is not null && c.ParamsSet is not null && !r.ParamsSet.SequenceEqual(c.ParamsSet))
            {
                Console.WriteLine($"{"",-26}params: ref [{string.Join(", ", r.ParamsSet)}] cand [{string.Join(", ", c.ParamsSet)}]");
                if (row.Gate && verdict == "PASS")
                {
                    // A parameter the reference exposes and the candidate does not is a lost
                    // binding even when the pixels happen to agree.
                    failures++;
                    gatePass--;
                    Console.WriteLine($"{"",-26}FAIL: parameter tables differ");
                }
            }
        }
        Console.WriteLine(new string('-', 80));

        int maxGateDelta = rows.Where(r => r.Gate)
            .Select(r => (outcomes[r.RefKey].Pixels, outcomes[r.CandKey].Pixels))
            .Where(p => p.Item1 is not null && p.Item2 is not null && p.Item1.Length == p.Item2.Length)
            .Select(p => SlangGateCorpus.Compare(p.Item1!, p.Item2!, tolerance).MaxDelta)
            .DefaultIfEmpty(-1).Max();

        Console.WriteLine($"\n[slang-{label}] GATE: {gatePass}/{gateTotal} PASS vs mgfxc {SlangGateCorpus.MgfxcVersion} /Profile:{mgfxcProfile} " +
                          $"(real MonoGame {mgfxcProfile} Effect load + render, same device; max gate delta {maxGateDelta}/255, tolerance {tolerance}).");
        Console.WriteLine($"[slang-{label}] positive controls: {controlsCaught}/{controlTotal} diverged from the reference as required.");
        Console.WriteLine(failures == 0 ? $"[slang-{label}] PASSED" : $"[slang-{label}] {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    private static (byte[]? Bytes, string? Error) CompileControl(
        SlangCompiler compiler, string source, string name, CompilerOptions options)
    {
        string mutated = name == SlangGateCorpus.PixelControlShader
            ? SlangGateCorpus.PerturbPixel(source)
            : SlangGateCorpus.PerturbVertex(source);
        var result = compiler.Compile(mutated, options);
        return result.IsSuccess
            ? (result.Value.Data, null)
            : (null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
    }

    private static string SceneLabel(Scene scene) => scene switch
    {
        Scene.Sprite => "sprite",
        Scene.DonorQuad => "donor",
        _ => "vs",
    };

    private sealed record Row(string Name, Scene Scene, bool Gate, string? ParityError, string? RefKeyOverride = null)
    {
        public string RefKey => RefKeyOverride ?? Name + ".reference";
        public string CandKey => Name + ".candidate";
    }
}

/// <summary>One effect to load and render.</summary>
public sealed record RenderJob(string Key, byte[]? Bytes, string? Error, Scene Scene);

/// <summary>How an effect is drawn.</summary>
public enum Scene
{
    /// <summary>PS-only, input = SpriteBatch's vertex output: drawn through SpriteBatch.</summary>
    Sprite,

    /// <summary>PS-only, input = <c>SV_Position, TEXCOORD0</c>: a clip-space quad behind the
    /// donor vertex shader (<see cref="SlangGateCorpus.DonorFx"/>), the texture bound by name.</summary>
    DonorQuad,

    /// <summary>The effect ships its own vertex shader: a clip-space quad under
    /// <see cref="SlangShaderParams.VertexTransform"/>.</summary>
    VsQuad,
}

/// <summary>What one job did in the real runtime; <see cref="Pixels"/> is tightly packed RGBA8.</summary>
public sealed record Outcome(string Key, bool Loaded, bool Rendered, string? Error, byte[]? Pixels, IReadOnlyList<string>? ParamsSet);

/// <summary>
/// Loads each job into a REAL MonoGame <see cref="Effect"/> on one device and renders one frame
/// to an offscreen target: PS-only effects over the cat through <see cref="SpriteBatch"/>
/// (mirrors <c>validation/SharedDx/DxEffectImageRenderer</c>), vertex-stage effects as a
/// clip-space quad through a custom vertex declaration (mirrors
/// <c>validation/Shared/VsEffectImageRenderer</c>). Both arms of a row go through this same
/// code, so a pixel difference is attributable only to the compiler that built the bytes.
/// </summary>
public sealed class SlangEffectRenderer : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly IReadOnlyList<RenderJob> _jobs;
    private readonly byte[] _donorBytes;

    private SpriteBatch _sb = null!;
    private Effect _donor = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<Outcome> Outcomes { get; } = new();

    public SlangEffectRenderer(string catPath, string outDir, IReadOnlyList<RenderJob> jobs, byte[] donorBytes)
    {
        _catPath = catPath;
        _outDir = outDir;
        _jobs = jobs;
        _donorBytes = donorBytes;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk Slang render gate (headless)";
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        using var fs = File.OpenRead(_catPath);
        _cat = Texture2D.FromStream(GraphicsDevice, fs);
        _donor = new Effect(GraphicsDevice, _donorBytes);
        Directory.CreateDirectory(Path.Combine(_outDir, "png"));
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done)
        {
            Exit();
            return;
        }

        GraphicsDevice.Clear(Color.Black);
        foreach (var job in _jobs)
            Outcomes.Add(RenderOne(job));

        _done = true;
        Exit();
    }

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

    private Outcome RenderOne(RenderJob job)
    {
        if (job.Bytes is null)
            return new Outcome(job.Key, false, false, $"compile failed: {job.Error}", null, null);

        Effect effect;
        try
        {
            effect = new Effect(GraphicsDevice, job.Bytes);
        }
        catch (Exception ex)
        {
            return new Outcome(job.Key, false, false, $"new Effect() threw: {ex.GetType().Name}: {ex.Message}", null, null);
        }

        int w = _cat.Width, h = _cat.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        try
        {
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);

            (IReadOnlyList<string> set, string? paramError) = (Array.Empty<string>(), null);

            if (job.Scene == Scene.Sprite)
            {
                var dest = new Rectangle(0, 0, w, h);
                // Prime SpriteBatch's sprite vertex shader (pixel-only effects ride it).
                _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                _sb.Draw(_cat, dest, Color.White);
                _sb.End();

                (set, paramError) = SlangShaderParams.Apply(effect, _cat);

                _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, effect);
                _sb.Draw(_cat, dest, Color.White);
                _sb.End();
            }
            else
            {
                GraphicsDevice.BlendState = BlendState.Opaque;
                GraphicsDevice.DepthStencilState = DepthStencilState.None;
                GraphicsDevice.RasterizerState = RasterizerState.CullNone;
                GraphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;

                GraphicsDevice.Textures[0] = null;

                (set, paramError) = SlangShaderParams.Apply(effect, _cat);

                var verts = new[]
                {
                    new VsVertex(new Vector3(-1f,  1f, 0f), Color.White, new Vector2(0f, 0f)), // TL
                    new VsVertex(new Vector3( 1f,  1f, 0f), Color.White, new Vector2(1f, 0f)), // TR
                    new VsVertex(new Vector3(-1f, -1f, 0f), Color.White, new Vector2(0f, 1f)), // BL
                    new VsVertex(new Vector3( 1f, -1f, 0f), Color.White, new Vector2(1f, 1f)), // BR
                };
                var indices = new short[] { 0, 1, 2, 2, 1, 3 };
                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    // DonorQuad: the donor's pass binds its vertex shader first; the effect's
                    // PS-only pass then keeps it (a pass without a VertexShader leaves the bound
                    // one in place, exactly how SpriteBatch donates its own).
                    if (job.Scene == Scene.DonorQuad)
                        _donor.CurrentTechnique.Passes[0].Apply();
                    pass.Apply();
                    GraphicsDevice.DrawUserIndexedPrimitives(
                        PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, 2, VsVertex.Declaration);
                }
            }

            GraphicsDevice.SetRenderTarget(null);

            var colors = new Color[w * h];
            rt.GetData(colors);
            var rgba = new byte[colors.Length * 4];
            for (int i = 0; i < colors.Length; i++)
            {
                rgba[i * 4] = colors[i].R;
                rgba[i * 4 + 1] = colors[i].G;
                rgba[i * 4 + 2] = colors[i].B;
                rgba[i * 4 + 3] = colors[i].A;
            }

            using (var outFs = File.Create(Path.Combine(_outDir, "png", job.Key + ".png")))
                rt.SaveAsPng(outFs, w, h);

            return new Outcome(job.Key, true, paramError is null, paramError, rgba, set);
        }
        catch (Exception ex)
        {
            try { _sb.End(); } catch { /* may not be in a batch */ }
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            return new Outcome(job.Key, true, false, $"render threw: {ex.GetType().Name}: {ex.Message}", null, null);
        }
        finally
        {
            effect.Dispose();
        }
    }
}
