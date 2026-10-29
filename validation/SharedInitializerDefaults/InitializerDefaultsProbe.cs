#nullable enable

// Rung-4 proof that a global initializer (`float R = 1;`) is the parameter's default in the REAL engine.
//
// Compiles tests/fixtures/golden/initializer-defaults/*.fx with ShadowDusk, loads the bytes into a real
// Effect (MonoGame or KNI, whichever the including project references; the Microsoft.Xna.Framework API is
// the same), draws a SpriteBatch quad with the effect WITHOUT calling any Set, and asserts the centre
// pixel is what the default produces. Controls that must NOT show the default:
//   * the same shader with the initializer removed (`float R;`) renders black;
//   * on the GL runtimes, the real mgfxc 3.8.4.1 OpenGL build of the same shader (it stores zeros,
//     "HACK: We don't have real default parameters from mojoshader!") renders black.
// A control that turned red would mean the test cannot tell a default from a Set. Exit code 0 iff every
// case and every control passes. See docs/validation-matrix.md section 6.
//
// With knifxReachRows (the KNI drivers), a second device on GraphicsProfile.Reach pins the shader model a
// KNIFX container records for `*_4_0_level_9_1`: KNI's own pipeline records 2.0, which Reach (ceiling 2.0)
// loads, for a pixel and a vertex shader, while `*_4_0_level_9_3` (3.0), `ps_4_0` (4.0) and, on GL,
// `ps_3_0` (3.0) must be refused with KNI's own "Shader model X is not supported by the current graphics
// profile 'Reach'". The refusals are the positive controls: they prove the Reach device checks the version.
//
// The same drivers pin `*_2_0` / `*_3_0` on KNIFX: KNI's own pipeline accepts them on DirectX (compiling
// them as `*_4_0_level_9_1` / `_9_3`, recording 2.0 / 3.0). ps_2_0 and vs_2_0 must load and render on HiDef
// and Reach, ps_3_0 and vs_3_0 on HiDef, and the _3_0 ones must be refused on Reach. On DirectX each loading
// row is also compared, every pixel, with KNI's own MGCB 4.2.9001 build of the same source
// (tests/fixtures/golden/initializer-defaults/kni-4.2.9001-Windows), which must give the same verdicts; an
// MGFX DirectX compile of ps_2_0 must still be refused with SD0015 (mgfxc's floor).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
// KNI WinForms.DX11 pulls System.Drawing into the implicit usings: name the XNA types.
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace ShadowDusk.Validation.InitializerDefaults;

/// <summary>One shader and the pixel its default value must produce.</summary>
internal sealed record DefaultCase(string Fx, bool GlSupported, byte R, byte G, byte B, byte A, int Tolerance = 1);

public static class InitializerDefaultsProbe
{
    private static readonly DefaultCase[] Cases =
    [
        new("RedDefault.fx",   true,  255, 0,   0, 255),   // float R = 1;
        new("A_Tint.fx",       true,  255, 0,   0, 255),   // float4 Tint = float4(1,0,0,1);
        new("B_Scale.fx",      true,  128, 128, 128, 255), // float Scale = 0.5;
        new("E_Array.fx",      true,  128, 128, 0, 255),   // float4 Colors[2] = { red, green };
        new("MatrixDefault.fx", true, 0,   128, 0, 255),   // float4x4 with M[0][1] = 1: a transposed blob gives (64,0,0)
        new("C_Vec2Mat2.fx",   false, 32,  96,  0, 255),   // float2 Offset + float2x2 0.5*I (mat2: not on GL, SD0210)
        new("D_IntBool.fx",    false, 128, 255, 0, 255),   // int Mode = 2; bool Flag = true; (int/bool: not on GL, SD0210)
    ];

    /// <summary>
    /// Runs the probe. <paramref name="mgfxcOpenGlControl"/> is true on the GL runtimes: the committed
    /// mgfxc OpenGL build of RedDefault must render black. <paramref name="skipFloat2x2"/> skips C_Vec2Mat2 on
    /// MonoGame DesktopVK, whose constant buffer throws on ANY float2x2 parameter (measured: the same throw with
    /// the initializers removed), so it says nothing about defaults.
    /// </summary>
    public static int Run(string label, PlatformTarget target, EffectContainer[] containers, bool mgfxcOpenGlControl, bool skipFloat2x2 = false, bool knifxReachRows = false)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(240_000);
            Console.WriteLine($"[{label}] WATCHDOG: killing");
            Environment.Exit(99);
        }) { IsBackground = true };
        watchdog.Start();

        string dir = Path.Combine(FindRepoRoot(), "tests", "fixtures", "golden", "initializer-defaults");
        var compiler = new EffectCompiler();
        var jobs = new List<Job>();
        bool isGl = target == PlatformTarget.OpenGL;

        foreach (EffectContainer container in containers)
        {
            foreach (DefaultCase c in Cases)
            {
                if (!c.GlSupported && isGl) continue;
                if (skipFloat2x2 && c.Fx == "C_Vec2Mat2.fx") continue;
                string path = Path.Combine(dir, c.Fx);
                jobs.Add(Compile(compiler, label, target, container, path, File.ReadAllText(path),
                    $"{Path.GetFileNameWithoutExtension(c.Fx)} [{container}]",
                    new Expect(c.R, c.G, c.B, c.A, c.Tolerance), setR: null));
            }

            // Control 1: the initializer removed. Must be black (the default IS what makes it red).
            string red = File.ReadAllText(Path.Combine(dir, "RedDefault.fx"));
            string bare = red.Replace("float R = 1;", "float R;", StringComparison.Ordinal);
            if (bare == red) throw new InvalidOperationException("RedDefault.fx no longer contains 'float R = 1;'");
            jobs.Add(Compile(compiler, label, target, container, "RedDefault-NoInitializer.fx", bare,
                $"CONTROL no-initializer [{container}]", new Expect(0, 0, 0, 255, 0), setR: null));

            // An explicit Set still overrides the default.
            jobs.Add(Compile(compiler, label, target, container, "RedDefault.fx", red,
                $"Set(R=0.5) overrides the default [{container}]", new Expect(128, 0, 0, 255, 1), setR: 0.5f));
        }

        if (mgfxcOpenGlControl)
        {
            string golden = Path.Combine(dir, "mgfxc-3.8.4.1-OpenGL", "RedDefault.mgfx");
            jobs.Add(new Job("CONTROL real mgfxc OpenGL build (stores zeros)", File.ReadAllBytes(golden), null,
                new Expect(0, 0, 0, 255, 0), null));
        }

        if (knifxReachRows)
            jobs.AddRange(Sm2Sm3HiDefJobs(compiler, label, target, dir));

        var results = new List<JobResult>();
        int expected = jobs.Count;
        using (var game = new ProbeGame(jobs, GraphicsProfile.HiDef))
        {
            game.Run();
            results.AddRange(game.Results);
        }

        if (knifxReachRows)
        {
            List<Job> reachJobs = ReachJobs(compiler, label, target, dir);
            expected += reachJobs.Count;
            using var reach = new ProbeGame(reachJobs, GraphicsProfile.Reach);
            reach.Run();
            results.AddRange(reach.Results);
        }

        int failures = 0;
        Console.WriteLine($"[{label}] results:");
        foreach (JobResult r in results)
        {
            Console.WriteLine($"  [{(r.Ok ? "PASS" : "FAIL")}] {r.Name,-52} {r.Detail}");
            if (!r.Ok) failures++;
        }

        if (results.Count != expected)
        {
            Console.WriteLine($"[{label}] FAIL: only {results.Count} of {expected} cases ran");
            return 1;
        }

        Console.WriteLine($"[{label}] {results.Count - failures}/{results.Count} passed.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The KNIFX rows run on a <see cref="GraphicsProfile.Reach"/> device (ceiling 2.0). A_Tint with its
    /// pixel profile set to <c>ps_4_0_level_9_1</c> (recorded 2.0) must load and render its default, and
    /// the same shader at <c>ps_4_0_level_9_3</c> (3.0), <c>ps_4_0</c> (4.0) and, on OpenGL,
    /// <c>ps_3_0</c> (3.0; below mgfxc's DirectX floor, so not a DirectX row) must be refused by KNI's
    /// shader-model check. A vertex-shader pair does the same for the VS profile: <c>vs_4_0_level_9_1</c>
    /// loads, <c>vs_4_0_level_9_3</c> (with a 2.0 pixel shader, so only the VS can trip it) is refused.
    /// </summary>
    private static List<Job> ReachJobs(EffectCompiler compiler, string label, PlatformTarget target, string dir)
    {
        string path = Path.Combine(dir, "A_Tint.fx");
        string tint = File.ReadAllText(path);
        string Ps(string profile) => TintAt(tint, profile);

        static string Refused(string sm) => $"Shader model {sm} is not supported by the current graphics profile 'Reach'";
        var red = new Expect(255, 0, 0, 255, 1);
        var none = new Expect(0, 0, 0, 0, 0);

        var jobs = new List<Job>
        {
            Compile(compiler, label, target, EffectContainer.Knifx, path, Ps("ps_4_0_level_9_1"),
                "PS ps_4_0_level_9_1 [Knifx] loads on Reach", red, setR: null),
            Compile(compiler, label, target, EffectContainer.Knifx, path, Ps("ps_4_0_level_9_3"),
                "CONTROL PS ps_4_0_level_9_3 [Knifx] refused on Reach", none, setR: null, expectRefusal: Refused("3.0")),
            Compile(compiler, label, target, EffectContainer.Knifx, path, Ps("ps_4_0"),
                "CONTROL PS ps_4_0 [Knifx] refused on Reach", none, setR: null, expectRefusal: Refused("4.0")),
            // ps_3_0 was an OpenGL-only row while the KNIFX DirectX route kept mgfxc's floor; KNI's own
            // DirectX pipeline accepts it (as ps_4_0_level_9_3, recorded 3.0), so it runs on both targets.
            Compile(compiler, label, target, EffectContainer.Knifx, path, Ps("ps_3_0"),
                "CONTROL PS ps_3_0 [Knifx] refused on Reach", none, setR: null, expectRefusal: Refused("3.0")),
        };

        jobs.Add(Compile(compiler, label, target, EffectContainer.Knifx, "ReachVs.fx", ReachVs("vs_4_0_level_9_1"),
            "VS vs_4_0_level_9_1 [Knifx] loads on Reach", red, setR: null));
        jobs.Add(Compile(compiler, label, target, EffectContainer.Knifx, "ReachVs.fx", ReachVs("vs_4_0_level_9_3"),
            "CONTROL VS vs_4_0_level_9_3 [Knifx] refused on Reach", none, setR: null, expectRefusal: Refused("3.0")));

        // SM2/SM3 as XNA-era effects (and every shader in KNI's own EffectTest suite) write them.
        byte[]? Kni(string name) => KniReference(target, dir, name);
        jobs.Add(Compile(compiler, label, target, EffectContainer.Knifx, path, Ps("ps_2_0"),
            "PS ps_2_0 [Knifx] loads on Reach", red, setR: null) with { Reference = Kni("A_Tint_ps_2_0") });
        jobs.Add(Compile(compiler, label, target, EffectContainer.Knifx, "ReachVs.fx", ReachVs("vs_2_0", "ps_2_0"),
            "VS vs_2_0 [Knifx] loads on Reach", red, setR: null) with { Reference = Kni("ReachVs_vs_2_0") });
        jobs.Add(Compile(compiler, label, target, EffectContainer.Knifx, "ReachVs.fx", ReachVs("vs_3_0", "ps_2_0"),
            "CONTROL VS vs_3_0 [Knifx] refused on Reach", none, setR: null, expectRefusal: Refused("3.0")));

        if (target == PlatformTarget.DirectX)
        {
            // KNI's own build of the same sources gives the same verdicts on the same device.
            foreach ((string name, string row, string? refusal) in new[]
                     {
                         ("A_Tint_ps_2_0", "KNI's own build of PS ps_2_0 loads on Reach", (string?)null),
                         ("ReachVs_vs_2_0", "KNI's own build of VS vs_2_0 loads on Reach", null),
                         ("A_Tint_ps_3_0", "CONTROL KNI's own build of PS ps_3_0 refused on Reach", Refused("3.0")),
                         ("ReachVs_vs_3_0", "CONTROL KNI's own build of VS vs_3_0 refused on Reach", Refused("3.0")),
                     })
                jobs.Add(new Job(row, Kni(name), null, refusal is null ? red : none, null, refusal));
        }
        return jobs;
    }

    /// <summary>
    /// The SM2/SM3 KNIFX rows on the HiDef device (ceiling 3.0): <c>ps_2_0</c>, <c>ps_3_0</c>,
    /// <c>vs_2_0</c> and <c>vs_3_0</c> must all load and render the <c>Tint</c> default, on DirectX each
    /// matching KNI's own build of the same source pixel for pixel. On DirectX an MGFX compile of
    /// <c>ps_2_0</c> must still fail with <c>SD0015</c>: MGFX follows mgfxc, which refuses SM1-3.
    /// </summary>
    private static List<Job> Sm2Sm3HiDefJobs(EffectCompiler compiler, string label, PlatformTarget target, string dir)
    {
        string path = Path.Combine(dir, "A_Tint.fx");
        string tint = File.ReadAllText(path);
        var red = new Expect(255, 0, 0, 255, 1);
        byte[]? Kni(string name) => KniReference(target, dir, name);

        var jobs = new List<Job>
        {
            Compile(compiler, label, target, EffectContainer.Knifx, path, TintAt(tint, "ps_2_0"),
                "PS ps_2_0 [Knifx] renders on HiDef", red, setR: null) with { Reference = Kni("A_Tint_ps_2_0") },
            Compile(compiler, label, target, EffectContainer.Knifx, path, TintAt(tint, "ps_3_0"),
                "PS ps_3_0 [Knifx] renders on HiDef", red, setR: null) with { Reference = Kni("A_Tint_ps_3_0") },
            Compile(compiler, label, target, EffectContainer.Knifx, "ReachVs.fx", ReachVs("vs_2_0", "ps_2_0"),
                "VS vs_2_0 [Knifx] renders on HiDef", red, setR: null) with { Reference = Kni("ReachVs_vs_2_0") },
            Compile(compiler, label, target, EffectContainer.Knifx, "ReachVs.fx", ReachVs("vs_3_0", "ps_2_0"),
                "VS vs_3_0 [Knifx] renders on HiDef", red, setR: null) with { Reference = Kni("ReachVs_vs_3_0") },
        };
        if (target == PlatformTarget.DirectX)
        {
            Job mgfx = Compile(compiler, label, target, EffectContainer.Mgfx, path, TintAt(tint, "ps_2_0"),
                "CONTROL PS ps_2_0 [Mgfx] refused at compile time (SD0015)", red, setR: null);
            jobs.Add(mgfx with { ExpectCompileError = "SD0015" });
        }
        return jobs;
    }

    /// <summary>A_Tint with both arms of its header naming <paramref name="profile"/>, so the row tests that profile on either target.</summary>
    private static string TintAt(string tint, string profile)
    {
        const string Level91 = "#define PS_SHADERMODEL ps_4_0_level_9_1";
        const string GlArm = "#define PS_SHADERMODEL ps_3_0";
        if (!tint.Contains(Level91, StringComparison.Ordinal) || !tint.Contains(GlArm, StringComparison.Ordinal))
            throw new InvalidOperationException($"A_Tint.fx no longer contains '{Level91}' and '{GlArm}'");
        return tint
            .Replace(Level91, "#define PS_SHADERMODEL " + profile, StringComparison.Ordinal)
            .Replace(GlArm, "#define PS_SHADERMODEL " + profile, StringComparison.Ordinal);
    }

    /// <summary>
    /// KNI's own MGCB 4.2.9001 <c>/platform:Windows</c> build of <paramref name="name"/> (the KNIF payload of
    /// its <c>.xnb</c>), for the DirectX rows; <c>null</c> on OpenGL, where there is no DirectX reference.
    /// </summary>
    private static byte[]? KniReference(PlatformTarget target, string dir, string name) =>
        target == PlatformTarget.DirectX
            ? File.ReadAllBytes(Path.Combine(dir, "kni-4.2.9001-Windows", name + ".knifx"))
            : null;

    /// <summary>
    /// A vertex + pixel effect for the Reach VS rows. The VS ignores SpriteBatch's transform and places
    /// the quad from its texture coordinates, so a full-target sprite covers the target; the PS returns
    /// the <c>Tint</c> default (red). The pixel profile defaults to <c>ps_4_0_level_9_1</c> (2.0).
    /// </summary>
    private static string ReachVs(string vsProfile, string psProfile = "ps_4_0_level_9_1") => $$"""
        #if defined(OPENGL)
        #define SV_POSITION POSITION
        #endif
        float4 Tint = float4(1, 0, 0, 1);
        struct VsOut { float4 Pos : SV_POSITION; float4 Color : COLOR0; float2 Tc : TEXCOORD0; };
        VsOut MainVS(float4 pos : POSITION0, float4 color : COLOR0, float2 tc : TEXCOORD0)
        {
            VsOut o;
            o.Pos = float4(tc.x * 2 - 1, 1 - tc.y * 2, 0, 1);
            o.Color = color;
            o.Tc = tc;
            return o;
        }
        float4 MainPS(VsOut i) : COLOR0 { return Tint; }
        technique T { pass P0 { VertexShader = compile {{vsProfile}} MainVS(); PixelShader = compile {{psProfile}} MainPS(); } }
        """;

    private sealed record Expect(byte R, byte G, byte B, byte A, int Tolerance);

    /// <param name="ExpectRefusal">When set, <c>new Effect()</c> must THROW with a message containing this text.</param>
    private sealed record Job(string Name, byte[]? Bytes, string? CompileError, Expect Expected, float? SetR, string? ExpectRefusal = null)
    {
        /// <summary>When set, a reference build of the same source: it must load too and render every pixel identically.</summary>
        public byte[]? Reference { get; init; }

        /// <summary>When set, the compile must FAIL with this diagnostic code (nothing is loaded).</summary>
        public string? ExpectCompileError { get; init; }
    }

    private sealed record JobResult(string Name, bool Ok, string Detail);

    private static Job Compile(
        EffectCompiler compiler, string label, PlatformTarget target, EffectContainer container,
        string path, string source, string name, Expect expected, float? setR, string? expectRefusal = null)
    {
        var result = compiler.CompileAsync(source, new CompilerOptions
        {
            Target = target,
            Container = container,
            SourceFileName = path,
        }).GetAwaiter().GetResult();

        if (result.IsFailure)
            return new Job(name, null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")), expected, setR, expectRefusal);

        // A default that is preserved must not leave DXC's now-untrue warning behind.
        foreach (ShaderError w in result.Value.Warnings)
            if (w.Message.Contains("initializer ignored", StringComparison.Ordinal) && path.EndsWith("RedDefault.fx", StringComparison.Ordinal))
                return new Job(name, null, $"unexpected warning: {w.Code}: {w.Message}", expected, setR, expectRefusal);

        Console.WriteLine($"[{label}] compiled {name}: {result.Value.Data.Length} bytes");
        return new Job(name, result.Value.Data, null, expected, setR, expectRefusal);
    }

    private sealed class ProbeGame : Game
    {
        private const int Size = 64;
        private readonly IReadOnlyList<Job> _jobs;
        private bool _done;

        public List<JobResult> Results { get; } = [];

        public ProbeGame(IReadOnlyList<Job> jobs, GraphicsProfile profile)
        {
            _jobs = jobs;
            _ = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = Size,
                PreferredBackBufferHeight = Size,
                GraphicsProfile = profile,
            };
            Window.Title = "ShadowDusk initializer defaults (headless)";
        }

        protected override void Draw(GameTime gameTime)
        {
            if (_done) { Exit(); return; }
            _done = true;
            try { RunAll(); }
            catch (Exception ex) { Results.Add(new JobResult("FATAL", false, ex.ToString())); }
            Exit();
        }

        private void RunAll()
        {
            using var sb = new SpriteBatch(GraphicsDevice);
            using var white = new Texture2D(GraphicsDevice, 1, 1);
            white.SetData([Color.White]);

            foreach (Job job in _jobs)
            {
                if (job.ExpectCompileError is { } code)
                {
                    bool failedAsExpected = job.Bytes is null && job.CompileError is { } err
                        && err.StartsWith(code + ":", StringComparison.Ordinal);
                    Results.Add(new JobResult(job.Name, failedAsExpected, job.Bytes is null
                        ? (failedAsExpected ? "refused as expected: " : "compile failed with the wrong error: ") + job.CompileError
                        : $"compiled ({job.Bytes.Length} bytes), but the compile must fail with {code}"));
                    continue;
                }

                if (job.Bytes is null)
                {
                    Results.Add(new JobResult(job.Name, false, "compile failed: " + job.CompileError));
                    continue;
                }

                Effect effect;
                try { effect = new Effect(GraphicsDevice, job.Bytes); }
                catch (Exception ex)
                {
                    bool refused = job.ExpectRefusal is { } want && ex.Message.Contains(want, StringComparison.Ordinal);
                    Results.Add(new JobResult(job.Name, refused,
                        (refused ? "refused as expected: " : "new Effect() threw: ") + ex.Message));
                    continue;
                }

                if (job.ExpectRefusal is { } expectedRefusal)
                {
                    Results.Add(new JobResult(job.Name, false,
                        $"new Effect() loaded, but the {GraphicsDevice.GraphicsProfile} device must refuse it: \"{expectedRefusal}\""));
                    effect.Dispose();
                    continue;
                }

                try
                {
                    // The parameter is NEVER Set unless the case says so: the value is the default.
                    if (job.SetR is { } r)
                        effect.Parameters["R"]!.SetValue(r);

                    Color[] px = Render(sb, white, effect);
                    Color c = px[(Size / 2) * Size + Size / 2];
                    Expect e = job.Expected;
                    bool ok = Math.Abs(c.R - e.R) <= e.Tolerance && Math.Abs(c.G - e.G) <= e.Tolerance
                           && Math.Abs(c.B - e.B) <= e.Tolerance && Math.Abs(c.A - e.A) <= e.Tolerance;
                    string detail = $"centre RGBA=({c.R},{c.G},{c.B},{c.A}) expected ({e.R},{e.G},{e.B},{e.A}) +-{e.Tolerance}";

                    if (job.Reference is { } reference)
                    {
                        // The reference build (KNI's own MGCB) must load on this device too and draw the same image.
                        using var refEffect = new Effect(GraphicsDevice, reference);
                        Color[] want = Render(sb, white, refEffect);
                        int maxd = 0;
                        for (int k = 0; k < px.Length; k++)
                        {
                            maxd = Math.Max(maxd, Math.Max(Math.Abs(px[k].R - want[k].R), Math.Abs(px[k].G - want[k].G)));
                            maxd = Math.Max(maxd, Math.Max(Math.Abs(px[k].B - want[k].B), Math.Abs(px[k].A - want[k].A)));
                        }
                        ok &= maxd == 0;
                        detail += $"; vs KNI's own build: {px.Length} px, maxd {maxd}";
                    }
                    Results.Add(new JobResult(job.Name, ok, detail));
                }
                catch (Exception ex)
                {
                    try { sb.End(); } catch { /* may not be in a batch */ }
                    try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
                    Results.Add(new JobResult(job.Name, false, $"render threw: {ex.GetType().Name}: {ex.Message}"));
                }
                finally
                {
                    effect.Dispose();
                }
            }
        }

        /// <summary>Draws a full-target SpriteBatch quad through <paramref name="effect"/> and reads every pixel back.</summary>
        private Color[] Render(SpriteBatch sb, Texture2D white, Effect effect)
        {
            // A fresh target per draw: after GetData, MonoGame DesktopVK leaves the image in a transfer
            // layout, and reusing it as a render-pass attachment is a validation-layer error
            // (VUID-vkCmdBeginRenderPass-initialLayout-00900); EffectImageRenderer avoids it the same way.
            using var rt = new RenderTarget2D(GraphicsDevice, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);
            // Prime SpriteBatch's sprite vertex shader (the effects are pixel-only), as EffectImageRenderer does.
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            sb.Draw(white, new Rectangle(0, 0, Size, Size), Color.White);
            sb.End();
            GraphicsDevice.Clear(new Color(255, 0, 255, 255));   // magenta: an unwritten pixel is obvious
            sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, effect);
            sb.Draw(white, new Rectangle(0, 0, Size, Size), Color.White);
            sb.End();
            GraphicsDevice.SetRenderTarget(null);

            var px = new Color[Size * Size];
            rt.GetData(px);
            return px;
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "ShadowDusk.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("repo root (ShadowDusk.slnx) not found above " + AppContext.BaseDirectory);
    }
}
