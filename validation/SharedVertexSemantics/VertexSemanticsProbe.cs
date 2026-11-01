#nullable enable

// Rung-4 proof that vertex inputs beyond POSITION / COLOR / TEXCOORD / NORMAL bind in the REAL
// GL engine, and that a float3x3 interpolant reaches the pixel shader intact.
//
// Compiles tests/fixtures/golden/vertex-semantics/{SemanticsRender,TangentFrameRender,
// DeferredGBufferShape}.fx with ShadowDusk (OpenGL), loads the bytes into a real Effect (MonoGame
// DesktopGL or KNI SDL2.GL, whichever the including project references; the Microsoft.Xna.Framework
// API is the same), and draws a quad through a CUSTOM VertexDeclaration carrying one element per
// semantic: Position, Normal, Binormal, Tangent, BlendIndices (Byte4), BlendWeight 0 and 1,
// PointSize, Fog, Depth, TextureCoordinate 0 and 3, Color. Every element holds a distinct constant,
// and the effect's `Pick` / `Mode` parameter makes the output show exactly one of them, so each
// render is checked twice:
//   * against the mgfxc 3.8.4.1 OpenGL build of the same .fx (the committed golden beside it),
//     loaded and drawn in the same process on the same device: whole image, maxd <= 1;
//   * against the colour the CPU knows that element carries (+-2/255), so a binding mgfxc and
//     ShadowDusk got wrong the same way still fails.
// Matrix-interpolant PACKING (tests/fixtures/golden/vertex-semantics/packing/*.fx): the two stages
// declare one interpolant differently (a matrix written whole and read as separate vectors, or the
// reverse; column_major and row_major; 3x3, 4x3, 3x4, 2x2; a typedef; #pragma pack_matrix), so the
// render shows the register layout fxc gives it, which a shader declaring it the same way in both
// stages can never show. Each mode vs the mgfxc golden, maxd <= 1.
// Non-square interpolants (NonSquareStems): each used to leave a non-square GLSL type in one stage
// (SD0403), which strict GL front ends (Mesa, Apple, ANGLE) refused at load and MonoGame 3.8.6
// DesktopGL4 aborted on. Since issue #453 the GLSL rewrite holds them as zero-padded square matN
// (Rule 17), so every case must render and pass on EVERY driver, strict or lenient, and a compile
// that still warns SD0403 is a FAIL. The driver is classified from its GL_VENDOR / GL_RENDERER /
// GL_VERSION strings (printed in the log); the class only decides how a positive control may be
// detected. The summary counts the layout cases rendered, square and non-square, and the gate
// fails if either count is zero.
// Positive controls (each must turn red):
//   * ShadowDusk's own SemanticsRender bytes with the BINORMAL0 attribute record re-labelled
//     Tangent. The runtime then binds the Tangent element to it, so the Binormal render shows the
//     tangent's colour; if it did not, the gate could not see a wrong record.
//   * RowMatToVec33 and VecToRowMat33 compiled with `row_major` deleted, against the row_major
//     goldens: the column packing a row_major interpolant had before it was modelled. If these
//     matched, the packing cases could not see a column/row swap.
//   * Split1x4ToVec compiled with its float1x4 declared float4 (one register, the layout before
//     degenerate interpolants were split): must diverge from the split golden. That layout's VS
//     writes one varying where the PS reads four, so a strict driver's linker refuses the program
//     (Mesa: "varying ... not written by vertex shader"); there that link refusal is the detection.
// Exit code 0 iff every case passes and every control is detected. See docs/validation-matrix.md §6.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Color = Microsoft.Xna.Framework.Color;

namespace ShadowDusk.Validation.VertexSemantics;

public static class VertexSemanticsProbe
{
    private const int Size = 32;

    // The constants each vertex element carries (every vertex the same, so interpolation is exact).
    private static readonly Vector3 Normal = new(0.1f, 0.2f, 0.3f);
    private static readonly Vector3 Binormal = new(0.4f, 0.5f, 0.6f);
    private static readonly Vector3 Tangent = new(0.7f, 0.8f, 0.9f);
    private static readonly byte[] Indices = [1, 2, 3, 4];                 // Byte4, unnormalized
    private static readonly Vector4 Weights = new(0.15f, 0.25f, 0.35f, 0.45f);
    private const float PSize = 0.55f, Fog = 0.65f, Depth = 0.75f;
    private static readonly Vector2 Uv0 = new(0.5f, 0.5f);
    private static readonly Vector2 Uv3 = new(0.12f, 0.34f);
    private static readonly Vector4 Weights1 = new(0.9f, 0.7f, 0.5f, 0.3f);
    private static readonly Color VertexColor = new(32, 64, 96, 255);

    private static Vector4 Scalar(float s) => new(s, s * 0.5f, 1 - s, 1);

    // SemanticsRender.fx `Pick` -> what the colour must be.
    private static readonly (string Name, Vector4 Expected)[] Picks =
    [
        ("NORMAL0",       new(Normal, 1)),
        ("BINORMAL0",     new(Binormal, 1)),
        ("TANGENT0",      new(Tangent, 1)),
        ("BLENDINDICES0", new Vector4(Indices[0], Indices[1], Indices[2], Indices[3]) / 8f),
        ("BLENDWEIGHT0",  Weights),
        ("PSIZE0",        Scalar(PSize)),
        ("FOG0",          Scalar(Fog)),
        ("DEPTH0",        Scalar(Depth)),
        ("TEXCOORD3",     new(Uv3, 0.5f, 1)),
        ("BLENDWEIGHT1",  Weights1),
        ("COLOR0",        VertexColor.ToVector4()),
    ];

    // TangentFrameRender.fx `Mode` -> what the colour must be (Frame rows = T, B, N; v = (0.5, 0.25, 0.125)).
    private static readonly Vector3 V = new(0.5f, 0.25f, 0.125f);
    private static readonly (string Name, Vector4 Expected)[] Modes =
    [
        ("mul(v, Frame)", new(Tangent * V.X + Binormal * V.Y + Normal * V.Z, 1)),
        ("mul(Frame, v)", new(Vector3.Dot(Tangent, V), Vector3.Dot(Binormal, V), Vector3.Dot(Normal, V), 1)),
        ("TEXCOORD5 after the matrix", new Vector4(Normal.Z, Normal.Y, Normal.X, 1) + new Vector4(0.25f, 0.75f, 0, 0) * 0.0625f),
    ];

    public static int Run(string label, EffectContainer[] containers)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(240_000);
            Console.WriteLine($"[{label}] WATCHDOG: killing");
            Environment.Exit(99);
        }) { IsBackground = true };
        watchdog.Start();

        string dir = Path.Combine(FindRepoRoot(), "tests", "fixtures", "golden", "vertex-semantics");
        var compiler = new EffectCompiler();
        var jobs = new List<Job>();
        var setupFailures = new List<string>();

        byte[] Golden(string stem) => File.ReadAllBytes(Path.Combine(dir, stem + ".OpenGL.mgfx"));

        foreach (EffectContainer container in containers)
        {
            var semantics = Compile(compiler, label, container, Path.Combine(dir, "SemanticsRender.fx"));
            var frame = Compile(compiler, label, container, Path.Combine(dir, "TangentFrameRender.fx"));
            var gbuffer = Compile(compiler, label, container, Path.Combine(dir, "DeferredGBufferShape.fx"));
            for (int p = 0; p < Picks.Length; p++)
                jobs.Add(new Job($"{Picks[p].Name} [{container}]", semantics, Golden("SemanticsRender"), "Pick", p, Picks[p].Expected, ControlKind.None));
            for (int m = 0; m < Modes.Length; m++)
                jobs.Add(new Job($"float3x3 {Modes[m].Name} [{container}]", frame, Golden("TangentFrameRender"), "Mode", m, Modes[m].Expected, ControlKind.None));
            jobs.Add(new Job($"DeferredGBufferShape COLOR0 [{container}]", gbuffer, Golden("DeferredGBufferShape"), null, 0, null, ControlKind.None));

            foreach ((string stem, int modes) in Packing)
            {
                var packed = Compile(compiler, label, container, Path.Combine(dir, "packing", stem + ".fx"));
                byte[] golden = File.ReadAllBytes(Path.Combine(dir, "packing", stem + ".OpenGL.mgfx"));
                // No non-square GLSL type may survive (issue #453): a strict driver would refuse it.
                if (packed.Bytes is not null && packed.NonSquareGlsl)
                {
                    setupFailures.Add($"packing {stem} [{container}]: the compile warned SD0403 (a non-square GLSL matrix " +
                                      "survived the rewrite's Rule 17 lowering)");
                }
                for (int m = 0; m < modes; m++)
                    jobs.Add(new Job($"packing {stem} mode {m} [{container}]", packed, golden, modes > 1 ? "Mode" : null, m, null, ControlKind.None)
                    {
                        PackingStem = stem,
                    });
            }
            foreach ((string stem, string[] techniques) in PackingTechniques)
            {
                var packed = Compile(compiler, label, container, Path.Combine(dir, "packing", stem + ".fx"));
                byte[] golden = File.ReadAllBytes(Path.Combine(dir, "packing", stem + ".OpenGL.mgfx"));
                foreach (string technique in techniques)
                    jobs.Add(new Job($"packing {stem} technique {technique} [{container}]", packed, golden, null, 0, null, ControlKind.None)
                    {
                        PackingStem = stem,
                        Technique = technique,
                    });
            }

            if (container == EffectContainer.Mgfx && semantics.Bytes is not null)
            {
                byte[] patched = RelabelAttribute(semantics.Bytes, fromUsage: 4, toUsage: 5); // Binormal -> Tangent
                jobs.Add(new Job("CONTROL BINORMAL0 record relabelled Tangent (must diverge)",
                    new Compiled(patched, null), null, "Pick", 1, Picks[1].Expected, ControlKind.WrongElement));
            }
            if (container == EffectContainer.Mgfx)
            {
                foreach (string stem in new[] { "RowMatToVec33", "VecToRowMat33" })
                {
                    string path = Path.Combine(dir, "packing", stem + ".fx");
                    string source = File.ReadAllText(path);
                    string columns = source.Replace("row_major ", "", StringComparison.Ordinal);
                    if (columns == source) throw new InvalidOperationException($"{stem}.fx no longer says row_major");
                    var swapped = CompileSource(compiler, label, container, path, columns);
                    byte[] golden = File.ReadAllBytes(Path.Combine(dir, "packing", stem + ".OpenGL.mgfx"));
                    jobs.Add(new Job($"CONTROL {stem} packed by columns (must diverge)", swapped, golden, "Mode", 0, null, ControlKind.WrongPacking));
                }

                // The split layout of a degenerate float1x4: the same shader with the interpolant
                // declared float4 (one register, the layout before the split) must diverge.
                {
                    string path = Path.Combine(dir, "packing", "Split1x4ToVec.fx");
                    string source = File.ReadAllText(path);
                    string vector = source.Replace("float1x4", "float4", StringComparison.Ordinal);
                    if (vector == source) throw new InvalidOperationException("Split1x4ToVec.fx no longer says float1x4");
                    var single = CompileSource(compiler, label, container, path, vector);
                    byte[] golden = File.ReadAllBytes(Path.Combine(dir, "packing", "Split1x4ToVec.OpenGL.mgfx"));
                    jobs.Add(new Job("CONTROL Split1x4ToVec in one register (must diverge)", single, golden, null, 0, null, ControlKind.WrongPacking));
                }
            }
        }

        using var game = new ProbeGame(jobs);
        game.Run();

        Console.WriteLine($"[{label}] GL_VENDOR={game.GlVendor} | GL_RENDERER={game.GlRenderer} | GL_VERSION={game.GlVersion}");
        Console.WriteLine($"[{label}] driver class: {(game.StrictDriver ? "STRICT" : "lenient")} (every case must render and pass)");

        int failures = 0;
        Console.WriteLine($"[{label}] results:");
        foreach (JobResult r in game.Results)
        {
            string tag = r.Ok ? "PASS" : "FAIL";
            Console.WriteLine($"  [{tag}] {r.Name,-58} {r.Detail}");
            if (!r.Ok) failures++;
        }
        foreach (string f in setupFailures)
        {
            Console.WriteLine($"  [FAIL] {f}");
            failures++;
        }

        if (game.Results.Count != jobs.Count)
        {
            Console.WriteLine($"[{label}] FAIL: only {game.Results.Count} of {jobs.Count} cases ran");
            return 1;
        }

        var layout = game.Results.Where(r => r.PackingStem is not null).ToList();
        int squareRendered = layout.Count(r => r.Rendered && !NonSquareStems.Contains(r.PackingStem!));
        int nonSquareRendered = layout.Count(r => r.Rendered && NonSquareStems.Contains(r.PackingStem!));
        Console.WriteLine($"[{label}] layout (packing) cases: {layout.Count}, rendered {squareRendered + nonSquareRendered} " +
                          $"(square {squareRendered}, non-square {nonSquareRendered}).");
        if (squareRendered == 0 || nonSquareRendered == 0)
        {
            Console.WriteLine($"[{label}] FAIL: no {(squareRendered == 0 ? "square" : "non-square")} layout case was rendered, " +
                              "so nothing pinned that packing.");
            failures++;
        }

        Console.WriteLine($"[{label}] {game.Results.Count - failures}/{game.Results.Count} passed.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The packing fixtures with a non-square matrix interpolant: before issue #453 their GLSL held a
    /// non-square type (SD0403) that strict GL front ends refused. They must now render on every
    /// driver; listed only so the summary can show that some of them did.
    /// </summary>
    private static readonly HashSet<string> NonSquareStems = new(StringComparer.Ordinal)
    {
        "MatToVec43", "RowMatToVec43", "MatToVec34", "RowMatToVec34", "VecToMat43", "VecToRowMat43", "VecToMat34",
    };

    /// <summary>
    /// The runtime's program-link failure (MonoGame and KNI GL both throw it; KNI appends the
    /// driver's log, MonoGame prints it to the console). On a strict driver it is the only throw
    /// that counts as detecting a <see cref="ControlKind.WrongPacking"/> control: Mesa 25.2.8
    /// (llvmpipe) refuses the Split1x4ToVec control with "fragment shader varying vTexCoord2 not
    /// written by vertex shader". A compile failure or any other throw is still a missed control.
    /// </summary>
    private const string StrictLinkRefusalSignature = "Unable to link effect program";

    // tests/fixtures/golden/vertex-semantics/packing: fixture -> how many `Mode` values it has.
    private static readonly (string Stem, int Modes)[] Packing =
    [
        ("MatToVec33", 3), ("RowMatToVec33", 3), ("VecToMat33", 3), ("VecToRowMat33", 3),
        ("MatToVec43", 3), ("RowMatToVec43", 4), ("MatToVec34", 4), ("RowMatToVec34", 3),
        ("MatToVec22", 2), ("RowMatToVec22", 2), ("VecToMat43", 3), ("VecToRowMat43", 3),
        ("VecToMat34", 3), ("VecToRowMat22", 3), ("TypedefRowMatToVec33", 3),
        ("PragmaRowMajorMatToVec33", 3), ("DuplicatePosition", 1),
        ("RetValRowMatToVec33", 3), ("CommaDeclRowMat", 3), ("LeadingZeroRowMatToVec33", 3),
        ("RowMatToColMat33", 3), ("NestedStructRowMatToVec33", 3), ("UnusedDegenerateStruct", 1),
        ("Split1x4Both", 1), ("Split1x4ToVec", 1), ("SplitRow4x1Both", 1), ("VecToSplit1x3", 1),
        ("LocalStructShadow", 1), ("LocalTypedefShadow", 1),
    ];

    // Packing fixtures with more than one technique: fixture -> the techniques to draw.
    private static readonly (string Stem, string[] Techniques)[] PackingTechniques =
    [
        ("TwoTechniques", ["A", "B"]),
    ];

    private enum ControlKind
    {
        None,
        /// <summary>Must NOT show the expected element's colour.</summary>
        WrongElement,
        /// <summary>Must NOT match the mgfxc golden.</summary>
        WrongPacking,
    }

    private sealed record Compiled(byte[]? Bytes, string? Error)
    {
        /// <summary>
        /// The compile warned SD0403: the GLSL still holds a non-square matrix type, which no
        /// packing fixture may since issue #453.
        /// </summary>
        public bool NonSquareGlsl { get; init; }
    }

    private sealed record Job(
        string Name, Compiled Candidate, byte[]? Golden, string? Selector, float SelectorValue,
        Vector4? Expected, ControlKind Control)
    {
        /// <summary>The packing fixture this case renders, or null for any other case.</summary>
        public string? PackingStem { get; init; }

        /// <summary>The technique to draw, or null for the effect's first.</summary>
        public string? Technique { get; init; }
    }

    private sealed record JobResult(string Name, bool Ok, string Detail)
    {
        public string? PackingStem { get; init; }

        /// <summary>The candidate loaded and drew.</summary>
        public bool Rendered { get; init; }
    }

    private static Compiled Compile(EffectCompiler compiler, string label, EffectContainer container, string path)
        => CompileSource(compiler, label, container, path, File.ReadAllText(path));

    private static Compiled CompileSource(EffectCompiler compiler, string label, EffectContainer container, string path, string source)
    {
        var result = compiler.CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            Container = container,
            SourceFileName = path,
        }).GetAwaiter().GetResult();
        if (result.IsFailure)
            return new Compiled(null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
        Console.WriteLine($"[{label}] compiled {Path.GetFileName(path)} [{container}]: {result.Value.Data.Length} bytes");
        return new Compiled(result.Value.Data, null)
        {
            NonSquareGlsl = result.Value.Warnings.Any(w => w.Code == "SD0403"),
        };
    }

    /// <summary>
    /// Rewrites the usage byte of the one MGFX v10 vertex-attribute record whose usage is
    /// <paramref name="fromUsage"/> at index 0. A record is the name string (7-bit length +
    /// <c>vs_vN</c>), then usage, index and a 16-bit location.
    /// </summary>
    private static byte[] RelabelAttribute(byte[] mgfx, byte fromUsage, byte toUsage)
    {
        byte[] copy = (byte[])mgfx.Clone();
        int hits = 0;
        for (int i = 0; i + 8 < copy.Length; i++)
        {
            int len = copy[i];
            if (len < 5 || len > 7 || i + 1 + len + 2 > copy.Length) continue;
            string name = System.Text.Encoding.ASCII.GetString(copy, i + 1, len);
            if (!name.StartsWith("vs_v", StringComparison.Ordinal) || !name[4..].All(char.IsAsciiDigit)) continue;
            int usage = i + 1 + len;
            if (copy[usage] == fromUsage && copy[usage + 1] == 0)
            {
                copy[usage] = toUsage;
                hits++;
            }
            i = usage + 3;
        }
        if (hits != 1)
            throw new InvalidOperationException($"control setup: expected exactly one usage-{fromUsage} attribute record, found {hits}");

        // The runtime caches at three levels, and each would hand the control the unpatched
        // effect's binding (measured: the control first ran green-masked): Effect objects by the
        // MGFX header's effect key (bytes 6-9), linked programs and a declaration's attribute
        // binding by the shader's GLSL hash. So: a different header key, a same-length whitespace
        // change to the GLSL, and (in Render) a declaration instance of its own.
        if (copy.Length < 10 || copy[0] != (byte)'M' || copy[4] != 10)
            throw new InvalidOperationException("control setup: not an MGFX v10 container");
        copy[6] ^= 0x5A;
        byte[] from = System.Text.Encoding.ASCII.GetBytes("attribute vec4 vs_v0;");
        int at = IndexOf(copy, from);
        if (at < 0 || IndexOf(copy, from, at + 1) >= 0)
            throw new InvalidOperationException("control setup: expected exactly one 'attribute vec4 vs_v0;' declaration");
        copy[at + "attribute".Length] = (byte)'\t';
        return copy;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (int i = start; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SemanticVertex
    {
        public Vector4 Position;   //   0
        public Vector3 Normal;     //  16
        public Vector3 Binormal;   //  28
        public Vector3 Tangent;    //  40
        public uint Indices;       //  52  Byte4
        public Vector4 Weights;    //  56
        public float PSize;        //  72
        public float Fog;          //  76
        public float Depth;        //  80
        public Vector2 Uv0;        //  84
        public Vector2 Uv3;        //  92
        public Vector4 Weights1;   // 100
        public Color Color;        // 116  (120 bytes)

        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(0,   VertexElementFormat.Vector4, VertexElementUsage.Position, 0),
            new VertexElement(16,  VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(28,  VertexElementFormat.Vector3, VertexElementUsage.Binormal, 0),
            new VertexElement(40,  VertexElementFormat.Vector3, VertexElementUsage.Tangent, 0),
            new VertexElement(52,  VertexElementFormat.Byte4,   VertexElementUsage.BlendIndices, 0),
            new VertexElement(56,  VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0),
            new VertexElement(72,  VertexElementFormat.Single,  VertexElementUsage.PointSize, 0),
            new VertexElement(76,  VertexElementFormat.Single,  VertexElementUsage.Fog, 0),
            new VertexElement(80,  VertexElementFormat.Single,  VertexElementUsage.Depth, 0),
            new VertexElement(84,  VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(92,  VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 3),
            new VertexElement(100, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 1),
            new VertexElement(116, VertexElementFormat.Color,   VertexElementUsage.Color, 0));
    }

    private static SemanticVertex[] Quad()
    {
        uint indices = (uint)(Indices[0] | Indices[1] << 8 | Indices[2] << 16 | Indices[3] << 24);
        SemanticVertex At(float x, float y) => new()
        {
            Position = new Vector4(x, y, 0, 1),
            Normal = Normal, Binormal = Binormal, Tangent = Tangent,
            Indices = indices, Weights = Weights,
            PSize = PSize, Fog = Fog, Depth = Depth,
            Uv0 = Uv0, Uv3 = Uv3, Weights1 = Weights1, Color = VertexColor,
        };
        return [At(-1, 1), At(1, 1), At(-1, -1), At(1, -1)];
    }

    private sealed class ProbeGame : Game
    {
        private readonly IReadOnlyList<Job> _jobs;
        private bool _done;

        public List<JobResult> Results { get; } = [];

        public string GlVendor { get; private set; } = "(unknown)";
        public string GlRenderer { get; private set; } = "(unknown)";
        public string GlVersion { get; private set; } = "(unknown)";

        /// <summary>
        /// The driver's GLSL front end is one that refuses a non-square matrix type in the
        /// versionless dialect: Mesa (llvmpipe/softpipe and its hardware drivers), Apple's, ANGLE.
        /// Decided from the GL strings before anything is drawn, never from a refusal.
        /// </summary>
        public bool StrictDriver { get; private set; }

        public ProbeGame(IReadOnlyList<Job> jobs)
        {
            _jobs = jobs;
            _ = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = Size,
                PreferredBackBufferHeight = Size,
                GraphicsProfile = GraphicsProfile.HiDef,
            };
            Window.Title = "ShadowDusk vertex semantics (headless)";
        }

        protected override void Draw(GameTime gameTime)
        {
            if (_done) { Exit(); return; }
            _done = true;
            try { RunAll(); }
            catch (Exception ex) { Results.Add(new JobResult("FATAL", false, ex.ToString())); }
            Exit();
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint GlGetStringFn(uint name);

        /// <summary>
        /// The current context's GL strings, read straight from the platform GL library (the
        /// runtime's context is current on this thread during Draw).
        /// </summary>
        private void ReadGlStrings()
        {
            const uint GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01, GL_VERSION = 0x1F02;
            string[] libs = OperatingSystem.IsWindows() ? ["opengl32.dll"]
                : OperatingSystem.IsMacOS() ? ["/System/Library/Frameworks/OpenGL.framework/OpenGL"]
                : ["libGL.so.1", "libGL.so", "libOpenGL.so.0"];
            foreach (string lib in libs)
            {
                if (!NativeLibrary.TryLoad(lib, out nint handle)) continue;
                if (!NativeLibrary.TryGetExport(handle, "glGetString", out nint fn)) continue;
                var glGetString = Marshal.GetDelegateForFunctionPointer<GlGetStringFn>(fn);
                GlVendor = Marshal.PtrToStringAnsi(glGetString(GL_VENDOR)) ?? "(null)";
                GlRenderer = Marshal.PtrToStringAnsi(glGetString(GL_RENDERER)) ?? "(null)";
                GlVersion = Marshal.PtrToStringAnsi(glGetString(GL_VERSION)) ?? "(null)";
                break;
            }
            string all = $"{GlVendor} {GlRenderer} {GlVersion}";
            StrictDriver = new[] { "Mesa", "llvmpipe", "softpipe", "Apple", "ANGLE" }
                .Any(k => all.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private void RunAll()
        {
            ReadGlStrings();
            using var texture = new Texture2D(GraphicsDevice, 1, 1);
            texture.SetData([new Color(200, 150, 100, 50)]);

            foreach (Job job in _jobs)
            {
                if (job.Candidate.Bytes is null)
                {
                    Results.Add(new JobResult(job.Name, false, "compile failed: " + job.Candidate.Error));
                    continue;
                }
                Color[] candidate;
                try
                {
                    candidate = Render(job.Candidate.Bytes, job, texture);
                }
                catch (Exception ex)
                {
                    try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
                    if (job.Control == ControlKind.WrongPacking && StrictDriver
                        && ex.Message.Contains(StrictLinkRefusalSignature, StringComparison.Ordinal))
                    {
                        // A wrong layout whose VS no longer writes a varying the PS reads: a strict
                        // linker refuses the program, so it cannot render like mgfxc's.
                        Results.Add(new JobResult(job.Name, true,
                            $"CONTROL-DETECTED (strict driver: {GlRenderer}): the linker refused the wrong layout: " +
                            $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ").Trim()}"));
                    }
                    else
                    {
                        Results.Add(new JobResult(job.Name, false,
                            $"render threw: {ex.GetType().Name}: {ex.Message}")
                        {
                            PackingStem = job.PackingStem,
                        });
                    }
                    continue;
                }
                try
                {
                    Color c = candidate[(Size / 2) * Size + Size / 2];
                    var notes = new List<string> { $"centre=({c.R},{c.G},{c.B},{c.A})" };
                    bool ok = true;

                    if (job.Expected is { } e)
                    {
                        Color want = new(e);
                        bool match = Near(c, want, 2);
                        notes.Add($"expected ({want.R},{want.G},{want.B},{want.A}) +-2");
                        if (job.Control == ControlKind.WrongElement)
                        {
                            ok = !match;
                            notes.Add(match ? "CONTROL NOT DETECTED: a relabelled record still drew the binormal"
                                            : "CONTROL-DETECTED: the relabelled record bound the wrong element");
                        }
                        else
                        {
                            ok &= match;
                        }
                    }

                    if (job.Golden is not null)
                    {
                        Color[] golden = Render(job.Golden, job, texture);
                        int maxd = 0;
                        for (int i = 0; i < golden.Length; i++)
                            maxd = Math.Max(maxd, Math.Max(Math.Max(Math.Abs(golden[i].R - candidate[i].R), Math.Abs(golden[i].G - candidate[i].G)),
                                                           Math.Max(Math.Abs(golden[i].B - candidate[i].B), Math.Abs(golden[i].A - candidate[i].A))));
                        Color g = golden[(Size / 2) * Size + Size / 2];
                        notes.Add($"mgfxc centre=({g.R},{g.G},{g.B},{g.A}) maxd vs mgfxc {maxd}");
                        if (job.Control == ControlKind.WrongPacking)
                        {
                            ok = maxd > 1;
                            notes.Add(ok ? "CONTROL-DETECTED: the wrong layout differs from mgfxc's"
                                         : "CONTROL NOT DETECTED: the wrong layout still matched the golden");
                        }
                        else
                        {
                            ok &= maxd <= 1;
                        }
                        if (job.Expected is { } e2 && !Near(g, new Color(e2), 2))
                        {
                            notes.Add("mgfxc build does not show the expected element either");
                            ok = false;
                        }
                        // A vacuous pass: both builds drawing nothing (or leaving the magenta clear).
                        // A WrongPacking control that drew nothing would "diverge" vacuously, so it is checked too.
                        if (job.Expected is null && job.Control != ControlKind.WrongElement
                            && candidate.All(px => (px.A == 0 && px.R == 0) || px == new Color(255, 0, 255, 255)))
                        {
                            notes.Add("nothing drawn");
                            ok = false;
                        }
                    }

                    Results.Add(new JobResult(job.Name, ok, string.Join("; ", notes))
                    {
                        PackingStem = job.PackingStem, Rendered = true,
                    });
                }
                catch (Exception ex)
                {
                    try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
                    Results.Add(new JobResult(job.Name, false, $"render threw: {ex.GetType().Name}: {ex.Message}"));
                }
            }
        }

        private static bool Near(Color a, Color b, int tol)
            => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol && Math.Abs(a.A - b.A) <= tol;

        private Color[] Render(byte[] bytes, Job job, Texture2D texture)
        {
            using var effect = new Effect(GraphicsDevice, bytes);
            if (job.Technique is not null)
                effect.CurrentTechnique = effect.Techniques[job.Technique]
                    ?? throw new InvalidOperationException($"technique '{job.Technique}' not found");
            if (job.Selector is not null)
                effect.Parameters[job.Selector]!.SetValue(job.SelectorValue);
            effect.Parameters["World"]?.SetValue(Matrix.Identity);
            effect.Parameters["View"]?.SetValue(Matrix.Identity);
            effect.Parameters["Projection"]?.SetValue(Matrix.Identity);
            effect.Parameters["AlbedoMap"]?.SetValue(texture);
            effect.Parameters["GlossMap"]?.SetValue(texture);

            // A fresh target per draw, as the other probes do (DesktopVK layout rule; harmless on GL).
            using var rt = new RenderTarget2D(GraphicsDevice, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(new Color(255, 0, 255, 255));   // magenta: an unwritten pixel is obvious
            GraphicsDevice.BlendState = BlendState.Opaque;
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            GraphicsDevice.SamplerStates[0] = SamplerState.PointClamp;
            GraphicsDevice.SamplerStates[1] = SamplerState.PointClamp;

            SemanticVertex[] verts = Quad();
            short[] indices = [0, 1, 2, 2, 1, 3];
            // The control gets a declaration of its own too: its attribute-binding cache is per
            // declaration instance (see RelabelAttribute).
            VertexDeclaration declaration = job.Control != ControlKind.None
                ? new VertexDeclaration(SemanticVertex.Declaration.GetVertexElements())
                : SemanticVertex.Declaration;
            foreach (EffectPass pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                GraphicsDevice.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, 2, declaration);
            }
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
