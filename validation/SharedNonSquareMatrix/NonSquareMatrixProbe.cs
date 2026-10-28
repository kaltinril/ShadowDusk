#nullable enable

// Real-engine probe for issue #423: do non-square matrix parameters reach the shader in the right
// registers when a C# Matrix is Set through EffectParameter?
//
// MonoGame's upload path (ConstantBuffer.SetParameter, shared by every backend) copies a matrix
// parameter as `ColumnCount` registers of `RowCount` floats from `Data`, which SetValue(Matrix)
// fills column-major from the header's Rows x Columns. On DirectX the header is the HLSL shape, so
// the registers are the HLSL columns. On Vulkan, real mgfxc 3.8.5 writes the header transposed;
// whether that lands the values in the wrong registers on real DesktopVK is what this measures.
//
// Each case is a pixel-only effect that writes row i of the matrix to pixel i (a one-hot row
// vector times M), so every element of the matrix lands in its own pixel channel. Every value Set
// is k/32 for a distinct integer k, so a read-back channel decodes back to the element index k
// (0 = the register was never written). The CPU expectation is MonoGame's OWN DirectX semantics
// (SetValue's column-major fill, SetParameter's register copy, HLSL's default column-major
// packing), which the WindowsDX run of the real mgfxc DirectX_11 build confirms.
//
// A second section draws two quads through the stock MonoGame SkinnedEffect (`float4x3 Bones[72]`),
// each skinned 100% to one bone with a rotation + translation, and reports where they landed
// against the CPU-transformed centre.
//
// Builds come from directories of prebuilt `<Case>.mgfx` files (label=dir on the command line) plus
// an in-process ShadowDusk compile of the in-tree compiler ("sd-intree"). Exit 0 iff the in-tree
// ShadowDusk arm matches the expectation on every gated case (the stock SkinnedEffect and the
// prebuilt arms are reported, never gated). See docs/validation-matrix.md section 6.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace ShadowDusk.Validation.NonSquareMatrix;

/// <summary>One probe: the shader, how many pixels it writes, how to Set it, and the expected channels.</summary>
internal sealed record ProbeCase(string Name, string Fx, int Width, Action<Effect> Set, float[] Expected, bool Gated, string Note);

/// <summary>One build of the cases: a label and either a directory of prebuilt .mgfx files or an in-process target.</summary>
public sealed record ProbeBuild(string Label, string? Directory, PlatformTarget? InProcessTarget);

public static class NonSquareMatrixProbe
{
    private const float Step = 1f / 32f;

    // K(r, c): the element index Set into HLSL M[r][c] by SetValue(Matrix) under MonoGame's DirectX
    // semantics, i.e. Matrix.M{r+1}{c+1} with Mrc = (4r + c + 1) / 32.
    private static int K(int r, int c) => 4 * r + c + 1;

    private static Matrix KMatrix(int offset = 0)
    {
        var v = new float[16];
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                v[4 * r + c] = (K(r, c) + offset) * Step;
        return new Matrix(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
    }

    private static float[] Px(params int[][] pixels) =>
        pixels.SelectMany(p => p.Select(k => k < 0 ? 1f : k * Step)).ToArray();   // -1 = 1.0 (alpha)

    private static int[] P(params int[] k) => k;

    internal static ProbeCase[] Cases()
    {
        Matrix m0 = KMatrix();
        Matrix m1 = KMatrix(16);
        return
        [
            new("A float4x3 SetValue(Matrix)", "A_F4x3", 4, e => e.Parameters["M"]!.SetValue(m0),
                Px(P(K(0, 0), K(0, 1), K(0, 2), -1), P(K(1, 0), K(1, 1), K(1, 2), -1), P(K(2, 0), K(2, 1), K(2, 2), -1), P(K(3, 0), K(3, 1), K(3, 2), -1)),
                true, "row 3 = Matrix.M41..M43 (translation)"),
            new("B float3x2 SetValue(Matrix)", "B_F3x2", 3, e => e.Parameters["M"]!.SetValue(m0),
                Px(P(K(0, 0), K(0, 1), 0, -1), P(K(1, 0), K(1, 1), 0, -1), P(K(2, 0), K(2, 1), 0, -1)), true, ""),
            new("C float2x3 SetValue(Matrix)", "C_F2x3", 2, e => e.Parameters["M"]!.SetValue(m0),
                Px(P(0, 0, 0, -1), P(0, 0, 0, -1)), true, "SetValue(Matrix) has no 2-row case: a no-op on every MonoGame backend"),
            new("D row_major float4x3 SetValue(Matrix)", "D_RowMajor4x3", 4, e => e.Parameters["M"]!.SetValue(m0),
                Px(P(K(0, 0), K(0, 1), K(0, 2), -1), P(K(1, 0), K(1, 1), K(1, 2), -1), P(K(2, 0), K(2, 1), K(2, 2), -1), P(K(3, 0), K(3, 1), K(3, 2), -1)),
                false, "informational: mgfxc DirectX_11 refuses the shape, so there is no DX reference"),
            new("E float4x3 B[2] SetValue(Matrix[])", "E_Array4x3", 8, e => e.Parameters["B"]!.SetValue(new[] { m0, m1 }),
                Px(P(K(0, 0), K(0, 1), K(0, 2), -1), P(K(1, 0), K(1, 1), K(1, 2), -1), P(K(2, 0), K(2, 1), K(2, 2), -1), P(K(3, 0), K(3, 1), K(3, 2), -1),
                   P(K(0, 0) + 16, K(0, 1) + 16, K(0, 2) + 16, -1), P(K(1, 0) + 16, K(1, 1) + 16, K(1, 2) + 16, -1), P(K(2, 0) + 16, K(2, 1) + 16, K(2, 2) + 16, -1), P(K(3, 0) + 16, K(3, 1) + 16, K(3, 2) + 16, -1)),
                true, "the SkinnedEffect Bones shape"),
            new("F float4x3 + float4 G", "F_F4x3Guard", 5, e => { e.Parameters["M"]!.SetValue(m0); e.Parameters["G"]!.SetValue(new Vector4(25, 26, 27, 28) * Step); },
                Px(P(K(0, 0), K(0, 1), K(0, 2), -1), P(K(1, 0), K(1, 1), K(1, 2), -1), P(K(2, 0), K(2, 1), K(2, 2), -1), P(K(3, 0), K(3, 1), K(3, 2), -1), P(25, 26, 27, 28)),
                true, "pixel 4 = G"),
            // SetValue(Matrix) has no 2x2 case and SetValue(float[]) is for arrays, so M cannot be Set: it stays
            // zero. The case is the buffer size: the first Apply uploads every parameter, M's last register included.
            new("G float2 V; float2x2 M (last)", "G_V2M2", 2, e => e.Parameters["V"]!.SetValue(new Vector2(29, 30) * Step),
                Px(P(29, 30, 0, 0), P(29, 30, 0, 0)), true, "buffer size (M is uploaded zero on the first Apply)"),
            new("H float3 V; float3x2 M (last)", "H_V3M32", 3, e => { e.Parameters["V"]!.SetValue(new Vector3(29, 30, 31) * Step); e.Parameters["M"]!.SetValue(m0); },
                Px(P(K(0, 0), K(0, 1), 29, -1), P(K(1, 0), K(1, 1), 30, -1), P(K(2, 0), K(2, 1), 31, -1)), true, "buffer size"),
            new("I float4 Pad; row_major float2x3 M (last)", "I_PadRowMajor2x3", 3, e => e.Parameters["Pad"]!.SetValue(new Vector4(25, 26, 27, 28) * Step),
                Px(P(0, 0, 0, -1), P(0, 0, 0, -1), P(25, 26, 27, 28)), false,
                "informational buffer size: M cannot be Set (2 rows) and mgfxc DirectX_11 refuses row_major; does the first upload throw?"),
        ];
    }

    public static int Run(string runtime, PlatformTarget inProcessTarget, string[] args)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(240_000);
            Console.WriteLine($"[{runtime}] WATCHDOG: killing");
            Environment.Exit(99);
        }) { IsBackground = true };
        watchdog.Start();

        string root = FindRepoRoot();
        string shaders = Path.Combine(root, "validation", "SharedNonSquareMatrix", "shaders");
        string outDir = Path.Combine(root, "validation", "output", "nonsquare-matrix", runtime);
        Directory.CreateDirectory(outDir);

        var builds = new List<ProbeBuild> { new("sd-intree", null, inProcessTarget) };
        foreach (string a in args)
        {
            int eq = a.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"expected label=dir, got '{a}'");
            builds.Add(new ProbeBuild(a[..eq], Path.GetFullPath(a[(eq + 1)..]), null));
        }

        ProbeCase[] cases = Cases();
        var compiler = new EffectCompiler();
        var jobs = new List<Job>();
        foreach (ProbeBuild b in builds)
        {
            foreach (ProbeCase c in cases)
            {
                byte[]? bytes = null;
                string? error = null;
                if (b.InProcessTarget is { } t)
                {
                    string path = Path.Combine(shaders, c.Fx + ".fx");
                    var r = compiler.CompileAsync(File.ReadAllText(path), new CompilerOptions { Target = t, SourceFileName = path }).GetAwaiter().GetResult();
                    if (r.IsFailure) error = string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}"));
                    else bytes = r.Value.Data.ToArray();
                }
                else
                {
                    string path = Path.Combine(b.Directory!, c.Fx + ".mgfx");
                    if (File.Exists(path)) bytes = File.ReadAllBytes(path);
                    else error = "no build (" + path + ")";
                }
                jobs.Add(new Job(b.Label, c, bytes, error));
            }
        }

        using var game = new ProbeGame(jobs, outDir);
        game.Run();

        var sb = new StringBuilder();
        sb.AppendLine($"[{runtime}] decoded k per pixel (channel value * 32; '.' = 0, '-' = 1.0 alpha). Expected = MonoGame DirectX semantics.");
        int gateFailures = 0;
        foreach (ProbeCase c in cases)
        {
            sb.AppendLine($"  {c.Name}  [{c.Fx}.fx]{(c.Gated ? "" : "  (informational)")}{(c.Note.Length > 0 ? "  -- " + c.Note : "")}");
            sb.AppendLine($"    {"expected",-14} {Decode(c.Expected.Select(v => (byte)Math.Round(v * 255)).ToArray(), c.Width)}");
            foreach (JobResult r in game.Results.Where(r => r.Job.Case == c))
            {
                string verdict = r.Pixels is null ? "ERROR" : r.Match ? "MATCH" : "DIFF";
                sb.AppendLine($"    {r.Job.Build,-14} {(r.Pixels is null ? r.Detail : Decode(r.Pixels, c.Width))}  {verdict}{(r.Pixels is not null && !r.Match ? "  raw " + Raw(r.Pixels, c.Width) : "")}");
                if (r.Job.Build == "sd-intree" && c.Gated && !r.Match) gateFailures++;
            }
        }

        // Informational, never gated: the stock SkinnedEffect is the engine's own build, not ShadowDusk output.
        sb.AppendLine($"[{runtime}] stock SkinnedEffect (WeightsPerVertex=1), two 12x6 quads, each 100% on one bone (informational: the engine's own effect):");
        foreach (string line in game.SkinnedReport) sb.AppendLine("    " + line);

        if (game.Results.Count != jobs.Count)
        {
            sb.AppendLine($"[{runtime}] FAIL: only {game.Results.Count} of {jobs.Count} jobs ran");
            gateFailures++;
        }

        Console.Write(sb.ToString());
        File.WriteAllText(Path.Combine(outDir, "report.txt"), sb.ToString());
        Console.WriteLine(gateFailures == 0
            ? $"[{runtime}] PASS: the in-tree ShadowDusk build matches the expectation on every gated case."
            : $"[{runtime}] FAIL: {gateFailures} gated failure(s) of the in-tree ShadowDusk build.");
        return gateFailures == 0 ? 0 : 1;
    }

    private static string Decode(byte[] px, int width)
    {
        var parts = new List<string>();
        for (int i = 0; i < width; i++)
        {
            var ch = new string[4];
            for (int j = 0; j < 4; j++)
            {
                byte b = px[i * 4 + j];
                ch[j] = b == 255 && j == 3 ? "-" : b == 0 ? "." : (b * 32 / 255.0) is var k && Math.Abs(k - Math.Round(k)) < 0.1 ? ((int)Math.Round(k)).ToString() : $"?{b}";
            }
            parts.Add("(" + string.Join(",", ch) + ")");
        }
        return string.Join(" ", parts);
    }

    private static string Raw(byte[] px, int width) =>
        string.Join(" ", Enumerable.Range(0, width).Select(i => $"[{px[i * 4]},{px[i * 4 + 1]},{px[i * 4 + 2]},{px[i * 4 + 3]}]"));

    private sealed record Job(string Build, ProbeCase Case, byte[]? Bytes, string? Error);

    private sealed record JobResult(Job Job, byte[]? Pixels, bool Match, string Detail);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct SkinVertex(Vector3 position, Byte4 indices) : IVertexType
    {
        public readonly Vector3 Position = position;
        public readonly Vector3 Normal = Vector3.UnitZ;
        public readonly Vector2 TexCoord = Vector2.Zero;
        public readonly Byte4 Indices = indices;
        public readonly Vector4 Weights = new(1, 0, 0, 0);

        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(32, VertexElementFormat.Byte4, VertexElementUsage.BlendIndices, 0),
            new VertexElement(36, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0));

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    private sealed class ProbeGame : Game
    {
        private const int SkinSize = 64;
        private readonly IReadOnlyList<Job> _jobs;
        private readonly string _outDir;
        private bool _done;

        public List<JobResult> Results { get; } = [];
        public List<string> SkinnedReport { get; } = [];
        public bool SkinnedOk { get; private set; }

        public ProbeGame(IReadOnlyList<Job> jobs, string outDir)
        {
            _jobs = jobs;
            _outDir = outDir;
            _ = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = SkinSize,
                PreferredBackBufferHeight = SkinSize,
                GraphicsProfile = GraphicsProfile.HiDef,
            };
            Window.Title = "ShadowDusk non-square matrix probe (headless)";
        }

        protected override void Draw(GameTime gameTime)
        {
            if (_done) { Exit(); return; }
            _done = true;
            try { RunCases(); }
            catch (Exception ex) { Console.WriteLine("FATAL in cases: " + ex); }
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            try { RunSkinned(); }
            catch (Exception ex) { SkinnedReport.Add("FATAL: " + ex); }
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            Exit();
        }

        private void RunCases()
        {
            using var sb = new SpriteBatch(GraphicsDevice);
            using var white = new Texture2D(GraphicsDevice, 1, 1);
            white.SetData([Color.White]);

            foreach (Job job in _jobs)
            {
                if (job.Bytes is null) { Results.Add(new JobResult(job, null, false, job.Error ?? "no bytes")); continue; }

                Effect effect;
                try { effect = new Effect(GraphicsDevice, job.Bytes); }
                catch (Exception ex) { Results.Add(new JobResult(job, null, false, "new Effect() threw: " + ex.Message)); continue; }

                int w = job.Case.Width;
                try
                {
                    job.Case.Set(effect);
                    // A fresh target per job (DesktopVK leaves a read-back image in a transfer layout).
                    using var rt = new RenderTarget2D(GraphicsDevice, w, 2, false, SurfaceFormat.Color, DepthFormat.None);
                    GraphicsDevice.SetRenderTarget(rt);
                    GraphicsDevice.Clear(Color.Transparent);
                    // Prime SpriteBatch's sprite vertex shader (the effects are pixel-only).
                    sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                    sb.Draw(white, new Rectangle(0, 0, w, 2), Color.White);
                    sb.End();
                    GraphicsDevice.Clear(new Color(255, 0, 255, 255));   // magenta: an unwritten pixel is obvious
                    sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, null, null, effect);
                    sb.Draw(white, new Rectangle(0, 0, w, 2), Color.White);
                    sb.End();
                    GraphicsDevice.SetRenderTarget(null);

                    var px = new Color[w * 2];
                    rt.GetData(px);
                    var bytes = new byte[w * 4];
                    for (int i = 0; i < w; i++)
                    {
                        Color c = px[i];
                        bytes[i * 4] = c.R; bytes[i * 4 + 1] = c.G; bytes[i * 4 + 2] = c.B; bytes[i * 4 + 3] = c.A;
                    }
                    bool match = true;
                    for (int i = 0; i < bytes.Length; i++)
                        if (Math.Abs(bytes[i] - (int)Math.Round(job.Case.Expected[i] * 255)) > 1) match = false;
                    Results.Add(new JobResult(job, bytes, match, ""));
                }
                catch (Exception ex)
                {
                    try { sb.End(); } catch { /* may not be in a batch */ }
                    try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
                    Results.Add(new JobResult(job, null, false, $"render threw {ex.GetType().Name}: {ex.Message}"));
                }
                finally { effect.Dispose(); }
            }
        }

        private void RunSkinned()
        {
            using var white = new Texture2D(GraphicsDevice, 1, 1);
            white.SetData([Color.White]);
            using var fx = new SkinnedEffect(GraphicsDevice)
            {
                WeightsPerVertex = 1,
                World = Matrix.Identity,
                View = Matrix.Identity,
                Projection = Matrix.CreateOrthographicOffCenter(0, SkinSize, SkinSize, 0, -1, 1),
                Texture = white,
                AmbientLightColor = Vector3.Zero,
                DiffuseColor = Vector3.Zero,
                SpecularColor = Vector3.Zero,
                PreferPerPixelLighting = false,
                FogEnabled = false,
            };
            fx.DirectionalLight0.Enabled = false;
            fx.DirectionalLight1.Enabled = false;
            fx.DirectionalLight2.Enabled = false;

            Matrix bone0 = Matrix.CreateRotationZ(MathHelper.ToRadians(30)) * Matrix.CreateTranslation(20, 14, 0);
            Matrix bone1 = Matrix.CreateRotationZ(MathHelper.ToRadians(-45)) * Matrix.CreateTranslation(44, 44, 0);
            fx.SetBoneTransforms([bone0, bone1]);

            using var rt = new RenderTarget2D(GraphicsDevice, SkinSize, SkinSize, false, SurfaceFormat.Color, DepthFormat.None);
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Black);
            GraphicsDevice.BlendState = BlendState.Opaque;
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            GraphicsDevice.SamplerStates[0] = SamplerState.PointClamp;
            var idx = new short[] { 0, 1, 2, 2, 1, 3 };
            DrawQuad(fx, 0, new Vector3(1, 0, 0), idx);
            DrawQuad(fx, 1, new Vector3(0, 1, 0), idx);
            GraphicsDevice.SetRenderTarget(null);

            var px = new Color[SkinSize * SkinSize];
            rt.GetData(px);
            WritePpm(Path.Combine(_outDir, "skinned.ppm"), px);

            bool ok = true;
            ok &= Report("bone 0 (red,   rot +30, T(20,14))", px, c => c.R > 128 && c.G < 64, new Vector2(20, 14));
            ok &= Report("bone 1 (green, rot -45, T(44,44))", px, c => c.G > 128 && c.R < 64, new Vector2(44, 44));
            SkinnedOk = ok;
            SkinnedReport.Add("image: " + Path.Combine(_outDir, "skinned.ppm"));
        }

        private void DrawQuad(SkinnedEffect fx, byte bone, Vector3 emissive, short[] idx)
        {
            fx.EmissiveColor = emissive;
            var b = new Byte4(bone, 0, 0, 0);
            SkinVertex[] v =
            [
                new(new Vector3(-6, -3, 0), b), new(new Vector3(6, -3, 0), b),
                new(new Vector3(-6, 3, 0), b), new(new Vector3(6, 3, 0), b),
            ];
            foreach (EffectPass pass in fx.CurrentTechnique.Passes)
            {
                pass.Apply();
                GraphicsDevice.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, v, 0, 4, idx, 0, 2, SkinVertex.Declaration);
            }
        }

        private bool Report(string name, Color[] px, Func<Color, bool> pick, Vector2 expected)
        {
            double sx = 0, sy = 0; int n = 0;
            for (int y = 0; y < SkinSize; y++)
                for (int x = 0; x < SkinSize; x++)
                    if (pick(px[y * SkinSize + x])) { sx += x + 0.5; sy += y + 0.5; n++; }
            if (n == 0) { SkinnedReport.Add($"{name}: NOTHING drawn (expected centre {expected.X},{expected.Y}, ~72 px)"); return false; }
            double cx = sx / n, cy = sy / n;
            double d = Math.Sqrt((cx - expected.X) * (cx - expected.X) + (cy - expected.Y) * (cy - expected.Y));
            bool ok = d < 1.0 && n >= 55 && n <= 90;
            SkinnedReport.Add($"{name}: {n} px, centre ({cx:F2},{cy:F2}) expected ({expected.X},{expected.Y}) ~72 px, off by {d:F2} px  {(ok ? "MATCH" : "WRONG")}");
            return ok;
        }

        private static void WritePpm(string path, Color[] px)
        {
            using var f = File.Create(path);
            byte[] header = Encoding.ASCII.GetBytes($"P6\n{SkinSize} {SkinSize}\n255\n");
            f.Write(header);
            foreach (Color c in px) { f.WriteByte(c.R); f.WriteByte(c.G); f.WriteByte(c.B); }
        }
    }

    /// <summary>The committed real mgfxc 3.8.5 builds of the cases for a profile (DirectX_11 or Vulkan).</summary>
    public static string CommittedGoldens(string profile) =>
        Path.Combine(FindRepoRoot(), "validation", "SharedNonSquareMatrix", "mgfxc-3.8.5-" + profile);

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
