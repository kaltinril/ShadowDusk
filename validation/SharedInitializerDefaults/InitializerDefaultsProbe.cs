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
    public static int Run(string label, PlatformTarget target, EffectContainer[] containers, bool mgfxcOpenGlControl, bool skipFloat2x2 = false)
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

        using var game = new ProbeGame(jobs);
        game.Run();

        int failures = 0;
        Console.WriteLine($"[{label}] results:");
        foreach (JobResult r in game.Results)
        {
            Console.WriteLine($"  [{(r.Ok ? "PASS" : "FAIL")}] {r.Name,-52} {r.Detail}");
            if (!r.Ok) failures++;
        }

        if (game.Results.Count != jobs.Count)
        {
            Console.WriteLine($"[{label}] FAIL: only {game.Results.Count} of {jobs.Count} cases ran");
            return 1;
        }

        Console.WriteLine($"[{label}] {game.Results.Count - failures}/{game.Results.Count} passed.");
        return failures == 0 ? 0 : 1;
    }

    private sealed record Expect(byte R, byte G, byte B, byte A, int Tolerance);

    private sealed record Job(string Name, byte[]? Bytes, string? CompileError, Expect Expected, float? SetR);

    private sealed record JobResult(string Name, bool Ok, string Detail);

    private static Job Compile(
        EffectCompiler compiler, string label, PlatformTarget target, EffectContainer container,
        string path, string source, string name, Expect expected, float? setR)
    {
        var result = compiler.CompileAsync(source, new CompilerOptions
        {
            Target = target,
            Container = container,
            SourceFileName = path,
        }).GetAwaiter().GetResult();

        if (result.IsFailure)
            return new Job(name, null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")), expected, setR);

        // A default that is preserved must not leave DXC's now-untrue warning behind.
        foreach (ShaderError w in result.Value.Warnings)
            if (w.Message.Contains("initializer ignored", StringComparison.Ordinal) && path.EndsWith("RedDefault.fx", StringComparison.Ordinal))
                return new Job(name, null, $"unexpected warning: {w.Code}: {w.Message}", expected, setR);

        Console.WriteLine($"[{label}] compiled {name}: {result.Value.Data.Length} bytes");
        return new Job(name, result.Value.Data, null, expected, setR);
    }

    private sealed class ProbeGame : Game
    {
        private const int Size = 64;
        private readonly IReadOnlyList<Job> _jobs;
        private bool _done;

        public List<JobResult> Results { get; } = [];

        public ProbeGame(IReadOnlyList<Job> jobs)
        {
            _jobs = jobs;
            _ = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = Size,
                PreferredBackBufferHeight = Size,
                GraphicsProfile = GraphicsProfile.HiDef,
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
            using var rt = new RenderTarget2D(GraphicsDevice, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);

            foreach (Job job in _jobs)
            {
                if (job.Bytes is null)
                {
                    Results.Add(new JobResult(job.Name, false, "compile failed: " + job.CompileError));
                    continue;
                }

                Effect effect;
                try { effect = new Effect(GraphicsDevice, job.Bytes); }
                catch (Exception ex)
                {
                    Results.Add(new JobResult(job.Name, false, "new Effect() threw: " + ex.Message));
                    continue;
                }

                try
                {
                    // The parameter is NEVER Set unless the case says so: the value is the default.
                    if (job.SetR is { } r)
                        effect.Parameters["R"]!.SetValue(r);

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
                    Color c = px[(Size / 2) * Size + Size / 2];
                    Expect e = job.Expected;
                    bool ok = Math.Abs(c.R - e.R) <= e.Tolerance && Math.Abs(c.G - e.G) <= e.Tolerance
                           && Math.Abs(c.B - e.B) <= e.Tolerance && Math.Abs(c.A - e.A) <= e.Tolerance;
                    Results.Add(new JobResult(job.Name, ok,
                        $"centre RGBA=({c.R},{c.G},{c.B},{c.A}) expected ({e.R},{e.G},{e.B},{e.A}) +-{e.Tolerance}"));
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
