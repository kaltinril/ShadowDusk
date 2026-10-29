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

/// <summary>
/// One shader and the pixel its default value must produce. <paramref name="OnDirectX"/>, when set, is the
/// pixel on the DirectX targets instead (a shape whose default DirectX refuses with SD0017).
/// </summary>
internal sealed record DefaultCase(string Fx, bool GlSupported, byte R, byte G, byte B, byte A, int Tolerance = 1,
    (byte R, byte G, byte B, byte A)? OnDirectX = null);

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
        // float2x3 + float3x2, every element distinct (issue #423: Vulkan reflected these transposed and
        // shipped zeros, which renders black). Non-square matrices: not on GL, SD0210.
        new("G_NonSquare.fx",  false, 191, 128, 64, 255),
        // MatrixDefault.fx declared row_major, then under #pragma pack_matrix(row_major), then a row_major
        // float2x2 (off GL). A row_major buffer holds one register per row, so the blob must be the rows in
        // order; the column-by-column blob lands transposed and renders (64,0,0) (issue #423 follow-up).
        new("H_RowMajorMatrix.fx",   true,  0, 128, 0, 255),
        new("I_PackMatrixPragma.fx", true,  0, 128, 0, 255),
        new("J_RowMajor2x2.fx",      false, 0, 128, 0, 255),
        // TALL row_major float4x3: on DirectX MonoGame's upload reaches rows 0..2 only. A zero fourth row
        // (an affine matrix) is kept, symmetric or not; a non-zero one is SD0017 and zeros there, black.
        new("K_RowMajorTallSymmetric.fx",  false, 64, 128, 191, 255),
        new("L_RowMajorTallAsymmetric.fx", false, 32, 128, 32, 255),
        new("M_RowMajorTallFourthRow.fx",  false, 64, 128, 191, 255, OnDirectX: (0, 0, 0, 255)),
        // An ARRAY of tall row_major float4x3: on DirectX each element is uploaded 3 registers after the last
        // over a 4-register stride, so it is SD0017 and zeros there (black); Vulkan advances 4.
        new("N_RowMajorTallArray.fx",      false, 32, 128, 32, 255, OnDirectX: (0, 0, 0, 255)),
    ];

    /// <summary>
    /// Runs the probe. <paramref name="mgfxcOpenGlControl"/> is true on the GL runtimes: the committed
    /// mgfxc OpenGL build of RedDefault must render black. C_Vec2Mat2 runs on DesktopVK too: it used to throw
    /// there with or without initializers, because ShadowDusk's Vulkan reflection sized a buffer ending in a
    /// float2x2 a register short (mgfxc's Vulkan build sizes it right) and MonoGame's upload ran past its end;
    /// fixed in SpirvReflectionParser.
    /// </summary>
    public static int Run(string label, PlatformTarget target, EffectContainer[] containers, bool mgfxcOpenGlControl)
    {
        WarnIfMapCountLow(label, target);
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
                string path = Path.Combine(dir, c.Fx);
                jobs.Add(Compile(compiler, label, target, container, path, File.ReadAllText(path),
                    $"{Path.GetFileNameWithoutExtension(c.Fx)} [{container}]",
                    target is PlatformTarget.DirectX or PlatformTarget.DirectX12 && c.OnDirectX is { } dx
                        ? new Expect(dx.R, dx.G, dx.B, dx.A, c.Tolerance)
                        : new Expect(c.R, c.G, c.B, c.A, c.Tolerance), setR: null));
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

    /// <summary>
    /// On Linux, warns when <c>vm.max_map_count</c> is too low for a Vulkan run on Mesa lavapipe. Measured
    /// 2026-10-28 (Mesa 25.2.8, MonoGame 3.8.5 DesktopVK): MonoGame's <c>MGG_Shader_Create</c> allocates about
    /// 13,500 descriptor sets per shader and never frees them, and lavapipe backs each set with its own buffer
    /// mapping, so a process holds ~13,500 more mappings per shader created. At the kernel's limit lavapipe's
    /// <c>lvp_descriptor_set_create</c> does not check the failed map and segfaults in <c>memset</c> (exit 139),
    /// minutes into the run with no managed output. 262,144 (GitHub's ubuntu image) is ~19 shaders; 65,530 (the
    /// kernel default) ~4. A GPU driver does not map per set. The CI job raises the limit.
    /// </summary>
    private static void WarnIfMapCountLow(string label, PlatformTarget target)
    {
        const long Needed = 1_048_576;
        if (target != PlatformTarget.Vulkan || !OperatingSystem.IsLinux()) return;
        try
        {
            if (long.TryParse(File.ReadAllText("/proc/sys/vm/max_map_count").Trim(), out long limit) && limit < Needed)
                Console.WriteLine($"[{label}] WARNING: vm.max_map_count is {limit}. On Mesa lavapipe MonoGame DesktopVK " +
                    $"holds ~13,500 mappings per shader, so this run may segfault (exit 139) after ~{limit / 13_500} shaders. " +
                    $"Raise it first: sudo sysctl -w vm.max_map_count=2097152");
        }
        catch (IOException) { /* not readable: nothing to say */ }
        catch (UnauthorizedAccessException) { }
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

            foreach (Job job in _jobs)
            {
                // Flushed before each job, so a native crash (no managed results) still names the case it was in.
                Console.WriteLine($"[probe] drawing {job.Name}");
                Console.Out.Flush();
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

                    // A fresh target per job: after GetData, MonoGame DesktopVK leaves the image in a transfer
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
